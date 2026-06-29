using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Config;
using Bot.Core.LDPlayer;
using Bot.Core.Exceptions;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Bot.Core.ImageDetection;
using Tesseract;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules
{
    public class TroopTrainingTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.TroopTraining;
        public override string Name => "Troop Training";

        // Updated troop click areas with more precise positioning to avoid conflicts
        private static readonly Rectangle InfantryStatusArea = new Rectangle(160, 556, 120, 35);
        private static readonly Rectangle CavalryStatusArea = new Rectangle(159, 630, 130, 40);
        private static readonly Rectangle ArchersStatusArea = new Rectangle(158, 705, 130, 35);

        private class TroopStatusInfo
        {
            public string TroopType { get; set; } = string.Empty;
            public bool IsReady { get; set; }
            public TimeSpan RemainingTime { get; set; } = TimeSpan.Zero;
            public DateTime FinishTimeUtc { get; set; } = DateTime.MinValue;
            public string RawOcrText { get; set; } = string.Empty;
        }

        private TroopTrainingSettings GetTroopTrainingSettings(AccountSettings account)
        {
            // Use the new TroopTrainingSettings property directly
            return account.TroopTrainingSettings ?? new TroopTrainingSettings();
        }


        private async Task<bool> PerformCurvedSwipeAsync(int instanceNumber, LogService logger, Rectangle swipeArea, CancellationToken cancellationToken)
        {
            try
            {
                // Calculate start and end points
                var startX = swipeArea.Left + 10; // Start 10 pixels from left edge
                var endX = swipeArea.Right - 10;  // End 10 pixels from right edge
                var centerY = swipeArea.Top + (swipeArea.Height / 2);

                // Add slight curve to end point for more human-like movement
                var random = new Random();
                var curveOffsetY = random.Next(-10, 11); // Random curve offset between -10 and +10 pixels
                var endY = centerY + curveOffsetY;

                logger.LogInfo($"[Instance {instanceNumber}] Performing curved swipe from ({startX},{centerY}) to ({endX},{endY})");

                // Execute the swipe using ADB V2 controller
                var connection = await ADBMigrationHelper.GetConnectionAsync(instanceNumber, logger, cancellationToken);
                if (connection == null)
                {
                    logger.LogError($"[Instance {instanceNumber}] Failed to get ADB connection for swipe");
                    return false;
                }

                // Cast to ADBControllerV2 to access SwipeAsync method
                if (connection is ADBControllerV2 adbController)
                {
                    bool swipeResult = await adbController.SwipeAsync(startX, centerY, endX, endY, 500, cancellationToken);

                    if (swipeResult)
                    {
                        logger.LogInfo($"[Instance {instanceNumber}] Curved swipe executed successfully");
                        await Task.Delay(300, cancellationToken); // Small delay after swipe
                        return true;
                    }
                    else
                    {
                        logger.LogError($"[Instance {instanceNumber}] Swipe execution failed");
                        return false;
                    }
                }
                else
                {
                    logger.LogError($"[Instance {instanceNumber}] ADB connection is not ADBControllerV2 type");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[Instance {instanceNumber}] Error performing curved swipe: {ex.Message}");
                return false;
            }
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🎯 Starting troop training check. IsReRun = {isReRun}");

                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber);

                if (cancellationToken.IsCancellationRequested)
                {
                    return TaskExecutionDetails.Failed("Troop training task cancelled during view location.");
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Bot confirmed in BaseView.");

                var botConfig = ConfigLoader.Load("configs/default_config.json");
                var maxWaitTime = TimeSpan.FromMinutes(botConfig.CycleManagement.MaxTroopTrainWaitMinutes);

                DateTime? overallLatestFinishTimeUtc = null;
                bool anyTroopsProcessedSuccessfully = false;
                
                // Track which troops have been successfully trained in this session
                var processedTroopsThisSession = new HashSet<string>();
                logger.LogInfo($"[{account.AccountName}] 📝 Initialized progress tracking for this session");

                for (int loopAttempt = 0; loopAttempt < 5; loopAttempt++) 
                {
                    var troopStatuses = await GetCurrentTroopStatuses(account, logger, cancellationToken);
                    if (troopStatuses == null)
                    {
                        return TaskExecutionDetails.Failed("Failed to get troop statuses.");
                    }

                    var readyToCollectAndRetrain = troopStatuses.Where(s => s.IsReady).ToList();
                    var stillTraining = troopStatuses.Where(s => !s.IsReady && s.RemainingTime > TimeSpan.Zero).ToList();

                    // Filter out troops that have already been processed this session
                    var unprocessedReadyTroops = readyToCollectAndRetrain.Where(t => !processedTroopsThisSession.Contains(t.TroopType)).ToList();
                    
                    if (readyToCollectAndRetrain.Any() && !unprocessedReadyTroops.Any())
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found {readyToCollectAndRetrain.Count} ready troops, but all have been processed this session: {string.Join(", ", readyToCollectAndRetrain.Select(t => t.TroopType))}");
                        logger.LogInfo($"[{account.AccountName}] 📝 Already processed: {string.Join(", ", processedTroopsThisSession)}");
                    }

                    if (unprocessedReadyTroops.Any())
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found {unprocessedReadyTroops.Count} unprocessed troop types ready: {string.Join(", ", unprocessedReadyTroops.Select(t => t.TroopType))}");
                        if (processedTroopsThisSession.Any())
                        {
                            logger.LogInfo($"[{account.AccountName}] 📝 Already processed this session: {string.Join(", ", processedTroopsThisSession)}");
                        }

                        // Validate all ready troops before processing
                        var validReadyTroops = new List<TroopStatusInfo>();
                        var validTroopTypes = new[] { "Infantry", "Cavalry", "Archers" };
                        
                        foreach (var troop in unprocessedReadyTroops)
                        {
                            if (!troop.IsReady)
                            {
                                logger.LogError($"[{account.AccountName}] ❌ CRITICAL: Attempting to process {troop.TroopType} but it's not ready! Skipping.");
                                continue;
                            }
                            
                            if (!validTroopTypes.Contains(troop.TroopType))
                            {
                                logger.LogError($"[{account.AccountName}] ❌ CRITICAL: Invalid troop type '{troop.TroopType}'. Skipping.");
                                continue;
                            }
                            
                            validReadyTroops.Add(troop);
                        }
                        
                        if (validReadyTroops.Any())
                        {
                            logger.LogInfo($"[{account.AccountName}] 🎯 PROCESSING ALL READY TROOPS IN ONE OPERATION: {string.Join(", ", validReadyTroops.Select(t => t.TroopType))}");
                            
                            // Process all ready troops in a single operation (pick the first one to enter the training interface)
                            var primaryTroop = validReadyTroops.First();
                            bool success = await ProcessAllReadyTroops(account, logger, cancellationToken, validReadyTroops);
                            
                            if (success)
                            {
                                anyTroopsProcessedSuccessfully = true;
                                // Mark ALL valid ready troops as processed since we processed them all in one go
                                foreach (var troop in validReadyTroops)
                                {
                                    processedTroopsThisSession.Add(troop.TroopType);
                                    logger.LogInfo($"[{account.AccountName}] ✅ {troop.TroopType} retrained successfully and marked as processed.");
                                }
                                logger.LogInfo($"[{account.AccountName}] 📝 Session progress: {string.Join(", ", processedTroopsThisSession)} completed");
                            }
                            else
                            {
                                logger.LogError($"[{account.AccountName}] ❌ Failed to process ready troops. None will be marked as processed.");
                            }
                        }
                        continue; 
                    }
                    
                    if (stillTraining.Any())
                    {
                        logger.LogInfo($"[{account.AccountName}] ⏳ Found {stillTraining.Count} troop types still training:");
                        foreach (var troop in stillTraining)
                        {
                            logger.LogInfo($"[{account.AccountName}]   - {troop.TroopType}: {troop.RemainingTime} remaining (Finishes UTC: {troop.FinishTimeUtc:o})");
                        }
                        
                        var troopToWaitFor = stillTraining.Where(t => t.RemainingTime <= maxWaitTime).OrderBy(t => t.RemainingTime).FirstOrDefault();
                        if (troopToWaitFor != null)
                        {
                            logger.LogInfo($"[{account.AccountName}] ⏳ Waiting for {troopToWaitFor.TroopType}. Time remaining: {troopToWaitFor.RemainingTime}.");
                            logger.LogInfo($"User Info: Waiting for {troopToWaitFor.TroopType}. Time remaining: {troopToWaitFor.RemainingTime}.", category: LogCategories.UserAction);
                            await Task.Delay(troopToWaitFor.RemainingTime.Add(TimeSpan.FromSeconds(15)), cancellationToken);
                            continue; 
                        }
                    }

                    overallLatestFinishTimeUtc = stillTraining.Any() ? stillTraining.Max(t => t.FinishTimeUtc) : null;
                    break;
                }
                
                await PerformPostTaskActions(account, logger, cancellationToken);
                
                // Log session summary
                if (processedTroopsThisSession.Any())
                {
                    logger.LogInfo($"[{account.AccountName}] 📊 Session Summary: Successfully processed {processedTroopsThisSession.Count} troop types: {string.Join(", ", processedTroopsThisSession)}");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] 📊 Session Summary: No troops were processed in this session");
                }
                
                string message = anyTroopsProcessedSuccessfully ? $"Processed {processedTroopsThisSession.Count} troop types." : "No troops processed or ready.";
                return new TaskExecutionDetails(true, overallLatestFinishTimeUtc, message);
            }
            catch (BotLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error executing Troop Training task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error executing Troop Training task: {ex.Message}");
            }
        }

        private async Task<List<TroopStatusInfo>?> GetCurrentTroopStatuses(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    logger.LogInfo($"[{account.AccountName}] 🖱️ Opening side menu for status check (Attempt {attempt}/{maxAttempts})");
                    if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, GameCoordinates.MenuTriggerArea))
                    {
                        if (attempt < maxAttempts) await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                        continue;
                    }
                    await Task.Delay(GameCoordinates.Delays.AfterMenuClick, cancellationToken);

                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null || !CheckForSideMenu(account, logger, screenshot))
                    {
                        if (attempt < maxAttempts) await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                        continue;
                    }

                    await Task.Delay(500, cancellationToken);

                    // Simplified timer config
                    var timerOcrConfig = new OCRConfiguration {
                        CharacterWhitelist = "0123456789:",
                        ScaleFactor = 4
                    };
                    using var timerOcrService = new OCRService(logger, timerOcrConfig);
                    
                    // Simplified word config - removed unused properties
                    var wordOcrConfig = new OCRConfiguration {
                        CharacterWhitelist = "ceidlmopt", 
                        ScaleFactor = 4,
                        PageSegMode = PageSegMode.SingleLine
                    };
                    using var wordOcrService = new OCRService(logger, wordOcrConfig);

                    var statuses = new List<TroopStatusInfo>();
                    var troopAreas = new Dictionary<string, Rectangle>
                    {
                        { "Infantry", InfantryStatusArea },
                        { "Cavalry", CavalryStatusArea },
                        { "Archers", ArchersStatusArea }
                    };

                    foreach (var troop in troopAreas)
                    {
                        string timerText = timerOcrService.ExtractTextFromScreenArea(screenshot, troop.Value);
                        var status = ParseTroopStatus(timerText, troop.Key, logger, isTimerCheck: true);

                        if (!status.IsReady && status.RemainingTime == TimeSpan.Zero)
                        {
                            logger.LogInfo($"[{troop.Key}] Not a timer, checking for words...");
                            string wordText = wordOcrService.ExtractTextFromScreenArea(screenshot, troop.Value);
                            status = ParseTroopStatus(wordText, troop.Key, logger, isTimerCheck: false);
                        }
                        
                        statuses.Add(status);
                    }
                    
                    logger.LogInfo($"[{account.AccountName}] 📊 Troop status check complete (Attempt {attempt}).");
                    return statuses;
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Error in GetCurrentTroopStatuses attempt {attempt}: {ex.Message}");
                    if (attempt < maxAttempts) await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                }
            }
            logger.LogError($"[{account.AccountName}] ❌ Failed to get troop statuses after {maxAttempts} attempts.");
            return null;
        }

        private TroopStatusInfo ParseTroopStatus(string ocrText, string troopType, LogService logger, bool isTimerCheck)
        {
            var status = new TroopStatusInfo
            {
                TroopType = troopType,
                RawOcrText = ocrText.Replace("\n", " ").Trim()
            };
            logger.LogInfo($"[{troopType}] Raw OCR text: '{status.RawOcrText}' (Timer Check: {isTimerCheck})");

            try
            {
                var timeMatch = Regex.Match(status.RawOcrText, @"(\d{1,2})\s?:\s?(\d{2})\s?:\s?(\d{2})");
                if (timeMatch.Success)
                {
                    if (int.TryParse(timeMatch.Groups[1].Value, out int h) &&
                        int.TryParse(timeMatch.Groups[2].Value, out int m) &&
                        int.TryParse(timeMatch.Groups[3].Value, out int s))
                    {
                        status.RemainingTime = new TimeSpan(h, m, s);
                        status.FinishTimeUtc = DateTime.UtcNow.Add(status.RemainingTime);
                        status.IsReady = (status.RemainingTime < TimeSpan.FromSeconds(2));
                        if(status.IsReady) logger.LogInfo($"[{troopType}] Status: Timer is basically zero. Marking as Ready.");
                        else logger.LogInfo($"[{troopType}] Status: Parsed time {status.RemainingTime:hh\\:mm\\:ss}.");
                        return status;
                    }
                }

                if (isTimerCheck) return status;

                string[] readyKeywords = { "complete", "completed", "idle", "ready" };
                var words = status.RawOcrText.Split(new[] { ' ', ':', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var word in words)
                {
                    foreach (var keyword in readyKeywords)
                    {
                        if (IsSimilar(word, keyword, 2))
                        {
                            status.IsReady = true;
                            status.RemainingTime = TimeSpan.Zero;
                            status.FinishTimeUtc = DateTime.UtcNow;
                            logger.LogInfo($"[{troopType}] Status: Ready (found word '{word}' similar to '{keyword}')");
                            return status;
                        }
                    }
                }

                logger.LogWarning($"[{troopType}] Could not determine a valid status from text: '{status.RawOcrText}'");
                status.IsReady = false;
                return status;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{troopType}] Error parsing troop status: {ex.Message}");
                status.IsReady = false;
                return status;
            }
        }

        private bool IsSimilar(string s1, string s2, int maxDistance)
        {
            string s1Lower = s1.ToLowerInvariant();
            string s2Lower = s2.ToLowerInvariant();

            int n = s1Lower.Length;
            int m = s2Lower.Length;
            int[,] d = new int[n + 1, m + 1];

            if (Math.Abs(n - m) > maxDistance) return false;
            if (n == 0) return m <= maxDistance;
            if (m == 0) return n <= maxDistance;

            for (int i = 0; i <= n; d[i, 0] = i++);
            for (int j = 0; j <= m; d[0, j] = j++);

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (s2Lower[j - 1] == s1Lower[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m] <= maxDistance;
        }
        

        private async Task<bool> ProcessAllReadyTroops(AccountSettings account, LogService logger, CancellationToken cancellationToken, List<TroopStatusInfo> readyTroops)
        {
            if (!readyTroops.Any())
            {
                logger.LogWarning($"[{account.AccountName}] No ready troops provided to process");
                return false;
            }

            const int maxRetries = 2;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    logger.LogInfo($"[{account.AccountName}] 🔄 Processing ALL ready troops in one operation (Attempt {attempt}/{maxRetries})");
                    logger.LogInfo($"[{account.AccountName}] 🎯 Ready troops to process: {string.Join(", ", readyTroops.Select(t => t.TroopType))}");
                    
                    // Use the first ready troop to enter the training interface
                    var primaryTroop = readyTroops.First();
                    Rectangle troopArea = GetTroopCompletionArea(primaryTroop.TroopType);
                    var centerPoint = troopArea.GetCenter();
                    
                    // Click the primary troop in side menu to enter training interface
                    logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking {primaryTroop.TroopType} in side menu to enter training interface at coordinates ({centerPoint.X}, {centerPoint.Y}) - Area: {troopArea}");
                    if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, troopArea, cancellationToken, 2))
                    {
                        if (attempt < maxRetries) { await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken); continue; }
                        return false;
                    }
                    await Task.Delay(GameCoordinates.Delays.AfterMenuClick, cancellationToken);

                    // Step 1: Pre-train sequence - Wait 1 second, then double-click in the specified box
                    logger.LogInfo($"[{account.AccountName}] ⏳ Waiting 1 second before train preparation...");
                    await Task.Delay(1000, cancellationToken);
                    
                    // Double-click in box 326,550 373,586 (width=47, height=36)
                    var preTrainClickArea = new Rectangle(326, 550, 47, 36);
                    logger.LogInfo($"[{account.AccountName}] 🖱️ Pre-train setup: clicking in preparation area twice...");
                    
                    // First click
                    if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, preTrainClickArea, cancellationToken, 1))
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Failed first pre-train click, continuing anyway...");
                    }
                    
                    // 0.2 second delay
                    await Task.Delay(200, cancellationToken);
                    
                    // Second click
                    if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, preTrainClickArea, cancellationToken, 1))
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Failed second pre-train click, continuing anyway...");
                    }
                    
                    // Step 2: Look for Train button FIRST with 2-second timeout and strict error handling
                    logger.LogInfo($"[{account.AccountName}] 🔍 Looking for 'Train' button...");
                    bool trainButtonFound = await FindAndClickImageWithTimeoutAsync("train.png", account.InstanceNumber, logger, 
                        threshold: 0.7, 
                        timeoutSeconds: 2,
                        useEnhancedMatching: false,
                        searchArea: null,
                        cancellationToken: cancellationToken);

                    if (!trainButtonFound)
                    {
                        logger.LogError($"[{account.AccountName}] ❌ CRITICAL ERROR: Failed to find 'train.png' - SKIPPING TROOP TRAINING MODULE");
                        return false; // Immediately exit with error when train.png fails
                    }

                    // Train button found, proceeding to next step
                    logger.LogInfo($"[{account.AccountName}] ✅ Found train.png, proceeding to next step");
                    
                    // Wait 0.5 seconds after clicking train button
                    await Task.Delay(500, cancellationToken);

                    // Step 3: Look for minus-button.png to verify we're in training interface
                    logger.LogInfo($"[{account.AccountName}] 🔍 Looking for minus-button.png to verify training interface...");
                    bool minusButtonFound = await FindAndClickImageWithTimeoutAsync("minus-button.png", account.InstanceNumber, logger,
                        threshold: 0.6,
                        timeoutSeconds: 2,
                        useEnhancedMatching: false,
                        searchArea: null,
                        cancellationToken: cancellationToken);

                    if (minusButtonFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found minus-button.png, we're in the correct training area");
                        await Task.Delay(500, cancellationToken);

                        // Check for train level 1 only setting
                        var troopSettings = GetTroopTrainingSettings(account);
                        logger.LogInfo($"[{account.AccountName}] 🔧 Train Level 1 Only setting: {troopSettings.TrainLevel1Only}");
                        if (troopSettings.TrainLevel1Only)
                        {
                            logger.LogInfo($"[{account.AccountName}] 🎯 Train Level 1 Only mode enabled, checking for Rookie Infantry...");

                            // Step 3.1: Use OCR to check for "Rookie Infantry" in area 202,6 518,71
                            var rookieCheckArea = new Rectangle(202, 6, 316, 65); // width = 518-202=316, height = 71-6=65

                            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                            if (screenshot != null)
                            {
                                // Create OCR config for text detection
                                var textOcrConfig = new OCRConfiguration
                                {
                                    CharacterWhitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ",
                                    ScaleFactor = 3,
                                    PageSegMode = PageSegMode.SingleLine
                                };

                                using var ocrService = new OCRService(logger, textOcrConfig);
                                string detectedText = ocrService.ExtractTextFromScreenArea(screenshot, rookieCheckArea);

                                logger.LogInfo($"[{account.AccountName}] 📝 OCR detected text: '{detectedText}'");

                                // Check if "Rookie Infantry" is detected (case insensitive, allowing for OCR errors)
                                bool isRookieInfantry = detectedText.ToLowerInvariant().Contains("rookie") &&
                                                       detectedText.ToLowerInvariant().Contains("infantry");

                                if (!isRookieInfantry)
                                {
                                    logger.LogInfo($"[{account.AccountName}] 🔄 Rookie Infantry not detected, performing swipe to navigate to level 1 troops...");

                                    // Step 3.2: Perform curved swipe from left to right in area 24,655 710,773
                                    var swipeArea = new Rectangle(24, 655, 686, 118); // width = 710-24=686, height = 773-655=118
                                    bool swipeSuccess = await PerformCurvedSwipeAsync(account.InstanceNumber, logger, swipeArea, cancellationToken);

                                    if (swipeSuccess)
                                    {
                                        logger.LogInfo($"[{account.AccountName}] ✅ Swipe completed successfully");
                                        await Task.Delay(1000, cancellationToken); // Wait for UI to settle

                                        // Step 3.3: Click random location in area 77,678 119,732
                                        var clickArea = new Rectangle(77, 678, 42, 54); // width = 119-77=42, height = 732-678=54
                                        logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking in level 1 selection area...");

                                        if (await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, clickArea, cancellationToken, 1))
                                        {
                                            logger.LogInfo($"[{account.AccountName}] ✅ Successfully clicked level 1 selection area");
                                            await Task.Delay(500, cancellationToken);
                                        }
                                        else
                                        {
                                            logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click level 1 selection area, continuing anyway...");
                                        }
                                    }
                                    else
                                    {
                                        logger.LogWarning($"[{account.AccountName}] ⚠️ Swipe failed, continuing with current selection...");
                                    }
                                }
                                else
                                {
                                    logger.LogInfo($"[{account.AccountName}] ✅ Rookie Infantry already selected, proceeding with training");
                                }
                            }
                            else
                            {
                                logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to take screenshot for rookie check, proceeding with training");
                            }
                        }

                        // Step 4: Click in the train box (409,1078 586,1136)
                        var trainBoxArea = new Rectangle(409, 1078, 177, 58); // width = 586-409=177, height = 1136-1078=58
                        if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, trainBoxArea, cancellationToken, 2))
                        {
                            logger.LogError($"[{account.AccountName}] ❌ Failed to click train box area");
                            return false; // Stop processing on error
                        }
                        await Task.Delay(GameCoordinates.Delays.AfterSettingAmount, cancellationToken);
                    }

                    // Step 5: Process remaining ready troops (exclude the primary troop since it's already being trained)
                    var remainingTroops = readyTroops.Where(t => !string.Equals(t.TroopType, primaryTroop.TroopType, StringComparison.OrdinalIgnoreCase)).ToList();
                    
                    if (remainingTroops.Any())
                    {
                        logger.LogInfo($"[{account.AccountName}] 🔄 Now processing remaining {remainingTroops.Count} ready troops (excluding {primaryTroop.TroopType} which is already being trained)...");
                        
                        foreach (var readyTroop in remainingTroops)
                        {
                            logger.LogInfo($"[{account.AccountName}] 🔍 Processing ready {readyTroop.TroopType}...");
                            
                            Rectangle clickArea;
                            switch (readyTroop.TroopType.ToLower())
                            {
                                case "cavalry":
                                    clickArea = new Rectangle(365, 1235, 30, 30);
                                    break;
                                case "archers":
                                    clickArea = new Rectangle(581, 1234, 30, 30);
                                    break;
                                case "infantry":
                                    clickArea = new Rectangle(134, 1229, 30, 30);
                                    break;
                                default:
                                    logger.LogWarning($"[{account.AccountName}] ⚠️ Unknown troop type: {readyTroop.TroopType}, skipping");
                                    continue;
                            }
                            
                            if (await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, clickArea, cancellationToken, 1))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Clicked ready {readyTroop.TroopType}, waiting 0.5 seconds...");
                                await Task.Delay(500, cancellationToken);
                                
                                // For cavalry and archers, look for minus button before clicking train box
                                if (readyTroop.TroopType.ToLower() == "cavalry" || readyTroop.TroopType.ToLower() == "archers")
                                {
                                    logger.LogInfo($"[{account.AccountName}] 🔍 Looking for minus-button.png after {readyTroop.TroopType} click...");
                                    bool minusFound = await FindAndClickImageWithTimeoutAsync("minus-button.png", account.InstanceNumber, logger,
                                        threshold: 0.6,
                                        timeoutSeconds: 2,
                                        useEnhancedMatching: false,
                                        searchArea: null,
                                        cancellationToken: cancellationToken);

                                    if (minusFound)
                                    {
                                        logger.LogInfo($"[{account.AccountName}] ✅ Found minus-button after {readyTroop.TroopType}, checking for level 1...");

                                        // Check for train level 1 only setting for this troop type as well
                                        var troopSettings = GetTroopTrainingSettings(account);
                                        logger.LogInfo($"[{account.AccountName}] 🔧 Train Level 1 Only setting for {readyTroop.TroopType}: {troopSettings.TrainLevel1Only}");
                                        if (troopSettings.TrainLevel1Only)
                                        {
                                            logger.LogInfo($"[{account.AccountName}] 🎯 Train Level 1 Only mode enabled for {readyTroop.TroopType}, checking for Rookie Infantry...");

                                            // Step: Use OCR to check for "Rookie Infantry" in area 202,6 518,71
                                            var rookieCheckArea = new Rectangle(202, 6, 316, 65); // width = 518-202=316, height = 71-6=65

                                            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                                            if (screenshot != null)
                                            {
                                                // Create OCR config for text detection
                                                var textOcrConfig = new OCRConfiguration
                                                {
                                                    CharacterWhitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ",
                                                    ScaleFactor = 3,
                                                    PageSegMode = PageSegMode.SingleLine
                                                };

                                                using var ocrService = new OCRService(logger, textOcrConfig);
                                                string detectedText = ocrService.ExtractTextFromScreenArea(screenshot, rookieCheckArea);

                                                logger.LogInfo($"[{account.AccountName}] 📝 OCR detected text for {readyTroop.TroopType}: '{detectedText}'");

                                                // Check if "Rookie Infantry" is detected (case insensitive, allowing for OCR errors)
                                                bool isRookieInfantry = detectedText.ToLowerInvariant().Contains("rookie") &&
                                                                       detectedText.ToLowerInvariant().Contains("infantry");

                                                if (!isRookieInfantry)
                                                {
                                                    logger.LogInfo($"[{account.AccountName}] 🔄 Rookie Infantry not detected for {readyTroop.TroopType}, performing swipe to navigate to level 1 troops...");

                                                    // Perform curved swipe from left to right in area 24,655 710,773
                                                    var swipeArea = new Rectangle(24, 655, 686, 118); // width = 710-24=686, height = 773-655=118
                                                    bool swipeSuccess = await PerformCurvedSwipeAsync(account.InstanceNumber, logger, swipeArea, cancellationToken);

                                                    if (swipeSuccess)
                                                    {
                                                        logger.LogInfo($"[{account.AccountName}] ✅ Swipe completed successfully for {readyTroop.TroopType}");
                                                        await Task.Delay(1000, cancellationToken); // Wait for UI to settle

                                                        // Click random location in area 77,678 119,732
                                                        var level1ClickArea = new Rectangle(77, 678, 42, 54); // width = 119-77=42, height = 732-678=54
                                                        logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking in level 1 selection area for {readyTroop.TroopType}...");

                                                        if (await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, level1ClickArea, cancellationToken, 1))
                                                        {
                                                            logger.LogInfo($"[{account.AccountName}] ✅ Successfully clicked level 1 selection area for {readyTroop.TroopType}");
                                                            await Task.Delay(500, cancellationToken);
                                                        }
                                                        else
                                                        {
                                                            logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click level 1 selection area for {readyTroop.TroopType}, continuing anyway...");
                                                        }
                                                    }
                                                    else
                                                    {
                                                        logger.LogWarning($"[{account.AccountName}] ⚠️ Swipe failed for {readyTroop.TroopType}, continuing with current selection...");
                                                    }
                                                }
                                                else
                                                {
                                                    logger.LogInfo($"[{account.AccountName}] ✅ Rookie Infantry already selected for {readyTroop.TroopType}, proceeding with training");
                                                }
                                            }
                                            else
                                            {
                                                logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to take screenshot for rookie check on {readyTroop.TroopType}, proceeding with training");
                                            }
                                        }
                                    }
                                }
                                
                                // Click train box area for this troop
                                var trainBoxAreaFinal = new Rectangle(409, 1078, 177, 58);
                                if (await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, trainBoxAreaFinal, cancellationToken, 1))
                                {
                                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully processed ready {readyTroop.TroopType}");
                                    await Task.Delay(500, cancellationToken);
                                }
                                else
                                {
                                    logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click train box for {readyTroop.TroopType}");
                                }
                            }
                            else
                            {
                                logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click {readyTroop.TroopType} icon");
                            }
                        }
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] ℹ️ Only {primaryTroop.TroopType} was ready, and it's already being trained by entering the training interface");
                    }
                    
                    // After processing all ready troops, we're done
                    logger.LogInfo($"[{account.AccountName}] ✅ Completed processing all {readyTroops.Count} ready troops, finishing up");
                    
                    // Click back button to return to base view
                    var backButtonArea = new Rectangle(24, 25, 27, 21);
                    logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking back button to return to base view...");
                    await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, backButtonArea, cancellationToken, 2);
                    await Task.Delay(GameCoordinates.Delays.AfterMenuClick, cancellationToken);
                    
                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully processed all ready troops: {string.Join(", ", readyTroops.Select(t => t.TroopType))}");
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Error processing ready troops: {ex.Message}");
                    if (attempt < maxRetries) await Task.Delay(GameCoordinates.Delays.BetweenErrorRetries, cancellationToken);
                }
            }
            
            logger.LogError($"[{account.AccountName}] ❌ Failed to process ready troops after {maxRetries} attempts");
            return false;
        }

        private async Task PerformPostTaskActions(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 📸 Performing post-task actions...");
                
                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber);
                
                var finalScreenshotCheck = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (finalScreenshotCheck != null && CheckForSideMenu(account, logger, finalScreenshotCheck))
                {
                    logger.LogInfo($"[{account.AccountName}] Side menu still open, closing it.");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, new Rectangle(453, 514, 19, 70));
                    await Task.Delay(GameCoordinates.Delays.AfterClick, cancellationToken);
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Post-task actions completed");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in post-task actions: {ex.Message}");
            }
        }

        private async Task<bool> ClickRandomInRectAsyncWithRetry(int instanceNumber, LogService logger, Rectangle rect, CancellationToken cancellationToken, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (await ClickRandomInRectAsync(instanceNumber, logger, rect))
                    {
                        return true;
                    }
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Click attempt {attempt} in {rect} exception: {ex.Message}");
                    if (ex.Message.Contains("device") && ex.Message.Contains("not found"))
                    {
                        logger.LogInfo("Detected ADB connection loss, attempting to recover...");
                        // V2 system handles connection recovery automatically
                        await Task.Delay(2000, cancellationToken);
                        await ADBMigrationHelper.GetConnectionAsync(instanceNumber, logger, cancellationToken);
                    }
                    if (attempt < maxRetries) await Task.Delay(GameCoordinates.Delays.BetweenErrorRetries, cancellationToken);
                }
            }
            logger.LogError($"Failed to click in {rect} after {maxRetries} attempts");
            return false;
        }

        private Rectangle GetTroopCompletionArea(string troopType)
        {
            var lowerTroopType = troopType.ToLowerInvariant();
            Rectangle result = lowerTroopType switch
            {
                "infantry" => InfantryStatusArea, 
                "cavalry" => CavalryStatusArea,  
                "archers" => ArchersStatusArea,  
                _ => throw new ArgumentException($"Unknown troop type: '{troopType}'. Expected: Infantry, Cavalry, or Archers")
            };

            return result;
        }

        private Rectangle GetTrainingArea(string troopType)
        {
            return troopType.ToLowerInvariant() switch
            {
                "infantry" => new Rectangle(304, 576, 93, 56), 
                "cavalry" => new Rectangle(328, 565, 94, 67),  
                "archers" => new Rectangle(308, 556, 114, 67), 
                _ => new Rectangle(304, 576, 93, 56) 
            };
        }

        
        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }


        private async Task<bool> FindAndClickImageWithTimeoutAsync(string imageName, int instanceNumber, LogService logger, 
            double threshold = 0.6, int timeoutSeconds = 2, bool useEnhancedMatching = false, 
            Rectangle? searchArea = null, CancellationToken cancellationToken = default)
        {
            const int pollIntervalMs = 200; // Check every 200ms
            var startTime = DateTime.UtcNow;
            var timeoutSpan = TimeSpan.FromSeconds(timeoutSeconds);
            
            logger.LogInfo($"Looking for '{imageName}' with {timeoutSeconds}s timeout...");
            
            while (DateTime.UtcNow - startTime < timeoutSpan && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(pollIntervalMs, cancellationToken);
                        continue;
                    }

                    if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                    var templatePath = Path.Combine(ImageTemplateFolder, imageName);

                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        templatePath,
                        instanceNumber,
                        threshold,
                        scales: useEnhancedMatching ? UnifiedTemplateMatchingService.StandardScales : new[] { 1.0 },
                        searchArea: searchArea
                    );

                    if (found)
                    {
                        var elapsed = DateTime.UtcNow - startTime;
                        logger.LogInfo($"Found '{imageName}' at {matchRect} with confidence {confidence:F3} after {elapsed.TotalSeconds:F1}s");
                        return await ClickRandomInRectAsync(instanceNumber, logger, matchRect);
                    }

                    // Store the best confidence for potential error reporting
                    if (DateTime.UtcNow - startTime >= timeoutSpan)
                    {
                        logger.LogWarning($"Could not find '{imageName}' within {timeoutSeconds}s timeout (best confidence: {confidence:F3})");
                        break;
                    }

                    await Task.Delay(pollIntervalMs, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error in FindAndClickImageWithTimeoutAsync for '{imageName}': {ex.Message}");
                    await Task.Delay(pollIntervalMs, cancellationToken);
                }
            }
            
            var totalElapsed = DateTime.UtcNow - startTime;
            logger.LogWarning($"Failed to find '{imageName}' after {totalElapsed.TotalSeconds:F1}s");
            return false;
        }

        private bool CheckForSideMenu(AccountSettings account, LogService logger, byte[] screenshot)
        {
            try
            {
                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }
                var sideMenuPath = Path.Combine(ImageTemplateFolder, "side-menu.png");

                // Side menu is a fixed UI element, so we can use a tighter scale range
                double[] sideMenuScales = new[] { 1.0 };
                
                var (found, _, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    sideMenuPath,
                    account.InstanceNumber,
                    threshold: 0.5,
                    scales: sideMenuScales,
                    verboseLogging: false
                );

                if (found)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Side menu detected (confidence: {confidence:F3})");
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Side menu not detected (best confidence: {confidence:F3})");
                }
                return found;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Exception in CheckForSideMenu: {ex.Message}");
                return false; 
            }
        }
    }
}
