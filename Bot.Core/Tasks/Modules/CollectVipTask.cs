using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.Exceptions;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    public class CollectVipTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.CollectVip;
        public override string Name => "Collect Vip";

        private LocatorService? _locatorService;
        
        // Static dictionary to track last collection times per instance
        private static readonly ConcurrentDictionary<int, DateTime> _lastCollectionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "vip_collection_times.json"
        );
        
        // Collection interval when both VIP rewards are collected
        private static readonly TimeSpan VipRewardsInterval = TimeSpan.FromHours(12);
        
        // Track if VIP rewards were collected
        private bool _vipRewardsClicked = false;
        private bool _vipRewards2Clicked = false;
        
        // Search areas for specific UI elements
        private static readonly Rectangle VipChestSearchArea = new Rectangle(580, 235, 97, 96);  // 580,235 to 677,331
        private static readonly Rectangle VipClaimSearchArea = new Rectangle(480, 780, 180, 80); // 480,780 to 660,860

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            _instanceLogger = logger;
            await base.OnInitializeAsync(logger, cancellationToken);
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _instanceLogger = logger;
                _locatorService = new LocatorService(logger, account, TaskType);
                
                // Reset VIP reward tracking flags
                _vipRewardsClicked = false;
                _vipRewards2Clicked = false;
                
                // Load collection times
                LoadCollectionTimes();
                
                // Check if enough time has passed since last collection
                if (!ShouldCollect(account.InstanceNumber))
                {
                    var nextCollection = GetNextCollectionTime(account.InstanceNumber);
                    logger.LogInfo($"[{account.AccountName}] Too soon to collect VIP rewards. Next collection at: {nextCollection}");
                    return new TaskExecutionDetails(true, nextCollection, "Skipped - Too soon to collect");
                }

                logger.LogInfo($"[{account.AccountName}] Starting VIP collection process...");

                // Step 1: Navigate to base mode
                logger.LogInfo($"[{account.AccountName}] Navigating to base view...");
                if (!await _locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                {
                    logger.LogError($"[{account.AccountName}] Failed to navigate to base view");
                    return TaskExecutionDetails.Failed("Failed to navigate to base view");
                }

                // Step 2: Find and click VIP button (with color template matching)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP button...");
                if (!await FindAndClickImageWithColorAsync("vip.png", account.InstanceNumber, logger, cancellationToken))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP button not found");
                    return TaskExecutionDetails.Failed("VIP button not found");
                }

                await Task.Delay(1000, cancellationToken); // Wait for VIP interface to load

                // Step 2.5: Check if VIP interface loaded properly by looking for VIP chest or VIP claim
                logger.LogInfo($"[{account.AccountName}] Checking if VIP interface loaded...");
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null && _templateMatcher != null)
                {
                    // First check for VIP chest
                    var vipChestPath = Path.Combine(ImageTemplateFolder, "vip-chest.png");
                    bool vipChestFound = false;
                    if (File.Exists(vipChestPath))
                    {
                        var vipChestResult = _templateMatcher.MatchTemplate(
                            screenshot,
                            vipChestPath,
                            account.InstanceNumber,
                            threshold: GameCoordinates.Thresholds.StandardConfidence,
                            scales: UnifiedTemplateMatchingService.StandardScales,
                            verboseLogging: false,
                            searchArea: VipChestSearchArea
                        );
                        vipChestFound = vipChestResult.found;
                    }
                    
                    // If VIP chest not found, check for VIP claim
                    bool vipClaimFound = false;
                    if (!vipChestFound)
                    {
                        var vipClaimPath = Path.Combine(ImageTemplateFolder, "vip-claim.png");
                        if (File.Exists(vipClaimPath))
                        {
                            var vipClaimResult = _templateMatcher.MatchTemplate(
                                screenshot,
                                vipClaimPath,
                                account.InstanceNumber,
                                threshold: GameCoordinates.Thresholds.StandardConfidence,
                                scales: UnifiedTemplateMatchingService.StandardScales,
                                verboseLogging: false,
                                searchArea: VipClaimSearchArea
                            );
                            vipClaimFound = vipClaimResult.found;
                        }
                    }
                    
                    // If neither VIP chest nor VIP claim found, click back button
                    if (!vipChestFound && !vipClaimFound)
                    {
                        logger.LogWarning($"[{account.AccountName}] VIP interface did not load properly (neither chest nor claim found) - clicking deploy-back button");
                        if (await FindAndClickButtonImageAsync("deploy-back.png", account.InstanceNumber, logger))
                        {
                            logger.LogInfo($"[{account.AccountName}] Successfully clicked deploy-back button");
                            return TaskExecutionDetails.Failed("VIP interface did not load - returned to base");
                        }
                        return TaskExecutionDetails.Failed("VIP interface did not load and could not return to base");
                    }
                }

                // Step 3: Find and click VIP chest (with color template matching in specific area)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP chest in area {VipChestSearchArea}...");
                if (!await FindAndClickImageWithColorAsync("vip-chest.png", account.InstanceNumber, logger, cancellationToken, VipChestSearchArea))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP chest not found in search area");
                    return TaskExecutionDetails.Failed("VIP chest not found");
                }

                await Task.Delay(1000, cancellationToken); // Wait for chest interface to load

                // Step 3.5: Check for vip-rewards.png after clicking vip-chest (with 2-second timeout)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP rewards template: templates\\images\\collectvip\\vip-rewards.png (up to 2 seconds)...");
                bool vipRewardsFound = false;
                var vipRewardsStartTime = DateTime.UtcNow;
                int attemptCount = 0;
                
                while (!vipRewardsFound && (DateTime.UtcNow - vipRewardsStartTime).TotalSeconds < 2)
                {
                    attemptCount++;
                    logger.LogInfo($"[{account.AccountName}] Searching for vip-rewards.png - Attempt {attemptCount}");
                    
                    if (await FindAndClickImageAsync("vip-rewards.png", account.InstanceNumber, logger, threshold: 0.8, useEnhancedMatching: true))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ VIP rewards found and clicked at attempt {attemptCount}");
                        _vipRewardsClicked = true; // Track that we clicked vip-rewards
                        vipRewardsFound = true;
                        await Task.Delay(1000, cancellationToken); // Wait for rewards to be collected
                    }
                    else if (!vipRewardsFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] VIP rewards not found in attempt {attemptCount}, waiting 200ms before retry");
                        await Task.Delay(200, cancellationToken); // Wait 200ms before retry
                    }
                }
                
                if (!vipRewardsFound)
                {
                    logger.LogInfo($"[{account.AccountName}] ❌ VIP rewards (templates\\images\\collectvip\\vip-rewards.png) not found after {attemptCount} attempts in 2 seconds, continuing...");
                }

                // Step 4: Find and click VIP claim button (regular template matching in specific area)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP claim button in area {VipClaimSearchArea}...");
                if (!await FindAndClickImageInAreaAsync("vip-claim.png", account.InstanceNumber, logger, VipClaimSearchArea))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP claim button not found in search area, continuing to next step...");
                }
                else
                {
                    await Task.Delay(1000, cancellationToken); // Wait for claim to process

                    // Step 4.5: Check for vip-rewards2.png after clicking vip-claim (with 2-second timeout)
                    logger.LogInfo($"[{account.AccountName}] Looking for VIP rewards 2 (up to 2 seconds)...");
                    bool vipRewards2Found = false;
                    var vipRewards2StartTime = DateTime.UtcNow;
                    
                    while (!vipRewards2Found && (DateTime.UtcNow - vipRewards2StartTime).TotalSeconds < 2)
                    {
                        if (await FindAndClickImageAsync("vip-rewards2.png", account.InstanceNumber, logger, threshold: 0.8, useEnhancedMatching: true))
                        {
                            logger.LogInfo($"[{account.AccountName}] VIP rewards 2 clicked");
                            _vipRewards2Clicked = true; // Track that we clicked vip-rewards2
                            vipRewards2Found = true;
                            await Task.Delay(1000, cancellationToken); // Wait for rewards to be collected
                        }
                        else if (!vipRewards2Found)
                        {
                            await Task.Delay(200, cancellationToken); // Wait 200ms before retry
                        }
                    }
                    
                    if (!vipRewards2Found)
                    {
                        logger.LogInfo($"[{account.AccountName}] VIP rewards 2 not found after 2 seconds, continuing...");
                    }
                }

                // Step 5: Find and click VIP plus button
                logger.LogInfo($"[{account.AccountName}] Looking for VIP plus button...");
                if (!await FindAndClickImageAsync("vip-plus.png", account.InstanceNumber, logger))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP plus button not found");
                    return TaskExecutionDetails.Failed("VIP plus button not found");
                }

                await Task.Delay(1000, cancellationToken); // Wait for plus interface to load

                // Step 6: Repeatedly click VIP use button until no longer detected
                logger.LogInfo($"[{account.AccountName}] Starting VIP use loop...");
                int useCount = 0;
                const int maxUseAttempts = 50; // Prevent infinite loops
                
                while (useCount < maxUseAttempts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    logger.LogInfo($"[{account.AccountName}] Looking for VIP use button (attempt {useCount + 1})...");
                    if (!await FindAndClickImageAsync("vip-use.png", account.InstanceNumber, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] VIP use button no longer detected, ending loop");
                        break;
                    }

                    useCount++;
                    await Task.Delay(500, cancellationToken); // Short delay between uses
                }

                if (useCount >= maxUseAttempts)
                {
                    logger.LogWarning($"[{account.AccountName}] Reached maximum VIP use attempts ({maxUseAttempts})");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Successfully used VIP {useCount} times");
                }

                // Step 7: Close level dialog
                logger.LogInfo($"[{account.AccountName}] Looking for level close button...");
                if (!await FindAndClickImageAsync("level-close.png", account.InstanceNumber, logger))
                {
                    logger.LogWarning($"[{account.AccountName}] Level close button not found, continuing...");
                }

                await Task.Delay(500, cancellationToken); // Wait for close to process

                // Step 8: Click deploy back button
                logger.LogInfo($"[{account.AccountName}] Looking for deploy back button...");
                if (!await FindAndClickButtonImageAsync("deploy-back.png", account.InstanceNumber, logger))
                {
                    logger.LogWarning($"[{account.AccountName}] Deploy back button not found, continuing...");
                }

                await Task.Delay(500, cancellationToken); // Wait for navigation

                // Update collection time only if both VIP rewards were clicked
                if (_vipRewardsClicked && _vipRewards2Clicked)
                {
                    UpdateCollectionTime(account.InstanceNumber);
                    logger.LogInfo($"[{account.AccountName}] ✅ Both VIP rewards collected - applying 12-hour cooldown");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ Not all VIP rewards were collected - no cooldown applied");
                    logger.LogInfo($"[{account.AccountName}] VIP Rewards: {(_vipRewardsClicked ? "Collected" : "Not found")}, VIP Rewards 2: {(_vipRewards2Clicked ? "Collected" : "Not found")}");
                }

                logger.LogInfo($"[{account.AccountName}] VIP collection process completed successfully");
                return TaskExecutionDetails.Succeeded();
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] VIP collection task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during VIP collection: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Find and click image using color template matching (enhanced matching)
        /// </summary>
        private async Task<bool> FindAndClickImageWithColorAsync(string imageName, int instanceNumber, LogService logger, CancellationToken cancellationToken, Rectangle? searchArea = null)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Image template not found: {templatePath}");
                    return false;
                }

                // Use enhanced matching with color template matching
                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: GameCoordinates.Thresholds.StandardConfidence,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: false,
                    searchArea: searchArea
                );

                if (result.found)
                {
                    logger.LogInfo($"Found '{imageName}' at {result.matchRect} with confidence {result.confidence:F3}");
                    return await ClickCenterInRectAsync(instanceNumber, logger, result.matchRect);
                }

                logger.LogWarning($"Could not find '{imageName}' with color matching (best confidence: {result.confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickImageWithColorAsync for '{imageName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find and click image from the buttons folder (shared UI elements)
        /// </summary>
        private async Task<bool> FindAndClickButtonImageAsync(string imageName, int instanceNumber, LogService logger)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for button '{imageName}'");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                // Use buttons folder instead of collectvip folder
                var templatePath = Path.Combine(AppContext.BaseDirectory, "templates", "images", "buttons", imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Button template not found: {templatePath}");
                    return false;
                }

                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: GameCoordinates.Thresholds.StandardConfidence,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: false
                );

                if (result.found)
                {
                    logger.LogInfo($"Found button '{imageName}' at {result.matchRect} with confidence {result.confidence:F3}");
                    return await ClickCenterInRectAsync(instanceNumber, logger, result.matchRect);
                }

                logger.LogWarning($"Could not find button '{imageName}' (best confidence: {result.confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickButtonImageAsync for '{imageName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find and click image using regular template matching in a specific area
        /// </summary>
        private async Task<bool> FindAndClickImageInAreaAsync(string imageName, int instanceNumber, LogService logger, Rectangle searchArea)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Image template not found: {templatePath}");
                    return false;
                }

                // Use regular template matching with search area
                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: GameCoordinates.Thresholds.StandardConfidence,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: false,
                    searchArea: searchArea
                );

                if (result.found)
                {
                    logger.LogInfo($"Found '{imageName}' at {result.matchRect} with confidence {result.confidence:F3}");
                    return await ClickCenterInRectAsync(instanceNumber, logger, result.matchRect);
                }

                logger.LogWarning($"Could not find '{imageName}' in search area {searchArea} (best confidence: {result.confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickImageInAreaAsync for '{imageName}': {ex.Message}");
                return false;
            }
        }

        protected override string GetImageFolderName()
        {
            return "collectvip";
        }
        
        private bool ShouldCollect(int instanceNumber)
        {
            if (!_lastCollectionTimes.TryGetValue(instanceNumber, out DateTime lastCollection))
            {
                return true; // First time collection
            }

            var nextCollection = GetNextCollectionTime(instanceNumber);
            return DateTime.UtcNow >= nextCollection;
        }

        private DateTime GetNextCollectionTime(int instanceNumber)
        {
            if (!_lastCollectionTimes.TryGetValue(instanceNumber, out DateTime lastCollection))
            {
                return DateTime.UtcNow; // First time collection
            }

            // Use 12-hour interval when VIP rewards were collected
            return lastCollection.Add(VipRewardsInterval);
        }

        private void UpdateCollectionTime(int instanceNumber)
        {
            _lastCollectionTimes[instanceNumber] = DateTime.UtcNow;
            SaveCollectionTimes();
        }

        private void LoadCollectionTimes()
        {
            try
            {
                if (File.Exists(TimeStorageFile))
                {
                    var json = File.ReadAllText(TimeStorageFile);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var times = JsonSerializer.Deserialize<ConcurrentDictionary<int, DateTime>>(json, options);
                    if (times != null)
                    {
                        // Validate and clean up old entries
                        var now = DateTime.UtcNow;
                        foreach (var kvp in times)
                        {
                            // Only keep entries from the last 24 hours
                            if (now.Subtract(kvp.Value).TotalHours <= 24)
                            {
                                _lastCollectionTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectVipTask] Error loading collection times: {ex.Message}");
                _lastCollectionTimes.Clear();
            }
        }

        private void SaveCollectionTimes()
        {
            try
            {
                var directory = Path.GetDirectoryName(TimeStorageFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Validate times before saving
                var now = DateTime.UtcNow;
                var validTimes = _lastCollectionTimes
                    .Where(kvp => now.Subtract(kvp.Value).TotalHours <= 24)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(validTimes, options);
                File.WriteAllText(TimeStorageFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectVipTask] Error saving collection times: {ex.Message}");
            }
        }
    }
}