using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Exceptions;
using Bot.Core.Services;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Drawing;
using Bot.Core.ImageDetection;
using Bot.Core.Config;
using Bot.Core.LDPlayer;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Exception thrown when no troops are available for farming.
    /// </summary>
    public class NoTroopsAvailableException : Exception
    {
        public NoTroopsAvailableException() : base("No troops available for farming") { }
    }

    /// <summary>
    /// Handles automated resource gathering in the game by sending marches to resource tiles.
    /// </summary>
    public class FarmingTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.Farming;
        public override string Name => "Farming";

        // Static instance-specific farming rotation indexes to maintain state across runs
        private static readonly ConcurrentDictionary<int, int> _farmingRotationIndexes = new();
        private readonly string _templateFolder;

        // Constants for polling and timeouts
        private const int POLL_INTERVAL_MS = 200;  // Check every 200ms
        private const int MAX_WAIT_TIME_MS = 5000; // 5 seconds total wait time
        private const double TEMPLATE_MATCH_THRESHOLD = 0.6; // Consistent threshold for template matching

        public FarmingTask()
        {
            // Get the application's base directory and combine with template path
            string baseDir = AppContext.BaseDirectory;
            _templateFolder = Path.Combine(baseDir, "templates", "images", "farming");

            // Ensure template directory exists
            if (!Directory.Exists(_templateFolder))
            {
                Directory.CreateDirectory(_templateFolder);
            }
        }

        // UI element coordinates
        private static readonly Rectangle MarchCapacityTextRect = new Rectangle(190, 196, 52, 32);  // 190,196 to 242,228
        private static readonly Rectangle SearchIconRect = new Rectangle(20, 855, 47, 43);  // Search icon in top menu
        private static readonly Rectangle ResourceScrollArea = new Rectangle(0, 850, 718, 150);  // Resource selection area
        private static readonly Rectangle GatherButtonArea = new Rectangle(257, 592, 207, 72);  // 257,592 to 464,664
        private static readonly Rectangle SearchButtonArea = new Rectangle(117, 1166, 414, 102);  // 117,1166 to 531,1268
        private static readonly Rectangle DeployButtonArea = new Rectangle(388, 1159, 315, 107);  // From AutoHuntTask: 388,1159 to 703,1266
        private static readonly Rectangle MaxMarchArea = new Rectangle(234, 311, 244, 57);  // 234,311 to 478,368
        private static readonly Rectangle LevelTextRect = new Rectangle(102, 991, 426, 40);  // 102,991 to 528,1031
        private static readonly Rectangle LevelPlusButtonRect = new Rectangle(470, 1037, 35, 34);
        private static readonly Rectangle LevelMinusButtonRect = new Rectangle(52, 1039, 34, 29);
        private static readonly Rectangle FailedGatherFallbackClickRect = new Rectangle(201, 398, 300, 403);
        
        /// <summary>
        /// Main execution method for the farming task.
        /// </summary>
        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting Farming task...");

                // Get AutoHunt settings to check for cooldown
                var autoHuntSettings = GetAutoHuntSettings(account);
                if (autoHuntSettings.LastMarchSentTime.HasValue)
                {
                    var timeSinceLastHunt = DateTime.UtcNow - autoHuntSettings.LastMarchSentTime.Value;
                    var huntCooldown = TimeSpan.FromMinutes(5); // 5 minute cooldown for AutoHunt
                    if (timeSinceLastHunt < huntCooldown)
                    {
                        var waitTime = huntCooldown - timeSinceLastHunt;
                        logger.LogInfo($"[{account.AccountName}] AutoHunt is on cooldown. Waiting for {waitTime.TotalSeconds:F1} seconds before starting farming.");
                        userNotifications?.ShowStatus($"Waiting for AutoHunt cooldown ({waitTime.TotalSeconds:F0}s)", NotificationType.Info);
                        await Task.Delay(waitTime, cancellationToken);
                    }
                }

                // Enhanced march timer checking using the new MarchTimerService
                var timerService = new MarchTimerService(logger);
                var timerResult = await timerService.CheckActiveMarchTimersAsync(account.InstanceNumber, logger, cancellationToken, userNotifications);
                
                if (timerResult.HasActiveTimers)
                {
                    var statusMessage = timerResult.GetStatusMessage();
                    logger.LogInfo($"[{account.AccountName}] 🕒 {statusMessage}");
                    userNotifications?.ShowStatus($"Farming delayed: {statusMessage}", NotificationType.Warning);
                    
                    // Return special result to indicate we should skip this task and retry later
                    return new TaskExecutionDetails(
                        success: false, 
                        message: "SKIP_AND_RETRY_LATER", 
                        recoveryNeeded: false
                    );
                }
                
                logger.LogInfo($"[{account.AccountName}] ✅ No active march timers detected, proceeding with farming");
                userNotifications?.ShowStatus($"All marches returned - starting farming for {account.AccountName}", NotificationType.Info);

                // OCRService is now instantiated inside the specific methods that use it,
                // allowing for custom configurations for different OCR tasks.

                userNotifications?.ShowStatus("Starting farming task...", NotificationType.Info);
                
                logger.LogInfo($"[{account.AccountName}] 🧭 Ensuring bot is in MapView for farming...");
                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber);

                // Add 1 second delay after map view
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(1000, cancellationToken);

                // Check and activate gathering boost if enabled
                await CheckAndActivateGatheringBoostAsync(account, logger, cancellationToken);

                // Initialize marches count
                int currentMarches = await GetAvailableMarchesAsync(account, logger, cancellationToken);
                if (currentMarches <= 0)
                {
                    logger.LogInfo($"[{account.AccountName}] No marches available after map view check.");
                    return new TaskExecutionDetails(true, message: "No marches available.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return TaskExecutionDetails.Failed("Farming task cancelled during view location.");
                }

                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(POLL_INTERVAL_MS, cancellationToken);

                if (account.FarmingTargets == null || !account.FarmingTargets.Any())
                {
                    logger.LogWarning($"[{account.AccountName}] No farming targets configured. Skipping farming task.");
                    return new TaskExecutionDetails(true, message: "No farming targets configured.");
                }

                // Check march limit before starting farming loop
                if (account.FarmingSettings.HasReachedMarchLimit(account.InstanceNumber))
                {
                    var marchesSent = FarmingSettings.GetMarchesSent(account.InstanceNumber);
                    var maxMarches = account.FarmingSettings.MaxFarmingMarches;
                    logger.LogInfo($"[{account.AccountName}] 🛑 March limit reached: {marchesSent}/{maxMarches} marches already sent this session");
                    userNotifications?.ShowStatus($"March limit reached: {marchesSent}/{maxMarches} marches sent", NotificationType.Warning);
                    return new TaskExecutionDetails(true, message: $"March limit reached: {marchesSent}/{maxMarches} marches sent this session");
                }

                // Main farming loop
                int marchesSentSuccessfully = 0;
                var initialMarchesSent = FarmingSettings.GetMarchesSent(account.InstanceNumber);
                
                try
                {
                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        
                        // Check for pause at the beginning of each loop iteration
                        await WaitIfPausedAsync(cancellationToken);
                        if (cancellationToken.IsCancellationRequested) break;

                        // Check march limit before sending each march
                        if (account.FarmingSettings.HasReachedMarchLimit(account.InstanceNumber))
                        {
                            var remainingMarches = account.FarmingSettings.GetRemainingMarches(account.InstanceNumber);
                            logger.LogInfo($"[{account.AccountName}] 🛑 March limit reached during farming loop");
                            userNotifications?.ShowStatus($"March limit reached: {account.FarmingSettings.MaxFarmingMarches} marches sent", NotificationType.Warning);
                            break;
                        }
                        
                        currentMarches = await GetAvailableMarchesAsync(account, logger, cancellationToken);
                        if (currentMarches <= 0)
                        {
                            logger.LogInfo($"[{account.AccountName}] No more marches available.");
                            break;
                        }

                        // Get and increment the rotation index for this instance
                        int currentIndex = _farmingRotationIndexes.GetOrAdd(account.InstanceNumber, 0);
                        FarmingTarget currentTarget = account.FarmingTargets[currentIndex % account.FarmingTargets.Count];
                        _farmingRotationIndexes[account.InstanceNumber] = currentIndex + 1;

                        logger.LogInfo($"[{account.AccountName}] 🎯 Attempting to send march for {currentTarget.ResourceType} Level {currentTarget.Level}");

                        bool marchSent = await ProcessSingleMarchAsync(account, logger, cancellationToken, currentTarget, userNotifications);
                        if (marchSent)
                        {
                            marchesSentSuccessfully++;
                            FarmingSettings.IncrementMarchesSent(account.InstanceNumber);
                            
                            var totalMarchesSent = FarmingSettings.GetMarchesSent(account.InstanceNumber);
                            var marchLimitText = account.FarmingSettings.MaxFarmingMarches > 0 
                                ? $" ({totalMarchesSent}/{account.FarmingSettings.MaxFarmingMarches})" 
                                : "";
                            
                            userNotifications?.ShowSuccess($"Successfully sent troops to {currentTarget.ResourceType} Level {currentTarget.Level}{marchLimitText}");
                            logger.LogInfo($"[{account.AccountName}] 🎯 March sent successfully. Total marches sent this session: {totalMarchesSent}{(account.FarmingSettings.MaxFarmingMarches > 0 ? $"/{account.FarmingSettings.MaxFarmingMarches}" : "")}");
                            await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                        }
                        else
                        {
                            logger.LogWarning($"[{account.AccountName}] Failed to send march for {currentTarget.ResourceType}. Stopping farming for this cycle.");
                            break;
                        }
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                    }
                }
                catch (NoTroopsAvailableException)
                {
                    logger.LogInfo($"[{account.AccountName}] 🚫 No troops available for farming. Exiting farming module.");
                }

                logger.LogInfo($"[{account.AccountName}] 🏠 Returning to base view...");
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber);
                logger.LogInfo($"[{account.AccountName}] ✅ Farming task completed.");

                if (marchesSentSuccessfully > 0)
                {
                    userNotifications?.ShowStatus($"Farming completed: {marchesSentSuccessfully} {(marchesSentSuccessfully == 1 ? "march" : "marches")} sent", NotificationType.Success);
                }

                return new TaskExecutionDetails(true, message: $"Farming completed. Marches sent: {marchesSentSuccessfully}");
            }
            catch (BotLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during farming: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during farming: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines how many marches are available using a simplified and robust Regex approach.
        /// </summary>
        private async Task<int> GetAvailableMarchesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // 1. Define a much tighter cropping area to isolate *only* the "3/3" text.
            // This prevents the OCR from seeing other noisy UI elements.
            var tightMarchCapacityRect = new Rectangle(197, 205, 48, 20); 

            // 2. Create a hyper-focused OCR configuration just for this task.
            var marchOcrConfig = new OCRConfiguration {
                ScaleFactor = 4,
                AdaptiveC = 5,
                MedianBlurKernelSize = 1,
                // We ONLY expect numbers and a slash, so we tell Tesseract to ignore everything else.
                CharacterWhitelist = "0123456789/" 
            };
            using var marchOcr = new OCRService(logger, marchOcrConfig);

            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return 1; // Assume at least 1 march available if screenshot fails

            // 3. Use the new tight rectangle for OCR extraction.
            string text = marchOcr.ExtractTextFromScreenArea(screenshot, tightMarchCapacityRect);
            logger.LogInfo($"[{account.AccountName}] March capacity OCR text: '{text}'");
            
            if (string.IsNullOrWhiteSpace(text)) {
                logger.LogInfo($"[{account.AccountName}] March OCR returned no text. Assuming at least 1 march is available.");
                return 1; // Assume at least 1 march is available when OCR fails
            }

            // 4. Use a reliable Regex to parse the "current/max" pattern.
            Match match = Regex.Match(text, @"(\d+)\s*/\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int current) && int.TryParse(match.Groups[2].Value, out int max)) {
                int available = max - current;
                logger.LogInfo($"[{account.AccountName}] Parsed Marches: {current}/{max}. Available: {available}");
                return available > 0 ? available : 0;
            }

            logger.LogInfo($"[{account.AccountName}] Could not parse march capacity from '{text}'. Assuming at least 1 march is available.");
            return 1; // Assume at least 1 march is available when parsing fails
        }

        /// <summary>
        /// Processes a single march to a specific resource target.
        /// </summary>
        private async Task<bool> ProcessSingleMarchAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, FarmingTarget target, IUserNotificationService? userNotifications = null)
        {
            if (target == null)
            {
                logger.LogError($"[{account.AccountName}] Target is null");
                return false;
            }

            // Click search icon to open resource search
            logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking search icon...");
            if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, SearchIconRect)) return false;
            await Task.Delay(GameCoordinates.Delays.AfterClick, cancellationToken);

            // Find and click the specific resource type
            string resourceImageName = $"{target.ResourceType.ToString().ToLower()}.png";
            if (!await FindAndClickResourceTypeAsync(account, logger, cancellationToken, resourceImageName))
            {
                return false;
            }

            // Try each level from target level down to 1
            for (int currentLevel = target.Level; currentLevel >= 1; currentLevel--)
            {
                // Set the desired resource level
                if (!await AdjustResourceLevelAsync(account, logger, cancellationToken, currentLevel))
                {
                    return false;
                }
                
                // Search for resources at the specified level
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking search button for Lvl {currentLevel} {target.ResourceType}...");
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, SearchButtonArea)) return false;

                // Poll for gather button with improved detection
                bool gatherClicked = false;
                DateTime gatherStartTime = DateTime.UtcNow;

                while ((DateTime.UtcNow - gatherStartTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"[{account.AccountName}] 👀 Looking for 'gather-button.png' for Lvl {currentLevel}...");
                    
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                        continue;
                    }

                    if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                    
                    var templatePath = Path.Combine(ImageTemplateFolder, "gather-button.png");
                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        templatePath,
                        account.InstanceNumber,
                        threshold: TEMPLATE_MATCH_THRESHOLD,
                        scales: UnifiedTemplateMatchingService.StandardScales,
                        verboseLogging: true,
                        searchArea: GatherButtonArea  // Use the refined search area
                    );

                    if (found)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found gather button at {matchRect} with confidence {confidence:F3}");
                        if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Clicked 'gather-button.png' for Lvl {currentLevel} after {(DateTime.UtcNow - gatherStartTime).TotalMilliseconds:F0}ms.");
                            gatherClicked = true;
                            break;
                        }
                    }

                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }

                if (gatherClicked)
                {
                    // Poll for both deploy button and max march
                    DateTime startTime = DateTime.UtcNow;
                    bool deployClicked = false;
                    string deployButtonPath = Path.Combine(ImageTemplateFolder, "deploy-button.png");
                    string maxMarchPath = Path.Combine(ImageTemplateFolder, "max-march.png");
                    string noTroopsPath = Path.Combine(ImageTemplateFolder, "no-troops.png");
                    string backButtonPath = Path.Combine(_templateFolder, "..", "buttons", "deploy-back.png");
                    var maxMarchArea = new Rectangle(249, 314, 217, 49); // 249,314 to 466,363

                    // Log template details before starting detection
                    if (File.Exists(deployButtonPath))
                    {
                        using var templateBmp = new Bitmap(deployButtonPath);
                        logger.LogInfo($"[{account.AccountName}] 🔍 Deploy button template details: Size={templateBmp.Width}x{templateBmp.Height}, PixelFormat={templateBmp.PixelFormat}");
                        logger.LogInfo($"[{account.AccountName}] 🔍 Deploy button search area: {DeployButtonArea}");
                    }

                    // First wait 3 seconds for deploy button
                    while ((DateTime.UtcNow - startTime).TotalMilliseconds < 3000 && !cancellationToken.IsCancellationRequested)
                    {
                        var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                        if (screenshot == null)
                        {
                            await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                            continue;
                        }

                        if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);

                        // Check for deploy button
                        var (deployFound, deployRect, deployConfidence) = _templateMatcher.MatchTemplate(
                            screenshot,
                            deployButtonPath,
                            account.InstanceNumber,
                            threshold: TEMPLATE_MATCH_THRESHOLD,
                            scales: UnifiedTemplateMatchingService.StandardScales,
                            verboseLogging: true,
                            searchArea: DeployButtonArea
                        );

                        // Deploy button has priority
                        if (deployFound)
                        {
                            if (await ClickRandomInRectAsync(account.InstanceNumber, logger, deployRect))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Clicked deploy button");
                                deployClicked = true;
                                break;
                            }
                        }

                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                    }

                    // If deploy button not found after 3 seconds, check for no-troops
                    if (!deployClicked)
                    {
                        logger.LogInfo($"[{account.AccountName}] 🔍 Deploy button not found after 3 seconds, checking for no-troops indicator...");
                        
                        var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                        if (screenshot != null)
                        {
                            var (noTroopsFound, noTroopsRect, noTroopsConfidence) = _templateMatcher.MatchTemplate(
                                screenshot,
                                noTroopsPath,
                                account.InstanceNumber,
                                threshold: TEMPLATE_MATCH_THRESHOLD,
                                scales: UnifiedTemplateMatchingService.StandardScales,
                                verboseLogging: true
                            );

                            if (noTroopsFound)
                            {
                                logger.LogInfo($"[{account.AccountName}] ⚠️ No troops available detected at {noTroopsRect} with confidence {noTroopsConfidence:F3}. Looking for back button...");
                                
                                // Look for and click the back button
                                var (backFound, backRect, backConfidence) = _templateMatcher.MatchTemplate(
                                    screenshot,
                                    backButtonPath,
                                    account.InstanceNumber,
                                    threshold: TEMPLATE_MATCH_THRESHOLD,
                                    scales: UnifiedTemplateMatchingService.StandardScales,
                                    verboseLogging: true
                                );

                                if (backFound)
                                {
                                    if (await ClickRandomInRectAsync(account.InstanceNumber, logger, backRect))
                                    {
                                        logger.LogInfo($"[{account.AccountName}] ✅ Clicked back button. No troops available, ending farming.");
                                        await Task.Delay(GameCoordinates.Delays.AfterClick, cancellationToken);
                                        
                                        // Return a special value to indicate we should exit the farming module completely
                                        throw new NoTroopsAvailableException();
                                    }
                                }
                                else
                                {
                                    logger.LogWarning($"[{account.AccountName}] ❌ Back button not found. Attempting to continue...");
                                    return false;
                                }
                            }
                            else
                            {
                                logger.LogInfo($"[{account.AccountName}] ℹ️ No-troops indicator not found. Will now check for max-march indicator...");
                            }
                        }

                        // Continue checking for max march or other indicators for remaining time
                        logger.LogInfo($"[{account.AccountName}] 🔍 Starting max-march detection loop (will check for up to 2 more seconds)...");
                        int maxMarchCheckCount = 0;
                        while ((DateTime.UtcNow - startTime).TotalMilliseconds < 5000 && !cancellationToken.IsCancellationRequested)
                        {
                            screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                            if (screenshot == null)
                            {
                                logger.LogWarning($"[{account.AccountName}] ⚠️ Screenshot failed during max-march check");
                                await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                                continue;
                            }

                            maxMarchCheckCount++;
                            logger.LogInfo($"[{account.AccountName}] 🔍 Checking for max-march.png (attempt {maxMarchCheckCount}) in area {maxMarchArea}...");
                            
                            // Check for max march
                            var (maxMarchFound, maxMarchRect, maxMarchConfidence) = _templateMatcher.MatchTemplate(
                                screenshot,
                                maxMarchPath,
                                account.InstanceNumber,
                                threshold: TEMPLATE_MATCH_THRESHOLD,
                                scales: UnifiedTemplateMatchingService.StandardScales,
                                verboseLogging: true,
                                searchArea: maxMarchArea
                            );

                            // If max march is found but no deploy button, handle max march case
                            if (maxMarchFound)
                            {
                                logger.LogInfo($"[{account.AccountName}] ⚠️ Found max march indicator at {maxMarchRect} with confidence {maxMarchConfidence:F3}");
                                // Click the specific coordinate for max march using a small rectangle around the point
                                var maxMarchClickRect = new Rectangle(642 - 5, 338 - 5, 10, 10);
                                if (await ClickRandomInRectAsync(account.InstanceNumber, logger, maxMarchClickRect))
                                {
                                    logger.LogInfo($"[{account.AccountName}] ✅ Clicked max march coordinate (642,338). All marches are full!");
                                    return false; // No more marches available
                                }
                            }
                            else
                            {
                                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                                logger.LogInfo($"[{account.AccountName}] ℹ️ Max-march not found yet (elapsed: {elapsedMs:F0}ms of 5000ms)");
                            }

                            await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                        }
                        
                        logger.LogInfo($"[{account.AccountName}] ⏱️ Max-march detection timeout reached (5 seconds total). Neither deploy button nor max-march indicator found.");
                    }

                    if (deployClicked)
                    {
                        await Task.Delay(GameCoordinates.Delays.AfterConfirm, cancellationToken);
                        return true;
                    }
                }

                if (currentLevel > 1)
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ No gather button found for Lvl {currentLevel}, trying Lvl {currentLevel - 1}...");
                }
            }

            logger.LogWarning($"[{account.AccountName}] ⚠️ Could not find any gatherable resources from level {target.Level} down to 1.");
            return false;
        }

        /// <summary>
        /// Searches for and clicks a specific resource type in the resource list using polling detection.
        /// </summary>
        private async Task<bool> FindAndClickResourceTypeAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string resourceImageName)
        {
            // Check if we're looking for stone or iron
            bool isLaterResource = resourceImageName.Contains("stone") || resourceImageName.Contains("iron");

            // Get resource-specific scales - most resource icons are the same size
            double[] resourceScales = new[] { 1.0, 0.95, 1.05 };

            for (int scrollAttempt = 0; scrollAttempt < 3; scrollAttempt++)
            {
                // For stone or iron, swipe first before looking
                if (isLaterResource && scrollAttempt == 0)
                {
                    logger.LogInfo($"[{account.AccountName}] 📜 Resource is {resourceImageName}, swiping first to reach later resources...");
                    Point swipeStart = new Point(ResourceScrollArea.Right - 50, ResourceScrollArea.Y + ResourceScrollArea.Height / 2);
                    Point swipeEnd = new Point(ResourceScrollArea.Left + 50, ResourceScrollArea.Y + ResourceScrollArea.Height / 2);
                    await SwipeAsync(account.InstanceNumber, logger, swipeStart, swipeEnd, 300);
                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }

                var startTime = DateTime.UtcNow;
                logger.LogInfo($"[{account.AccountName}] 👀 Looking for '{resourceImageName}' (attempt {scrollAttempt + 1}/3)...");

                while ((DateTime.UtcNow - startTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
                {
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                        continue;
                    }

                    if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                    
                    var resourcePath = Path.Combine(ImageTemplateFolder, resourceImageName);
                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        resourcePath,
                        account.InstanceNumber,
                        threshold: TEMPLATE_MATCH_THRESHOLD,
                        scales: resourceScales,
                        verboseLogging: true,
                        searchArea: ResourceScrollArea
                    );

                    if (found)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found '{resourceImageName}' at {matchRect} with confidence {confidence:F3}");
                        // Try clicking multiple times with small delays
                        for (int clickAttempt = 0; clickAttempt < 2; clickAttempt++)
                        {
                            if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Clicked '{resourceImageName}' after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms.");
                                logger.LogInfo($"[{account.AccountName}] ⏳ Waiting 500ms for UI to update before OCR...");
                                await Task.Delay(500, cancellationToken); // Wait 0.5 seconds for OCR accuracy
                                return true;
                            }
                            await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                        }
                    }

                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }
                
                if (scrollAttempt < 2)
                {
                    // Don't swipe on first attempt for stone/iron since we already did
                    if (!(isLaterResource && scrollAttempt == 0))
                    {
                        logger.LogInfo($"[{account.AccountName}] 📜 Resource not found after {MAX_WAIT_TIME_MS}ms. Scrolling resource list...");
                        Point swipeStart = new Point(ResourceScrollArea.Right - 50, ResourceScrollArea.Y + ResourceScrollArea.Height / 2);
                        Point swipeEnd = new Point(ResourceScrollArea.Left + 50, ResourceScrollArea.Y + ResourceScrollArea.Height / 2);
                        await SwipeAsync(account.InstanceNumber, logger, swipeStart, swipeEnd, 300);
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                    }
                }
            }
            logger.LogError($"[{account.AccountName}] ❌ Failed to find '{resourceImageName}' after all scroll attempts.");
            return false;
        }

        /// <summary>
        /// Adjusts the resource level using a robust OCR-first approach with a fallback.
        /// </summary>
        private async Task<bool> AdjustResourceLevelAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, int desiredLevel)
        {
            if (desiredLevel < 1 || desiredLevel > 8) {
                logger.LogError($"[{account.AccountName}] Invalid resource level: {desiredLevel}");
                return false;
            }

            // Define the OCR capture rectangle for the level number
            var numberOnlyRect = new Rectangle(589, 1034, 35, 34);

            // 2. Create an OCR config that ONLY looks for single digits.
            // Create a specialized OCR config for level detection
            var levelOcrConfig = new OCRConfiguration { 
                ScaleFactor = 4,
                AdaptiveC = 5,  // Reduced from 10 to match AutoHuntTask
                MedianBlurKernelSize = 1,
                CharacterWhitelist = "12345678",  // Only expecting level numbers 1-8
                PageSegMode = Tesseract.PageSegMode.SingleChar  // We're only looking for one character
            };
            using var levelOcr = new OCRService(logger, levelOcrConfig);
            
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            // 3. Use the new tight rectangle and specialized OCR service.
            logger.LogInfo($"[{account.AccountName}] Running OCR on rectangle: X={numberOnlyRect.X}, Y={numberOnlyRect.Y}, Width={numberOnlyRect.Width}, Height={numberOnlyRect.Height}");
            logger.LogInfo($"[{account.AccountName}] OCR Configuration: ScaleFactor={levelOcrConfig.ScaleFactor}, AdaptiveC={levelOcrConfig.AdaptiveC}, MedianBlur={levelOcrConfig.MedianBlurKernelSize}");
            string ocrText = levelOcr.ExtractTextFromScreenArea(screenshot, numberOnlyRect);
            logger.LogInfo($"[{account.AccountName}] OCR text for level: '{ocrText}' (Length: {ocrText?.Length ?? 0})");

            // 4. Try to parse the level, but don't fail if OCR fails
            if (!string.IsNullOrEmpty(ocrText))
            {
                var match = Regex.Match(ocrText, @"\d");
                if (match.Success && int.TryParse(match.Value, out int currentLevel))
                {
                    logger.LogInfo($"[{account.AccountName}] Detected current level: {currentLevel}");
                    int clicksNeeded = desiredLevel - currentLevel;

                    if (clicksNeeded == 0) {
                        logger.LogInfo($"[{account.AccountName}] Already at desired level {desiredLevel}.");
                        return true;
                    }

                    var buttonToClick = clicksNeeded > 0 ? LevelPlusButtonRect : LevelMinusButtonRect;
                    string direction = clicksNeeded > 0 ? "Increasing" : "Decreasing";
                    
                    logger.LogInfo($"[{account.AccountName}] {direction} level from {currentLevel} to {desiredLevel}...");
                    for (int i = 0; i < Math.Abs(clicksNeeded); i++)
                    {
                        if (cancellationToken.IsCancellationRequested) return false;
                        if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, buttonToClick)) return false;
                        await Task.Delay(100, cancellationToken);
                    }
                    return true;
                }
            }

            // --- Fallback if OCR fails ---
            logger.LogInfo($"[{account.AccountName}] OCR detection failed. Using reliable click method to set level {desiredLevel}.");
            
            // First reset to level 1 to ensure we start from a known state
            for (int i = 0; i < 7; i++) {
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, LevelMinusButtonRect)) return false;
                await Task.Delay(100, cancellationToken);
            }
            
            // Then click up to desired level
            for (int i = 0; i < desiredLevel - 1; i++) {
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, LevelPlusButtonRect)) return false;
                await Task.Delay(100, cancellationToken);
            }
            
            logger.LogInfo($"[{account.AccountName}] Successfully set level to {desiredLevel} using click method.");
            return true;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private AutoHuntSettings GetAutoHuntSettings(AccountSettings account)
        {
            if (!account.TaskSettings.TryGetValue("AutoHunt", out var settingsJson))
            {
                return new AutoHuntSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AutoHuntSettings>(settingsJson)
                    ?? new AutoHuntSettings();
            }
            catch
            {
                return new AutoHuntSettings();
            }
        }

        private async Task<bool> FindAndClickImageWithTimeoutAsync(string imageName, int instanceNumber, LogService logger, double threshold, int timeoutSeconds, bool useEnhancedMatching, Rectangle? searchArea, CancellationToken cancellationToken)
        {
            var timeoutMs = timeoutSeconds * 1000;
            var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < endTime)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                try
                {
                    var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(200, cancellationToken);
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
                        verboseLogging: false,
                        searchArea: searchArea
                    );

                    logger.LogInfo($"Found '{imageName}' with confidence: {confidence:F3}");

                    if (found)
                    {
                        if (await ClickRandomInRectAsync(instanceNumber, logger, matchRect))
                        {
                            logger.LogInfo($"Successfully clicked {imageName}");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error in FindAndClickImageWithTimeoutAsync: {ex.Message}");
                }

                // Wait 200ms before trying again
                await Task.Delay(200, cancellationToken);
            }

            logger.LogWarning($"Could not find '{imageName}' within {timeoutSeconds} seconds");
            return false;
        }

        private async Task CheckAndActivateGatheringBoostAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Check if gathering boost is enabled
                if (!account.FarmingBoostSettings.EnableGatherBoost)
                {
                    logger.LogInfo($"[{account.AccountName}] 📊 Gathering boost is disabled, skipping boost check");
                    return;
                }

                logger.LogInfo($"[{account.AccountName}] 🚀 Gathering boost enabled ({account.FarmingBoostSettings.SelectedBoostDuration}), checking boost status...");

                // Step 1: Click boost icon in area 0,83 111,187
                var boostIconArea = new Rectangle(0, 83, 111, 104); // width = 111-0=111, height = 187-83=104
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for boost-icon.png...");

                if (await FindAndClickImageWithTimeoutAsync("boost-icon.png", account.InstanceNumber, logger,
                    threshold: 0.6, timeoutSeconds: 3, useEnhancedMatching: false, searchArea: boostIconArea, cancellationToken: cancellationToken))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked boost icon");
                    await Task.Delay(1000, cancellationToken);

                    // Step 2: Click around 517,120 with random variation
                    var clickPoint = new Point(517 + new Random().Next(-3, 4), 120 + new Random().Next(-3, 4));
                    logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking boost area at ({clickPoint.X}, {clickPoint.Y})...");

                    if (await ClickAsync(account.InstanceNumber, logger, clickPoint))
                    {
                        await Task.Delay(1000, cancellationToken);

                        // Step 3: Look for gather-boost.png and check timer
                        logger.LogInfo($"[{account.AccountName}] 🔍 Looking for gather-boost.png...");

                        if (await WaitForImageAsync("gather-boost.png", account.InstanceNumber, logger, timeoutMs: 3000, cancellationToken: cancellationToken))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Found gather-boost interface");

                            // Step 4: Use OCR to check for existing timer in area 311,593 485,632
                            var timerArea = new Rectangle(311, 593, 174, 39); // width = 485-311=174, height = 632-593=39
                            await CheckGatherBoostTimerAsync(account, logger, timerArea, cancellationToken);
                        }
                        else
                        {
                            logger.LogWarning($"[{account.AccountName}] ⚠️ Could not find gather-boost interface, skipping boost activation");
                        }

                        // Step 5: Return to map view by clicking back button
                        var backButtonArea = new Rectangle(48, 39, 10, 10);
                        await ClickBackButtonAndEnsureMapViewAsync(account, logger, backButtonArea, cancellationToken);
                    }
                    else
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click boost area");
                    }
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ Boost icon not found, boost may already be active or unavailable");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error during boost activation: {ex.Message}");
            }
        }

        private async Task CheckGatherBoostTimerAsync(AccountSettings account, LogService logger, Rectangle timerArea, CancellationToken cancellationToken)
        {
            try
            {
                // Take screenshot for OCR
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to take screenshot for timer check");
                    return;
                }

                // Create OCR config for timer detection
                var timerOcrConfig = new OCRConfiguration
                {
                    CharacterWhitelist = "0123456789:",
                    ScaleFactor = 3,
                    PageSegMode = Tesseract.PageSegMode.SingleLine
                };

                using var ocrService = new OCRService(logger, timerOcrConfig);
                string detectedText = ocrService.ExtractTextFromScreenArea(screenshot, timerArea);

                logger.LogInfo($"[{account.AccountName}] 📝 OCR detected timer text: '{detectedText}'");

                // Check if we detected a valid timer format (HH:MM:SS)
                var timePattern = @"^\d{1,2}:\d{2}:\d{2}$";
                if (Regex.IsMatch(detectedText.Trim(), timePattern))
                {
                    logger.LogInfo($"[{account.AccountName}] ⏰ Active boost detected with timer: {detectedText.Trim()}");

                    // Click back button and exit - boost already active
                    var backButtonArea = new Rectangle(53, 38, 10, 10);
                    await ClickBackButtonAndEnsureMapViewAsync(account, logger, backButtonArea, cancellationToken);
                    return;
                }

                logger.LogInfo($"[{account.AccountName}] ⚠️ No active boost timer detected, attempting to activate boost...");

                // Click where we checked for timer to activate boost selection
                var timerClickPoint = new Point(timerArea.X + timerArea.Width / 2, timerArea.Y + timerArea.Height / 2);
                await ClickAsync(account.InstanceNumber, logger, timerClickPoint);
                await Task.Delay(500, cancellationToken);

                // Determine which use button to look for based on boost duration setting
                Rectangle useButtonArea;
                string boostType;

                if (account.FarmingBoostSettings.SelectedBoostDuration == BoostDuration.EightHour)
                {
                    // Expanded 8-hour use button area - added 30px padding on all sides
                    useButtonArea = new Rectangle(467, 665, 242, 146); // original: 497,695,182,86 -> expanded by 30px each side
                    boostType = "8-hour";
                }
                else // TwentyFourHour
                {
                    // Expanded 24-hour use button area - added 30px padding on all sides
                    useButtonArea = new Rectangle(469, 817, 240, 142); // original: 499,847,180,82 -> expanded by 30px each side
                    boostType = "24-hour";
                }

                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for {boostType} use button...");


                if (await FindAndClickImageWithTimeoutAsync("use-button.png", account.InstanceNumber, logger,
                    threshold: 0.6, timeoutSeconds: 3, useEnhancedMatching: false, searchArea: useButtonArea, cancellationToken: cancellationToken))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully clicked {boostType} use button");
                    await Task.Delay(1000, cancellationToken);

                    // Check for timer again after activation
                    using var ocrService2 = new OCRService(logger, timerOcrConfig);
                    var screenshot2 = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot2 != null)
                    {
                        string newTimerText = ocrService2.ExtractTextFromScreenArea(screenshot2, timerArea);
                        logger.LogInfo($"[{account.AccountName}] 📝 Timer after activation: '{newTimerText}'");

                        if (Regex.IsMatch(newTimerText.Trim(), timePattern))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Boost successfully activated with timer: {newTimerText.Trim()}");
                        }
                        else
                        {
                            logger.LogWarning($"[{account.AccountName}] ⚠️ Boost activation unclear, may need to purchase with gems");
                            await HandleBoostPurchaseAsync(account, logger, cancellationToken);
                        }
                    }
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Could not find {boostType} use button");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error checking boost timer: {ex.Message}");
            }
        }

        private async Task HandleBoostPurchaseAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 💎 Attempting to purchase boost with gems...");

                // Look for buy-gather button
                if (await FindAndClickImageWithTimeoutAsync("buy-gather.png", account.InstanceNumber, logger,
                    threshold: 0.6, timeoutSeconds: 3, useEnhancedMatching: false, searchArea: null, cancellationToken: cancellationToken))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked buy-gather button");
                    await Task.Delay(1000, cancellationToken);

                    // Look for gems-buy button
                    if (await FindAndClickImageWithTimeoutAsync("gems-buy.png", account.InstanceNumber, logger,
                        threshold: 0.6, timeoutSeconds: 3, useEnhancedMatching: false, searchArea: null, cancellationToken: cancellationToken))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully purchased boost with gems");
                        await Task.Delay(1000, cancellationToken);
                    }
                    else
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Could not find gems-buy button");
                    }
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Could not find buy-gather button");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error during boost purchase: {ex.Message}");
            }
        }

        /// <summary>
        /// Clicks back button and ensures we're in map view, retrying if necessary
        /// </summary>
        private async Task ClickBackButtonAndEnsureMapViewAsync(AccountSettings account, LogService logger, Rectangle backButtonArea, CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            int attempts = 0;

            while (attempts < maxRetries)
            {
                attempts++;
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking back button (attempt {attempts}/{maxRetries}) to return to map...");

                await ClickRandomInRectAsync(account.InstanceNumber, logger, backButtonArea);
                await Task.Delay(1000, cancellationToken); // Wait 1 second as requested

                // Check if we're in map view by looking for map-view indicators
                if (await CheckIfInMapViewAsync(account.InstanceNumber, logger, cancellationToken))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully returned to map view");
                    return;
                }

                logger.LogWarning($"[{account.AccountName}] ⚠️ Not in map view after clicking back button, attempt {attempts}/{maxRetries}");

                if (attempts < maxRetries)
                {
                    await Task.Delay(500, cancellationToken); // Brief delay before retry
                }
            }

            logger.LogError($"[{account.AccountName}] ❌ Failed to return to map view after {maxRetries} attempts");
        }

        /// <summary>
        /// Checks if we're currently in map view by looking for map-specific UI elements
        /// </summary>
        private async Task<bool> CheckIfInMapViewAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) return false;

                // Use the existing map-view.png template from locator folder
                var mapViewTemplatePath = Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator", "map-view.png");
                var (mapViewFound, _, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    mapViewTemplatePath,
                    instanceNumber,
                    threshold: 0.6
                );

                if (mapViewFound)
                {
                    logger.LogInfo($"Map view detected with confidence: {confidence:F3}");
                    return true;
                }

                // Fallback: Look for gather-button as a map indicator (this exists in farming folder)
                var gatherButtonArea = new Rectangle(610, 1185, 80, 80);
                var (gatherFound, _, gatherConfidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    Path.Combine(ImageTemplateFolder, "gather-button.png"),
                    instanceNumber,
                    threshold: 0.6,
                    searchArea: gatherButtonArea
                );

                if (gatherFound)
                {
                    logger.LogInfo($"Gather button detected with confidence: {gatherConfidence:F3}");
                    return true;
                }

                logger.LogInfo($"Map view not detected (map-view confidence: {confidence:F3}, gather-button confidence: {gatherConfidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error checking map view: {ex.Message}");
                return false; // Assume not in map view if check fails
            }
        }
    }
}