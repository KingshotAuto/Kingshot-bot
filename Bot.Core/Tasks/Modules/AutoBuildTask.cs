using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Bot.Core.Tasks.Modules
{
    public class AutoBuildTask : BaseTaskWithCommonPatterns
    {
        private const int DEFAULT_WAIT_TIMEOUT_MS = 5000; // 5 seconds timeout
        private const int DETECTION_POLL_INTERVAL_MS = 200; // Check every 200ms
        private const int MIN_CLICK_DELAY_MS = 500; // Minimum delay between clicks
        private const int MAX_CLICK_DELAY_MS = 1000; // Maximum delay between clicks
        private const int MIN_UPGRADE_ICON_DELAY_MS = 1500; // Minimum delay before upgrade icon
        private const int MAX_UPGRADE_ICON_DELAY_MS = 2000; // Maximum delay before upgrade icon
        private static readonly Random _random = new Random();

        public override TaskType TaskType => TaskType.AutoBuild;
        public override string Name => "Auto Build";
        
        protected override string GetImageFolderName() => "autobuild";

        private readonly Rectangle[] idleCheckRegions = new[]
        {
            new Rectangle(86, 370, 237, 34),
            new Rectangle(90, 445, 233, 34)
        };

        private readonly Rectangle timerRegion = new Rectangle(450, 1000, 126, 99);
        private readonly Rectangle redXSearchArea = new Rectangle(449, 589, 211, 543);
        private readonly Rectangle goButtonSearchArea = new Rectangle(527, 588, 159, 547);
        private readonly Rectangle personHeadArea = new Rectangle(254, 5, 48, 47);
        private readonly Rectangle kitchenArea = new Rectangle(39, 967, 92, 92);
        private readonly Rectangle upgradeIconArea = new Rectangle(129, 565, 474, 338);
        private readonly Rectangle upgradeButtonArea = new Rectangle(391, 930, 245, 63); // For final upgrade button
        private readonly Rectangle firstUpgradeButtonArea = new Rectangle(538, 601, 136, 43); // For first upgrade button after kitchen

        // Helper method for random delays
        private async Task RandomDelay(CancellationToken cancellationToken)
        {
            await WaitIfPausedAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            int delay = _random.Next(MIN_CLICK_DELAY_MS, MAX_CLICK_DELAY_MS + 1);
            await Task.Delay(delay, cancellationToken);
        }

        private async Task LongerRandomDelay(CancellationToken cancellationToken)
        {
            await WaitIfPausedAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            int delay = _random.Next(MIN_UPGRADE_ICON_DELAY_MS, MAX_UPGRADE_ICON_DELAY_MS + 1);
            await Task.Delay(delay, cancellationToken);
        }

        /// <summary>
        /// Enhanced version of FindAndClickImageAsync with debug logging and visualization
        /// </summary>
        private async Task<bool> FindAndClickImageWithDebugAsync(
            string imageName, 
            int instanceNumber, 
            LogService logger, 
            Rectangle? searchArea = null, 
            double threshold = 0.8,
            bool saveFailedMatches = true,
            int timeoutMs = DEFAULT_WAIT_TIMEOUT_MS)
        {
            var startTime = DateTime.UtcNow;
            logger.LogInfo($"[{Name}] Looking for {imageName} (timeout: {timeoutMs}ms)" + (searchArea.HasValue ? $" in area: {searchArea}" : ""));

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) continue;

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"[{Name}] Template not found: {templatePath}");
                    return false;
                }

                // Track all detections with confidence > 0.4 for debugging
                var detections = new List<(Rectangle rect, double confidence)>();

                // First try with high threshold
                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: threshold,
                    searchArea: searchArea
                );

                // Special debug for upgrade button
                if (imageName == "upgrade-button.png")
                {
                    logger.LogInfo($"[{Name}] Upgrade button detection - Found: {found}, Confidence: {confidence:F3}, Location: {matchRect}");
                    if (found)
                    {
                        var clickPoint = new Point(matchRect.X + matchRect.Width / 2, matchRect.Y + matchRect.Height / 2);
                        logger.LogInfo($"[{Name}] Will click upgrade button at center point: ({clickPoint.X}, {clickPoint.Y})");
                    }
                }

                // If high threshold fails, try with lower threshold for debugging
                if (!found && saveFailedMatches)
                {
                    var (_, lowMatchRect, lowConfidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        templatePath,
                        instanceNumber,
                        threshold: 0.4,
                        searchArea: searchArea
                    );

                    if (lowConfidence > 0.4)
                    {
                        detections.Add((lowMatchRect, lowConfidence));
                        logger.LogInfo($"[{Name}] Found potential {imageName} match with lower confidence {lowConfidence:F3} at {lowMatchRect}");
                    }
                }

                if (found)
                {
                    logger.LogInfo($"[{Name}] Found {imageName} at {matchRect} with confidence {confidence:F3} after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                    
                    // Debug image saving disabled

                    return await ClickRandomInRectAsync(instanceNumber, logger, matchRect);
                }

                // If we haven't found it yet and we still have time, wait a bit before next attempt
                if ((DateTime.UtcNow - startTime).TotalMilliseconds + DETECTION_POLL_INTERVAL_MS < timeoutMs)
                {
                    await Task.Delay(DETECTION_POLL_INTERVAL_MS);
                    continue;
                }

                // Debug screenshot saving disabled
                break;
            }

            logger.LogInfo($"[{Name}] Failed to find {imageName} after {timeoutMs}ms");
            return false;
        }

        /// <summary>
        /// Enhanced version of WaitForImageAsync with debug logging and visualization
        /// </summary>
        private async Task<bool> WaitForImageWithDebugAsync(
            string imageName, 
            int instanceNumber, 
            LogService logger, 
            CancellationToken cancellationToken,
            Rectangle? searchArea = null,
            int timeoutMs = DEFAULT_WAIT_TIMEOUT_MS,
            double threshold = 0.8)
        {
            var startTime = DateTime.UtcNow;
            logger.LogInfo($"[{Name}] Waiting for {imageName} (timeout: {timeoutMs}ms)...");
            double bestConfidenceSeen = 0.0;

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) continue;

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"[{Name}] Template not found: {templatePath}");
                    return false;
                }

                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: threshold,
                    searchArea: searchArea
                );

                // Track the best confidence we've seen
                if (confidence > bestConfidenceSeen)
                {
                    bestConfidenceSeen = confidence;
                }

                if (found)
                {
                    logger.LogInfo($"[{Name}] Found {imageName} at {matchRect} with confidence {confidence:F3} after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                    return true;
                }

                // If we haven't found it yet and we still have time, wait a bit before next attempt
                if ((DateTime.UtcNow - startTime).TotalMilliseconds + DETECTION_POLL_INTERVAL_MS < timeoutMs)
                {
                    await Task.Delay(DETECTION_POLL_INTERVAL_MS, cancellationToken);
                    continue;
                }

                // Continue waiting if we haven't reached timeout
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInfo($"[{Name}] Wait for {imageName} cancelled after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms (best confidence: {bestConfidenceSeen:F3})");
            }
            else
            {
                logger.LogInfo($"[{Name}] Failed to find {imageName} after {timeoutMs}ms - best confidence: {bestConfidenceSeen:F3} (threshold: {threshold:F1})");
            }
            return false;
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Ensure in base view
                NotifyProgress(userNotifications, "Navigating to base", 10);
                var locator = new LocatorService(logger, account);
                if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                {
                    return FailNavigation("Step 1: Navigating to base view", "Could not reach base view");
                }

                // Click alliance button
                await ClickAsync(account.InstanceNumber, logger, new Point(12, 549));
                await RandomDelay(cancellationToken);

                // Verify side menu
                NotifyProgress(userNotifications, "Opening side menu", 20);
                var sideMenuFound = await WaitForImageWithDebugAsync("side-menu.png", account.InstanceNumber, logger, cancellationToken);
                if (!sideMenuFound)
                {
                    return FailDetection("Step 2: Opening side menu", "side menu");
                }
                await RandomDelay(cancellationToken);

                // Check idle status
                NotifyProgress(userNotifications, "Checking builders", 30);
                if (!await CheckIdleStatus(account, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] No idle builders found, clicking exit (461, 542)");
                    await ClickAsync(account.InstanceNumber, logger, new Point(461, 542));
                    await RandomDelay(cancellationToken);
                    return TaskExecutionDetails.FailedWith(
                        FailureCategory.GameState,
                        "Step 3: Checking builders",
                        "No idle builders available",
                        customHint: "All builders are busy - task will retry next cycle");
                }

                // Navigate to kitchen
                NotifyProgress(userNotifications, "Finding kitchen", 50);
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                if (!await NavigateToKitchen(account, logger, cancellationToken))
                {
                    return FailNavigation("Step 4: Finding kitchen", "Could not locate kitchen building");
                }

                // Handle upgrade process
                NotifyProgress(userNotifications, "Starting upgrade", 70);
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                if (!await HandleUpgradeProcess(account, logger, cancellationToken))
                {
                    return FailGameState("Step 5: Starting upgrade", "Upgrade dialog not responding", recoveryNeeded: true);
                }

                // Update last build time
                account.AutoBuildSettings.LastBuildTime = DateTime.UtcNow;
                return TaskExecutionDetails.Succeeded();
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] AutoBuild task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in AutoBuild: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }

        private async Task<bool> CheckIdleStatus(AccountSettings account, LogService logger)
        {
            var ocrConfig = new OCRConfiguration 
            { 
                ScaleFactor = 2.0f,
                CharacterWhitelist = "idleIDLE"
            };
            
            using var ocrService = new OCRService(logger, ocrConfig);
            
            foreach (var region in idleCheckRegions)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;

                var text = ocrService.ExtractTextFromScreenArea(screenshot, region);
                logger.LogInfo($"[{account.AccountName}] OCR Result for region {region}: '{text}'");
                
                if (text.ToLower().Contains("idle"))
                    return true;
            }
            
            return false;
        }

        private async Task<bool> NavigateToKitchen(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Attempting to navigate to kitchen");
            logger.LogInfo($"[{account.AccountName}] Searching for person head in area: {personHeadArea}");
            
            // Click person head
            if (!await FindAndClickImageWithDebugAsync("person-head.png", account.InstanceNumber, logger, searchArea: personHeadArea))
            {
                logger.LogError($"[{account.AccountName}] Person head icon not found in area {personHeadArea}");
                return false;
            }
            await RandomDelay(cancellationToken);

            logger.LogInfo($"[{account.AccountName}] Searching for kitchen in area: {kitchenArea}");
            
            // Click kitchen
            if (!await FindAndClickImageWithDebugAsync("kitchen.png", account.InstanceNumber, logger, searchArea: kitchenArea))
            {
                logger.LogError($"[{account.AccountName}] Kitchen icon not found in area {kitchenArea}");
                return false;
            }
            await RandomDelay(cancellationToken);

            // Click sequence
            logger.LogInfo($"[{account.AccountName}] Executing click sequence");
            await ClickAsync(account.InstanceNumber, logger, new Point(245, 902));
            await RandomDelay(cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(363, 299));
            await RandomDelay(cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(635, 757));
            await RandomDelay(cancellationToken);

            return true;
        }

        private async Task<bool> HandleUpgradeProcess(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Starting upgrade process");
            
            // Click first upgrade button (after kitchen)
            logger.LogInfo($"[{account.AccountName}] Looking for first upgrade button in area: {firstUpgradeButtonArea}");
            if (!await FindAndClickImageWithDebugAsync("upgrade-button.png", account.InstanceNumber, logger, searchArea: firstUpgradeButtonArea))
            {
                logger.LogError($"[{account.AccountName}] First upgrade button not found in area {firstUpgradeButtonArea}");
                return false;
            }
            await RandomDelay(cancellationToken);

            logger.LogInfo($"[{account.AccountName}] Checking for red X in area: {redXSearchArea}");
            
            // Check for red X (use lower threshold since red X can vary in appearance)
            var redXFound = await WaitForImageWithDebugAsync("red-x.png", account.InstanceNumber, logger, cancellationToken, searchArea: redXSearchArea, threshold: 0.6);

            if (!redXFound)
            {
                logger.LogInfo($"[{account.AccountName}] No red X found, clicking default position (530, 1215)");
                await ClickAsync(account.InstanceNumber, logger, new Point(530, 1215));
                await RandomDelay(cancellationToken);
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] Red X found, searching for go button in area: {goButtonSearchArea}");
                // Red X found - look for go button
                if (!await FindAndClickImageWithDebugAsync("go-button.png", account.InstanceNumber, logger, searchArea: goButtonSearchArea, threshold: 0.8))
                {
                    logger.LogWarning($"[{account.AccountName}] Go button not found in area {goButtonSearchArea}, checking for obtain.png fallback...");
                    
                    // Fallback: Look for and click obtain.png
                    if (await FindAndClickImageWithDebugAsync("obtain.png", account.InstanceNumber, logger, searchArea: goButtonSearchArea, threshold: 0.8, timeoutMs: 2000))
                    {
                        logger.LogInfo($"[{account.AccountName}] Found and clicked obtain.png, clicking again after 0.5s delay and skipping module");
                        await Task.Delay(500, cancellationToken); // 0.5 second delay
                        await FindAndClickImageWithDebugAsync("obtain.png", account.InstanceNumber, logger, searchArea: goButtonSearchArea, threshold: 0.8, timeoutMs: 1000);
                        await RandomDelay(cancellationToken);
                        return true; // Skip the rest of the module
                    }
                    
                    logger.LogError($"[{account.AccountName}] Neither go button nor obtain.png found");
                    return false;
                }
                await RandomDelay(cancellationToken);

                // Click the specified point twice
                var clickPoint = new Point(347, 595);
                logger.LogInfo($"[{account.AccountName}] Clicking additional point after go button {clickPoint} (first click)");
                await ClickAsync(account.InstanceNumber, logger, clickPoint);
                await RandomDelay(cancellationToken);
                
                logger.LogInfo($"[{account.AccountName}] Clicking additional point after go button {clickPoint} (second click)");
                await ClickAsync(account.InstanceNumber, logger, clickPoint);
                await RandomDelay(cancellationToken);
            }

            // Wait longer before looking for upgrade icon
            logger.LogInfo($"[{account.AccountName}] Waiting 1.5-2 seconds before looking for upgrade icon...");
            await LongerRandomDelay(cancellationToken);

            // Look for upgrade icon in the new search area
            if (!await FindAndClickImageWithDebugAsync("upgrade-icon.png", account.InstanceNumber, logger, searchArea: upgradeIconArea))
            {
                logger.LogInfo($"[{account.AccountName}] Upgrade icon not found, checking if troops are training...");
                
                // Try the train icon fallback path
                if (await HandleTrainIconFallback(account, logger, cancellationToken))
                {
                    // After handling train icon, try to find upgrade icon again
                    logger.LogInfo($"[{account.AccountName}] Retrying upgrade icon search after handling training...");
                    await RandomDelay(cancellationToken);
                    
                    if (!await FindAndClickImageWithDebugAsync("upgrade-icon.png", account.InstanceNumber, logger, searchArea: upgradeIconArea))
                    {
                        logger.LogError($"[{account.AccountName}] Upgrade icon still not found after handling training");
                        return false;
                    }
                }
                else
                {
                    logger.LogError($"[{account.AccountName}] Upgrade icon not found in area {upgradeIconArea}");
                    return false;
                }
            }
            await RandomDelay(cancellationToken);

            // Look for final upgrade button in bottom area
            logger.LogInfo($"[{account.AccountName}] Looking for final upgrade button in area: {upgradeButtonArea}");
            if (!await FindAndClickImageWithDebugAsync("upgrade-button.png", account.InstanceNumber, logger, searchArea: upgradeButtonArea))
            {
                logger.LogError($"[{account.AccountName}] Final upgrade button not found in area {upgradeButtonArea}");
                return false;
            }
            await RandomDelay(cancellationToken);

            // Look for alliance help button
            logger.LogInfo($"[{account.AccountName}] Looking for alliance help button...");
            var allianceHelpImagePath = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder) ?? "", "alliance", "alliance-help.png");
            if (File.Exists(allianceHelpImagePath))
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null)
                {
                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        allianceHelpImagePath,
                        account.InstanceNumber,
                        threshold: 0.7,
                        searchArea: null // Search entire screen
                    );
                    
                    if (found)
                    {
                        logger.LogInfo($"[{account.AccountName}] Found alliance help button at {matchRect} with confidence {confidence:F3}, clicking...");
                        await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect);
                        await RandomDelay(cancellationToken);
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] Couldn't find alliance help button after 2 seconds, moving on");
                    }
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Couldn't find alliance help button after 2 seconds, moving on");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] Alliance help template not found at: {allianceHelpImagePath}");
            }

            // Success - no need to look for back button or check upgrade icon again
            return true;
        }

        private async Task<bool> HandleTrainIconFallback(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Looking for train icon...");
                
                // Look for and click train icon
                if (!await FindAndClickImageWithDebugAsync("train-icon.png", account.InstanceNumber, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] Train icon not found, fallback not applicable");
                    return false;
                }
                await RandomDelay(cancellationToken);
                
                // Look for speedup button
                logger.LogInfo($"[{account.AccountName}] Looking for speedup button...");
                if (await WaitForImageWithDebugAsync("speedup-button.png", account.InstanceNumber, logger, cancellationToken, timeoutMs: 3000))
                {
                    logger.LogInfo($"[{account.AccountName}] Speedup button found, looking for troop training cancel button...");
                    
                    // Look for and click the cancel button from trooptraining folder
                    var cancelImagePath = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder) ?? "", "trooptraining", "cancel.png");
                    if (File.Exists(cancelImagePath))
                    {
                        var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                        if (screenshot != null)
                        {
                            var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                                screenshot,
                                cancelImagePath,
                                account.InstanceNumber,
                                threshold: 0.7
                            );
                            
                            if (found)
                            {
                                logger.LogInfo($"[{account.AccountName}] Found cancel button, clicking...");
                                await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect);
                                await RandomDelay(cancellationToken);
                                
                                // Look for confirm button from ChangeAccount folder
                                var confirmImagePath = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder) ?? "", "ChangeAccount", "confirm.png");
                                if (File.Exists(confirmImagePath))
                                {
                                    screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                                    if (screenshot != null)
                                    {
                                        var (confirmFound, confirmRect, confirmConfidence) = _templateMatcher.MatchTemplate(
                                            screenshot,
                                            confirmImagePath,
                                            account.InstanceNumber,
                                            threshold: 0.7
                                        );
                                        
                                        if (confirmFound)
                                        {
                                            logger.LogInfo($"[{account.AccountName}] Found confirm button, clicking...");
                                            await ClickRandomInRectAsync(account.InstanceNumber, logger, confirmRect);
                                            await RandomDelay(cancellationToken);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                // Look for and click back button
                logger.LogInfo($"[{account.AccountName}] Looking for back button...");
                if (await FindAndClickImageWithDebugAsync("back-button.png", account.InstanceNumber, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] Back button clicked, waiting for base view...");
                    await RandomDelay(cancellationToken);
                    
                    // Wait for base-view.png from locator folder
                    var baseViewImagePath = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder) ?? "", "locator", "base-view.png");
                    if (File.Exists(baseViewImagePath))
                    {
                        logger.LogInfo($"[{account.AccountName}] Waiting for base view to appear...");
                        var baseViewFound = false;
                        var waitStartTime = DateTime.UtcNow;
                        var maxWaitTime = TimeSpan.FromSeconds(10);
                        
                        while (!baseViewFound && (DateTime.UtcNow - waitStartTime) < maxWaitTime)
                        {
                            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                            if (screenshot != null)
                            {
                                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                                    screenshot,
                                    baseViewImagePath,
                                    account.InstanceNumber,
                                    threshold: 0.7
                                );
                                
                                if (found)
                                {
                                    logger.LogInfo($"[{account.AccountName}] Base view detected (confidence: {confidence:F3})");
                                    baseViewFound = true;
                                    break;
                                }
                            }
                            
                            await Task.Delay(500, cancellationToken);
                        }
                        
                        if (baseViewFound)
                        {
                            // Perform the two clicks at the upgrade location
                            var clickPoint = new Point(347, 595);
                            logger.LogInfo($"[{account.AccountName}] Performing first click at {clickPoint}");
                            await ClickAsync(account.InstanceNumber, logger, clickPoint);
                            await RandomDelay(cancellationToken);
                            
                            logger.LogInfo($"[{account.AccountName}] Performing second click at {clickPoint}");
                            await ClickAsync(account.InstanceNumber, logger, clickPoint);
                            await RandomDelay(cancellationToken);
                            
                            return true;
                        }
                        else
                        {
                            logger.LogWarning($"[{account.AccountName}] Base view not found after {maxWaitTime.TotalSeconds} seconds");
                        }
                    }
                    else
                    {
                        logger.LogWarning($"[{account.AccountName}] base-view.png not found at: {baseViewImagePath}");
                    }
                    
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in train icon fallback: {ex.Message}");
                return false;
            }
        }

        private async Task<TimeSpan?> ReadSpeedupTimer(AccountSettings account, LogService logger)
        {
            var ocrConfig = new OCRConfiguration 
            { 
                ScaleFactor = 2.0f,
                CharacterWhitelist = "0123456789:"
            };
            
            using var ocrService = new OCRService(logger, ocrConfig);
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return null;

            var text = ocrService.ExtractTextFromScreenArea(screenshot, timerRegion);
            logger.LogInfo($"[{account.AccountName}] Timer OCR Result: '{text}'");
            
            if (TryParseTimer(text, out var timeSpan))
                return timeSpan;
                
            return null;
        }

        private bool TryParseTimer(string text, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Remove any non-timer characters
            text = new string(text.Where(c => char.IsDigit(c) || c == ':').ToArray());
            
            string[] parts = text.Split(':');
            if (parts.Length != 3)
                return false;

            if (!int.TryParse(parts[0], out int hours) ||
                !int.TryParse(parts[1], out int minutes) ||
                !int.TryParse(parts[2], out int seconds))
                return false;

            result = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath,
                    "Auto Build Module Templates:\n\n" +
                    "Required images:\n" +
                    "- side-menu.png: Side menu indicator\n" +
                    "- person-head.png: Person head icon\n" +
                    "- kitchen.png: Kitchen building icon\n" +
                    "- upgrade-button.png: Upgrade button\n" +
                    "- red-x.png: Red X indicator\n" +
                    "- go-button.png: Go button\n" +
                    "- upgrade-icon.png: Upgrade icon\n" +
                    "- train-icon.png: Train icon\n" +
                    "- speedup-button.png: Speedup button\n" +
                    "- use-button.png: Use button\n" +
                    "- back-button.png: Back button\n\n" +
                    "Notes:\n" +
                    "- Images should be clear screenshots\n" +
                    "- Crop to just the unique elements\n" +
                    "- Test at different zoom levels", 
                    cancellationToken);
            }
        }
    }
} 