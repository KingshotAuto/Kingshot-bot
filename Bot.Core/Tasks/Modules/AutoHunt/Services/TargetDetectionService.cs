using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using Bot.Core.Tasks.Modules.AutoHunt.Services;
using Bot.Core.ImageDetection;
using Bot.Core.Tasks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Simplified service for detecting and prioritizing hunt targets
    /// </summary>
    public class TargetDetectionService : ITargetDetectionService
    {
        private readonly UnifiedTemplateMatchingService _templateMatcher;
        private readonly string _imageTemplateFolder;
        private readonly IHuntModeNavigationService _huntModeNavigation;
        private readonly IAutoHuntVisualDebugger _visualDebugger;

        public TargetDetectionService(
            UnifiedTemplateMatchingService templateMatcher,
            IHuntModeNavigationService huntModeNavigation,
            IAutoHuntVisualDebugger visualDebugger)
        {
            _templateMatcher = templateMatcher;
            _huntModeNavigation = huntModeNavigation;
            _visualDebugger = visualDebugger;
            _imageTemplateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images");
        }

        public async Task<List<HuntTarget>> DetectAllTargetsAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken)
        {
            var targets = new List<HuntTarget>();
            var bestConfidences = new Dictionary<string, List<(Rectangle location, double confidence)>>();

            // Get account ID for this account
            var accountId = await GetAccountIdAsync(account, logger);
            
            // Log current blocked areas for this account
            var settings = GetAutoHuntSettings(account);
            var blockedAreas = settings.GetUsedTargetAreas(accountId);
            LogBlockedAreas(account, logger, accountId, blockedAreas);

            // First check if we're in hunt mode
            logger.LogInfo($"[{account.AccountName}] Waiting up to 5 seconds for hunt mode indicators...");
            bool huntModeFound = await WaitForHuntMode(account, logger, cancellationToken);

            if (!huntModeFound)
            {
                logger.LogInfo($"[{account.AccountName}] No hunt mode indicators found (stamina or compass), skipping target detection");
                return targets;
            }

            logger.LogInfo($"[{account.AccountName}] Found hunt mode indicators, processing targets...");

            // Process ticks first if we're in hunt mode
            bool foundAnyTick = await ProcessTicks(account, logger, cancellationToken, accountId, settings);

            // Scan for targets
            await ScanForTargets(account, logger, cancellationToken, bestConfidences, huntModeFound);

            // Process the best detections
            targets = ProcessBestDetections(account, logger, bestConfidences, settings, accountId);

            // Handle case where no targets found
            if (!targets.Any())
            {
                await HandleNoTargetsFound(account, logger, cancellationToken, bestConfidences, foundAnyTick);
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] Found {targets.Count} valid targets after scanning");
            }

            // Create visual blocking debug log
            await CreateDebugLog(account, logger, targets, settings, accountId);

            return targets;
        }

        public HuntTarget? PrioritizeTarget(
            List<HuntTarget> targets,
            TargetPrioritizationContext context,
            out bool noMarchesAvailable)
        {
            noMarchesAvailable = false;
            bool foundTargetButNoMarches = false;

            // Select targets by highest confidence instead of fixed priority order
            var targetsOrderedByConfidence = targets.OrderByDescending(t => t.Confidence).ToList();

            foreach (var target in targetsOrderedByConfidence)
            {
                // Check if target is blocked by session state
                if (IsTargetBlockedBySessionState(target, context.SessionState))
                {
                    continue;
                }

                // Check march requirements
                if (target.RequiresMarch && context.AvailableMarches <= 0)
                {
                    foundTargetButNoMarches = true;
                    continue;
                }

                return target;
            }

            noMarchesAvailable = foundTargetButNoMarches;
            return null;
        }

        public bool IsTargetAreaBlocked(HuntTarget target, string accountId)
        {
            // This would need access to settings - might need to inject ConfigurationManager
            // For now, return false - this logic will be handled by the coordinator
            return false;
        }

        private async Task<string> GetAccountIdAsync(AccountSettings account, LogService logger)
        {
            // Use account name as ID since AccountDetectionTask is not available
            return $"{account.InstanceNumber}_{account.AccountName}";
        }

        private AutoHuntSettings GetAutoHuntSettings(AccountSettings account)
        {
            var configManager = ConfigurationManager.Instance;
            var config = configManager.GetConfig();
            
            if (!account.TaskSettings.TryGetValue("AutoHunt", out var settingsJson) || string.IsNullOrEmpty(settingsJson))
            {
                return new AutoHuntSettings();
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AutoHuntSettings>(settingsJson) ?? new AutoHuntSettings();
            }
            catch (Exception)
            {
                return new AutoHuntSettings();
            }
        }

        private void SaveAutoHuntSettings(AccountSettings account, AutoHuntSettings settings)
        {
            var configManager = ConfigurationManager.Instance;
            var settingsJson = System.Text.Json.JsonSerializer.Serialize(settings);
            account.TaskSettings["AutoHunt"] = settingsJson;
            // Configuration is auto-saved via ConfigurationManager.Instance
        }

        private void LogBlockedAreas(AccountSettings account, LogService logger, string accountId, List<Rectangle> blockedAreas)
        {
            if (blockedAreas.Any())
            {
                logger.LogInfo($"[{account.AccountName}] Currently blocked areas for account {accountId}: {blockedAreas.Count} areas");
                foreach (var area in blockedAreas)
                {
                    logger.LogInfo($"[{account.AccountName}] -> Blocked: {area}");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] No blocked areas for account {accountId}");
            }
        }

        private async Task<bool> WaitForHuntMode(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            bool huntModeFound = false;
            DateTime startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !huntModeFound && !cancellationToken.IsCancellationRequested)
            {
                huntModeFound = await _huntModeNavigation.IsInHuntModeAsync(account, logger);
                if (!huntModeFound)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }

            return huntModeFound;
        }

        private async Task<bool> ProcessTicks(AccountSettings account, LogService logger, CancellationToken cancellationToken, 
            string accountId, AutoHuntSettings settings)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for ticks to process (scanning up to 3 seconds)...");
            bool foundTick;
            bool foundAnyTick = false;
            DateTime tickScanStart = DateTime.UtcNow;
            TimeSpan timeSinceLastTick = TimeSpan.Zero;

            do
            {
                foundTick = false;
                var tickScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (tickScreenshot == null) break;

                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    tickScreenshot,
                    Path.Combine(_imageTemplateFolder, "tick.png"),
                    account.InstanceNumber,
                    threshold: 0.8,
                    searchArea: AutoHuntConstants.TargetSearchArea
                );

                if (found)
                {
                    foundTick = true;
                    foundAnyTick = true;
                    await ProcessTickFound(account, logger, matchRect, confidence, accountId, settings, cancellationToken);
                    await Task.Delay(500, cancellationToken);
                    tickScanStart = DateTime.UtcNow;
                }
                else
                {
                    await Task.Delay(200, cancellationToken);
                }

                timeSinceLastTick = DateTime.UtcNow - tickScanStart;
            } while ((foundTick || timeSinceLastTick.TotalSeconds < 1.5) && !cancellationToken.IsCancellationRequested);

            if (!foundTick)
            {
                logger.LogInfo($"[{account.AccountName}] No ticks found after scanning for {timeSinceLastTick.TotalSeconds:F1} seconds");
            }

            return foundAnyTick;
        }

        private async Task ProcessTickFound(AccountSettings account, LogService logger, Rectangle matchRect, 
            double confidence, string accountId, AutoHuntSettings settings, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Found tick at {matchRect} with confidence {confidence:F3}");

            // Click the tick
            if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
            {
                logger.LogInfo($"[{account.AccountName}] Successfully clicked tick");
                await Task.Delay(500, cancellationToken);
                
                // Remove overlapping used areas
                RemoveOverlappingUsedAreas(account, logger, matchRect, accountId, settings);
                
                // Click the confirm point
                if (await ClickAsync(account.InstanceNumber, logger, AutoHuntConstants.TickConfirmPoint))
                {
                    logger.LogInfo($"[{account.AccountName}] Clicked tick confirm point");
                }
            }
        }

        private void RemoveOverlappingUsedAreas(AccountSettings account, LogService logger, Rectangle matchRect, 
            string accountId, AutoHuntSettings settings)
        {
            var tickArea = new Rectangle(
                matchRect.X - AutoHuntConstants.TARGET_AREA_PADDING,
                matchRect.Y - AutoHuntConstants.TARGET_AREA_PADDING,
                matchRect.Width + (AutoHuntConstants.TARGET_AREA_PADDING * 2),
                matchRect.Height + (AutoHuntConstants.TARGET_AREA_PADDING * 2)
            );

            var usedAreas = settings.GetUsedTargetAreas(accountId);
            var overlappingAreas = usedAreas.Where(used => 
                !(tickArea.Left > used.Right || 
                  tickArea.Right < used.Left || 
                  tickArea.Top > used.Bottom || 
                  tickArea.Bottom < used.Top)).ToList();

            foreach (var area in overlappingAreas)
            {
                settings.RemoveUsedTargetArea(accountId, area);
                logger.LogInfo($"[{account.AccountName}] Removed used area restriction at {area} due to tick click");
            }
            
            SaveAutoHuntSettings(account, settings);
        }

        private async Task ScanForTargets(AccountSettings account, LogService logger, CancellationToken cancellationToken,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences, bool huntModeFound)
        {
            logger.LogInfo($"[{account.AccountName}] Finished processing ticks, now scanning for targets (up to 5 seconds)...");

            var targetTypes = new[]
            {
                ("king.png", true),
                ("bear.png", true),
                ("scout.png", false),
                ("attack.png", false)
            };

            DateTime startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;

                foreach (var (imageName, requiresMarch) in targetTypes)
                {
                    // Skip certain targets if not in hunt mode
                    if (!huntModeFound && (imageName == "king.png" || imageName == "scout.png" || imageName == "attack.png"))
                    {
                        continue;
                    }

                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        Path.Combine(_imageTemplateFolder, imageName),
                        account.InstanceNumber,
                        threshold: 0.68,
                        searchArea: AutoHuntConstants.TargetSearchArea
                    );

                    // Track all detections with confidence > 0.5
                    if (confidence > 0.5)
                    {
                        if (!bestConfidences.ContainsKey(imageName))
                        {
                            bestConfidences[imageName] = new List<(Rectangle, double)>();
                        }

                        // Check if we already have a similar location
                        bool isDuplicate = bestConfidences[imageName].Any(existing =>
                            Math.Abs(existing.location.X - matchRect.X) < AutoHuntConstants.TARGET_AREA_PADDING &&
                            Math.Abs(existing.location.Y - matchRect.Y) < AutoHuntConstants.TARGET_AREA_PADDING);

                        if (!isDuplicate)
                        {
                            bestConfidences[imageName].Add((matchRect, confidence));
                            logger.LogInfo($"[{account.AccountName}] Found potential {imageName} at {matchRect} with confidence {confidence:F3}");
                        }
                    }
                }

                // If we have found at least one high-confidence target, we can break early
                if (bestConfidences.Any(kv => kv.Value.Any(v => v.confidence >= 0.68)))
                {
                    break;
                }

                await Task.Delay(200, cancellationToken);
            }
        }

        private List<HuntTarget> ProcessBestDetections(AccountSettings account, LogService logger,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences,
            AutoHuntSettings settings, string accountId)
        {
            var targets = new List<HuntTarget>();
            var targetTypes = new[]
            {
                ("king.png", true),
                ("bear.png", true),
                ("scout.png", false),
                ("attack.png", false)
            };

            foreach (var (imageName, detections) in bestConfidences)
            {
                var sortedDetections = detections.OrderByDescending(d => d.confidence).ToList();

                foreach (var (matchRect, confidence) in sortedDetections)
                {
                    if (confidence < 0.68) continue;

                    var targetArea = new Rectangle(
                        matchRect.X - AutoHuntConstants.TARGET_AREA_PADDING,
                        matchRect.Y - AutoHuntConstants.TARGET_AREA_PADDING,
                        matchRect.Width + (AutoHuntConstants.TARGET_AREA_PADDING * 2),
                        matchRect.Height + (AutoHuntConstants.TARGET_AREA_PADDING * 2)
                    );

                    var usedAreas = settings.GetUsedTargetAreas(accountId);
                    bool areaAlreadyUsed = IsAreaAlreadyUsed(targetArea, usedAreas);

                    if (!areaAlreadyUsed)
                    {
                        logger.LogInfo($"[{account.AccountName}] Adding valid {imageName} at {matchRect} with confidence {confidence:F3}");
                        targets.Add(new HuntTarget
                        {
                            Type = imageName.Replace(".png", ""),
                            RequiresMarch = targetTypes.First(t => t.Item1 == imageName).Item2,
                            MatchLocation = matchRect,
                            TargetArea = targetArea,
                            Confidence = confidence
                        });
                    }
                    else
                    {
                        LogBlockedTarget(account, logger, imageName, matchRect, targetArea, usedAreas, accountId);
                    }
                }
            }

            return targets;
        }

        private bool IsAreaAlreadyUsed(Rectangle targetArea, List<Rectangle> usedAreas)
        {
            return usedAreas.Any(used => 
                !(targetArea.Left > used.Right || 
                  targetArea.Right < used.Left || 
                  targetArea.Top > used.Bottom || 
                  targetArea.Bottom < used.Top));
        }

        private void LogBlockedTarget(AccountSettings account, LogService logger, string imageName, 
            Rectangle matchRect, Rectangle targetArea, List<Rectangle> usedAreas, string accountId)
        {
            logger.LogInfo($"[{account.AccountName}] BLOCKED: Skipping {imageName} at {matchRect} (blocked area: {targetArea}) - overlaps with previously clicked area for account {accountId}");
            
            var overlappingArea = usedAreas.FirstOrDefault(used => 
                !(targetArea.Left > used.Right || 
                  targetArea.Right < used.Left || 
                  targetArea.Top > used.Bottom || 
                  targetArea.Bottom < used.Top));
            
            if (overlappingArea != Rectangle.Empty)
            {
                logger.LogInfo($"[{account.AccountName}] -> Overlaps with used area: {overlappingArea}");
            }
        }

        private async Task HandleNoTargetsFound(AccountSettings account, LogService logger, CancellationToken cancellationToken,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences, bool foundAnyTick)
        {
            // Log potential targets that didn't meet the threshold
            var lowConfidenceTargets = bestConfidences
                .SelectMany(kv => kv.Value.Select(v => (type: kv.Key, location: v.location, confidence: v.confidence)))
                .OrderByDescending(t => t.confidence)
                .ToList();

            if (lowConfidenceTargets.Any())
            {
                logger.LogInfo($"[{account.AccountName}] No valid targets found, but detected these potential targets (confidence < 0.68):");
                foreach (var (type, location, confidence) in lowConfidenceTargets)
                {
                    logger.LogInfo($"[{account.AccountName}] - {type} at {location} with confidence {confidence:F3}");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] No targets detected at all during scanning period");
            }

            // If no targets found and no ticks were processed, check for level-up
            if (!foundAnyTick)
            {
                await _huntModeNavigation.HandleLevelUpPopupAsync(account, logger);
            }
        }

        private async Task CreateDebugLog(AccountSettings account, LogService logger, List<HuntTarget> targets, 
            AutoHuntSettings settings, string accountId)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting visual blocking debug log creation...");
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null)
                {
                    logger.LogInfo($"[{account.AccountName}] Screenshot captured ({screenshot.Length} bytes), creating target info...");
                    var targetInfos = targets.Select(t => new TargetInfo
                    {
                        Type = t.Type,
                        TargetArea = t.TargetArea,
                        Confidence = t.Confidence
                    }).ToList();

                    var blockedAreasForVisual = settings.GetUsedTargetAreas(accountId).ToList();
                    var action = targets.Any() ? $"found_{targets.Count}_targets" : "no_targets_found";
                    logger.LogInfo($"[{account.AccountName}] Creating visual log: {targetInfos.Count} targets, {blockedAreasForVisual.Count} blocked areas, action: {action}");
                    
                    _ = Task.Run(async () =>
                    {
                        await _visualDebugger.CreateVisualBlockingLogAsync(screenshot, account, logger, targetInfos, blockedAreasForVisual, action);
                    });
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] Screenshot was null, skipping visual blocking log");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{account.AccountName}] Failed to create visual blocking log: {ex.Message}");
            }
        }

        private bool IsTargetBlockedBySessionState(HuntTarget target, AutoHuntSessionState sessionState)
        {
            return (target.Type == "king" && !sessionState.CanAttackKing) ||
                   (target.Type == "bear" && !sessionState.CanAttackBear) ||
                   (target.Type == "attack" && !sessionState.CanAttackAttack);
        }

        // Helper methods that would need to be injected or moved to base class
        private async Task<byte[]?> TakeScreenshotAsync(int instanceNumber, LogService logger)
        {
            // This would need to be injected from BaseTaskWithCommonPatterns
            // For now, return null - this will be handled by the coordinator
            return null;
        }

        private async Task<bool> ClickRandomInRectAsync(int instanceNumber, LogService logger, Rectangle rect)
        {
            // This would need to be injected from BaseTaskWithCommonPatterns
            return false;
        }

        private async Task<bool> ClickAsync(int instanceNumber, LogService logger, Point point)
        {
            // This would need to be injected from BaseTaskWithCommonPatterns
            return false;
        }
    }
}