using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.ImageDetection;
using Bot.Core.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    public class AutoRallyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoRally;
        public override string Name => "Auto Rally";

        // Static session state storage - keyed by account ID for proper isolation
        private static readonly ConcurrentDictionary<string, int> MarchesUsed = new();
        private static readonly ConcurrentDictionary<string, DateTime> LastExecutionTimes = new();
        
        private readonly string _templateFolder = string.Empty;
        private new UnifiedTemplateMatchingService? _templateMatcher;
        private const int SEARCH_TIMEOUT_MS = 2000; // 2 seconds for image searches (as requested)
        private const int SEARCH_INTERVAL_MS = 500; // Check every 0.5 seconds (4 attempts total)
        private const double TEMPLATE_MATCH_THRESHOLD = 0.6; // Threshold for template matching
        
        // Rally icon search area
        private static readonly Rectangle RallyIconSearchArea = new Rectangle(597, 486, 117, 121); // From 597,486 to 714,607

        // Green plus search area - rally list area only
        private static readonly Rectangle GreenPlusSearchArea = new Rectangle(572, 164, 146, 1110); // From 572,164 to 718,1274

        // Auto Join OCR areas
        private static readonly Rectangle AutoJoinTextArea = new Rectangle(225, 1188, 267, 67); // 225,1188 to 492,1255
        private static readonly Rectangle EnableTextArea = new Rectangle(408, 965, 134, 42); // 408,965 to 542,1007
        private static readonly Rectangle TimerTextArea = new Rectangle(287, 1138, 137, 34); // 287,1138 to 424,1172
        private static readonly Rectangle StopTextArea = new Rectangle(617, 868, 99, 30); // 617,868 to 716,868 (estimated height)
        private static readonly Point CloseAutoJoinPoint = new Point(642, 259); // Close button coordinate

        // Template image names
        private const string RALLY_ICON = "rally-icon.png";
        private const string ALLIANCE_ICON = "alliance-icon.png";
        private const string WAR_ICON = "war-icon.png";
        private const string GREEN_PLUS = "green-plus.png";
        private const string EQUALIZE = "equalize.png";
        private const string DEPLOY = "deploy.png";
        private const string RALLY_MENU = "rally-menu.png";
        private const string ERROR_JOIN = "confirm-join.png";
        private const string BACK_BUTTON = "back.png";
        private const string MAX_MARCH = "max-march.png";

        public AutoRallyTask()
        {
            try
            {
                // Get the application's base directory and combine with template path
                string baseDir = AppContext.BaseDirectory;
                _templateFolder = Path.Combine(baseDir, "templates", "images", "Auto Rally");

                // Ensure template directory exists
                if (!Directory.Exists(_templateFolder))
                {
                    Directory.CreateDirectory(_templateFolder);
                }


                LoadExecutionTimes();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoRallyTask] Error during initialization: {ex.Message}");
            }
        }


        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🎯 Starting Auto Rally task");
                
                userNotifications?.ShowStatus("Starting Auto Rally task...", NotificationType.Info);

                // Check for pause before starting
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");

                // Get unique account ID for accurate timer tracking (moved to beginning)
                var accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                if (string.IsNullOrEmpty(accountId))
                {
                    logger.LogWarning($"[{account.AccountName}] No cached account ID found, using fallback key");
                    accountId = $"Account_{account.AccountName}_{account.InstanceNumber}";
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Using cached account ID: '{accountId}'");
                }

                // Get AutoRally settings
                var settings = account.AutoRallySettings;

                // Step 1: Handle Auto Join if enabled and timing allows
                if (settings.AutoJoin)
                {
                    if (ShouldCheckAutoJoin(settings, account.AccountName, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] 🤖 Auto Join enabled and timing check passed, setting up auto join...");
                        var autoJoinResult = await HandleAutoJoinAsync(account, logger, cancellationToken, userNotifications);
                        
                        // Update the last check time regardless of success/failure
                        settings.LastAutoJoinCheck = DateTime.UtcNow;
                        
                        if (!autoJoinResult.Success)
                        {
                            return autoJoinResult; // Return early if auto join setup failed
                        }
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] 🤖 Auto Join enabled but timing check not due yet, skipping auto join setup");
                    }
                }

                // Step 2: Look for rally icon
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for rally icon...");
                
                // Ensure we're in the correct view before looking for rally icon
                logger.LogInfo($"[{account.AccountName}] 🏠 Ensuring correct view before rally icon search...");
                var locatorService = new LocatorService(logger, account);
                
                var currentView = await locatorService.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] 📍 Current view: {currentView}");
                
                if (currentView != ViewType.BaseView && currentView != ViewType.MapView)
                {
                    logger.LogInfo($"[{account.AccountName}] 🔄 Not in base/map view, attempting to navigate to base view...");
                    if (!await locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Could not navigate to base view, proceeding anyway...");
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully navigated to base view");
                    }
                    
                    await Task.Delay(1000, cancellationToken); // Wait for view transition
                }
                
                var rallyIconPath = Path.Combine(_templateFolder, RALLY_ICON);
                
                // Check if the image file exists
                if (!File.Exists(rallyIconPath))
                {
                    logger.LogError($"[{account.AccountName}] ❌ Rally icon template not found: {RALLY_ICON}");
                    userNotifications?.ShowError($"Rally icon template not found: {RALLY_ICON}");
                    return TaskExecutionDetails.Failed($"Rally icon template not found: {RALLY_ICON}");
                }
                
                // Check if rally icon is present on screen first (without clicking)
                var rallyIconPresent = await IsImageFoundAsync(account, logger, cancellationToken, rallyIconPath, "rally icon", SEARCH_TIMEOUT_MS, RallyIconSearchArea);
                
                if (!rallyIconPresent)
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Could not find rally icon on screen");
                    logger.LogInfo($"[{account.AccountName}] Search parameters:");
                    logger.LogInfo($"[{account.AccountName}] - Timeout: {SEARCH_TIMEOUT_MS}ms");
                    logger.LogInfo($"[{account.AccountName}] - Interval: {SEARCH_INTERVAL_MS}ms");
                    logger.LogInfo($"[{account.AccountName}] - Threshold: {TEMPLATE_MATCH_THRESHOLD}");
                    
                    userNotifications?.ShowWarning("Rally icon not found on screen");
                    
                    // Save execution time even on failure
                    LastExecutionTimes[accountId] = DateTime.Now;
                    await SaveExecutionTimesAsync();
                    
                    return new TaskExecutionDetails(true, message: "Rally icon not found - no rallies available");
                }
                
                // Check available marches before doing anything else (skip if AutoJoin is enabled)
                if (!settings.AutoJoin)
                {
                    int initialMarches = await GetAvailableMarchesAsync(account, logger, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Initial march check: {initialMarches} marches available");
                    if (initialMarches <= 0)
                    {
                        return TaskExecutionDetails.Failed("No marches available at start, skipping Auto Rally module");
                    }
                }
                
                // Now click the rally icon since we have marches available
                var rallyIconFound = await FindAndClickWithTimeoutAsync(
                    account, logger, cancellationToken, rallyIconPath, 
                    "rally icon", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS);

                if (!rallyIconFound)
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Could not click rally icon");
                    userNotifications?.ShowWarning("Failed to click rally icon");
                    
                    // Save execution time even on failure
                    LastExecutionTimes[accountId] = DateTime.Now;
                    await SaveExecutionTimesAsync();
                    
                    return TaskExecutionDetails.Failed("Failed to click rally icon");
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Successfully found and clicked rally icon");
                await Task.Delay(1000, cancellationToken); // Wait for rally screen to load

                // Initialize march counter for this account (using cached account ID for proper isolation)
                MarchesUsed.TryAdd(accountId, 0);

                // Try to join rallies until we run out of marches or find no more rallies
                int rallyCount = 0;
                int maxRallies = 10; // Safety limit to prevent infinite loops
                
                while (!cancellationToken.IsCancellationRequested && rallyCount < maxRallies)
                {
                    logger.LogInfo($"[{account.AccountName}] 🎯 Attempting to join rally #{rallyCount + 1}...");
                    
                    var joinResult = await TryJoinRallyAsync(account, logger, cancellationToken);
                    
                    if (joinResult == RallyJoinResult.NoMoreRallies)
                    {
                        logger.LogInfo($"[{account.AccountName}] 🏁 No more rallies available to join");
                        break;
                    }
                    
                    if (joinResult == RallyJoinResult.MaxMarchesReached)
                    {
                        logger.LogInfo($"[{account.AccountName}] 🎖️ Maximum marches reached");
                        break;
                    }
                    
                    if (joinResult == RallyJoinResult.Success)
                    {
                        rallyCount++;
                        MarchesUsed[accountId]++;
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully joined rally #{rallyCount}, total marches used: {MarchesUsed[accountId]}");
                        userNotifications?.ShowStatus($"Joined rally #{rallyCount}", NotificationType.Info);
                    }
                    else if (joinResult == RallyJoinResult.Error)
                    {
                        rallyCount++; // Increment counter to move to next rally even on error
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Rally join error occurred, continuing to next rally...");
                    }

                    await Task.Delay(1000, cancellationToken); // Brief pause between rally attempts
                }

                // Click back to exit rally screen
                logger.LogInfo($"[{account.AccountName}] 🔚 Exiting rally screen...");
                var backButtonPath = Path.Combine(_templateFolder, BACK_BUTTON);
                await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, backButtonPath, "back button", 2000, SEARCH_INTERVAL_MS);
                await Task.Delay(500, cancellationToken); // Wait for navigation to complete
                
                // Save execution time
                LastExecutionTimes[accountId] = DateTime.Now;
                await SaveExecutionTimesAsync();

                var totalMarches = MarchesUsed[accountId];
                string message = rallyCount > 0 ? 
                    $"Joined {rallyCount} rallies, used {totalMarches} marches" : 
                    "No rallies joined - none available or max marches reached";

                logger.LogInfo($"[{account.AccountName}] 🎉 Auto Rally task completed: {message}");
                userNotifications?.ShowSuccess($"Auto Rally completed: {message}");

                return new TaskExecutionDetails(true, message: message);
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in Auto Rally task: {ex.Message}");
                logger.LogError($"[{account.AccountName}] Stack trace: {ex.StackTrace}");
                userNotifications?.ShowError($"Auto Rally task failed: {ex.Message}");
                return TaskExecutionDetails.Failed($"Auto Rally task failed: {ex.Message}");
            }
        }

        #region Execution Time Tracking

        private void LoadExecutionTimes()
        {
            try
            {
                string filePath = Path.Combine("data", "auto_rally_execution_times.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var times = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                    if (times != null)
                    {
                        foreach (var kvp in times)
                        {
                            LastExecutionTimes[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoRallyTask] Error loading execution times: {ex.Message}");
            }
        }

        private async Task SaveExecutionTimesAsync()
        {
            try
            {
                Directory.CreateDirectory("data");
                string filePath = Path.Combine("data", "auto_rally_execution_times.json");
                var times = LastExecutionTimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                string json = JsonSerializer.Serialize(times, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoRallyTask] Error saving execution times: {ex.Message}");
            }
        }

        #endregion

        private async Task<RallyJoinResult> TryJoinRallyAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Step 1: Look for green plus button
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for green plus button...");
                if (!await FindGreenPlusButtonAsync(account, logger, cancellationToken))
                {
                    logger.LogInfo($"[{account.AccountName}] 🚫 No green plus button found - no more rallies available");
                    return RallyJoinResult.NoMoreRallies;
                }

                await Task.Delay(500, cancellationToken); // Wait for click to register

                // Step 2: Look for equalize or max march button
                logger.LogInfo($"[{account.AccountName}] 🔍 Checking for equalize or max march buttons...");
                
                var equalizePath = Path.Combine(_templateFolder, EQUALIZE);
                var maxMarchPath = Path.Combine(_templateFolder, MAX_MARCH);
                
                // Check for max march first (indicates we're at capacity)
                if (await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, maxMarchPath, "max march", 1000, SEARCH_INTERVAL_MS))
                {
                    logger.LogInfo($"[{account.AccountName}] 🎖️ Max marches reached - clicking back");
                    await Task.Delay(500, cancellationToken);
                    var backPath = Path.Combine(_templateFolder, BACK_BUTTON);
                    await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, backPath, "back button", 2000, SEARCH_INTERVAL_MS);
                    await Task.Delay(500, cancellationToken); // Wait for navigation to complete
                    return RallyJoinResult.MaxMarchesReached;
                }
                
                // Look for equalize button
                if (!await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, equalizePath, "equalize", 2000, SEARCH_INTERVAL_MS))
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Equalize button not found");
                    return RallyJoinResult.Error;
                }

                await Task.Delay(500, cancellationToken);

                // Step 3: Look for and click deploy button
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for deploy button...");
                var deployPath = Path.Combine(_templateFolder, DEPLOY);
                if (!await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, deployPath, "deploy", 2000, SEARCH_INTERVAL_MS))
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Deploy button not found");
                    return RallyJoinResult.Error;
                }

                await Task.Delay(1000, cancellationToken); // Wait for deploy to process

                // Step 4: Look for and click confirm join button first
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for confirm join button...");
                var errorJoinPath = Path.Combine(_templateFolder, ERROR_JOIN);
                
                if (await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, errorJoinPath, "confirm join", 2000, SEARCH_INTERVAL_MS))
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Confirm join dialog detected and clicked - rally join failed");
                    await Task.Delay(1000, cancellationToken);
                    return RallyJoinResult.Error; // Will retry from green plus stage
                }

                // Step 5: If no confirm join button found, check for rally menu (success)
                logger.LogInfo($"[{account.AccountName}] 🔍 No confirm join button found, checking for rally menu...");
                var rallyMenuPath = Path.Combine(_templateFolder, RALLY_MENU);
                
                if (await IsImageFoundAsync(account, logger, cancellationToken, rallyMenuPath, "rally menu", 2000))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Rally menu detected - join successful!");
                    return RallyJoinResult.Success;
                }
                
                logger.LogWarning($"[{account.AccountName}] ❓ Neither confirm join nor rally menu found - assuming error");
                return RallyJoinResult.Error;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in TryJoinRallyAsync: {ex.Message}");
                return RallyJoinResult.Error;
            }
        }

        private async Task<bool> FindGreenPlusButtonAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var greenPlusPath = Path.Combine(_templateFolder, GREEN_PLUS);
            var rallyMenuPath = Path.Combine(_templateFolder, RALLY_MENU);

            // First attempt - check rally menu visibility, then look for green plus without scrolling
            logger.LogInfo($"[{account.AccountName}] 🔍 First attempt: checking rally menu visibility...");
            if (!await IsRallyMenuVisibleAsync(account, logger, cancellationToken, rallyMenuPath))
            {
                logger.LogWarning($"[{account.AccountName}] ❌ Rally menu not visible, exiting green plus search");
                return false;
            }
            
            logger.LogInfo($"[{account.AccountName}] 🔍 Rally menu visible, looking for green plus button...");
            if (await FindAndClickGreenPlusWithTimeoutAsync(account, logger, cancellationToken, greenPlusPath, "green plus", 2000, SEARCH_INTERVAL_MS, GreenPlusSearchArea))
            {
                logger.LogInfo($"[{account.AccountName}] ✅ Found green plus button on first attempt");
                return true;
            }

            // Scroll attempts (2 times as specified)
            const int maxScrollAttempts = 2;
            for (int attempt = 1; attempt <= maxScrollAttempts; attempt++)
            {
                logger.LogInfo($"[{account.AccountName}] 📜 Green plus not found, checking rally menu before scroll attempt {attempt}/{maxScrollAttempts}");
                
                // Check rally menu visibility before scrolling
                if (!await IsRallyMenuVisibleAsync(account, logger, cancellationToken, rallyMenuPath))
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Rally menu not visible before scroll attempt {attempt}, exiting");
                    return false;
                }
                
                // Scroll from bottom up (swipe up)
                await ScrollUpAsync(account.InstanceNumber, logger, cancellationToken);
                await Task.Delay(1000, cancellationToken); // Wait for scroll to complete

                // Check rally menu visibility after scrolling
                logger.LogInfo($"[{account.AccountName}] 🔍 Checking rally menu visibility after scroll attempt {attempt}...");
                if (!await IsRallyMenuVisibleAsync(account, logger, cancellationToken, rallyMenuPath))
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Rally menu not visible after scroll attempt {attempt}, exiting");
                    return false;
                }

                // Look for green plus after scrolling (using color-sensitive detection)
                logger.LogInfo($"[{account.AccountName}] 🔍 Rally menu visible, looking for green plus after scroll attempt {attempt}...");
                if (await FindAndClickGreenPlusWithTimeoutAsync(account, logger, cancellationToken, greenPlusPath, "green plus", 2000, SEARCH_INTERVAL_MS, GreenPlusSearchArea))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Found green plus button after scroll attempt {attempt}");
                    return true;
                }
            }

            // If still not found after scrolling, click back
            logger.LogInfo($"[{account.AccountName}] 🚫 Green plus button not found after {maxScrollAttempts} scroll attempts - clicking back");
            var backPath = Path.Combine(_templateFolder, BACK_BUTTON);
            await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, backPath, "back button", 2000, SEARCH_INTERVAL_MS);
            await Task.Delay(500, cancellationToken); // Wait for navigation to complete
            
            return false;
        }

        private async Task ScrollUpAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Scroll from bottom to top (coordinates for a typical 720x1280 screen)
                int startX = 360; // Center of screen horizontally
                int startY = 1000; // Near bottom of screen
                int endX = 360; // Same horizontal position
                int endY = 400; // Higher up on screen
                
                logger.LogInfo($"Scrolling up on instance {instanceNumber}: ({startX},{startY}) → ({endX},{endY})");
                await SwipeAsync(instanceNumber, logger, new Point(startX, startY), new Point(endX, endY), 800); // 800ms swipe duration
            }
            catch (Exception ex)
            {
                logger.LogError($"Error scrolling up on instance {instanceNumber}: {ex.Message}");
            }
        }

        private async Task<bool> IsRallyMenuVisibleAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string rallyMenuPath)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 👀 Checking rally menu visibility (grayscale)...");
                
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) return false;

                if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                
                // Use grayscale matching for fast detection
                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    rallyMenuPath,
                    account.InstanceNumber,
                    threshold: TEMPLATE_MATCH_THRESHOLD,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    isUIElement: false, // Use grayscale (not color) for fast detection
                    verboseLogging: false // Reduce logging for frequent checks
                );

                if (found)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Rally menu visible (confidence: {confidence:F3})");
                    return true;
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] ❌ Rally menu not visible (confidence: {confidence:F3})");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error checking rally menu visibility: {ex.Message}");
                return false; // Err on the side of caution
            }
        }

        private async Task<bool> IsImageFoundAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string templatePath, string templateName, int timeoutMs, Rectangle? searchArea = null)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                int attemptCount = 0;

                while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs && !cancellationToken.IsCancellationRequested)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return false;
                    
                    attemptCount++;
                    logger.LogInfo($"[{account.AccountName}] 👀 Checking for {templateName} (attempt {attemptCount})...");
                    
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(SEARCH_INTERVAL_MS, cancellationToken);
                        continue;
                    }

                    if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                    
                    logger.LogInfo($"[{account.AccountName}] 🔍 Searching for {templateName} in area: {(searchArea?.ToString() ?? "full screen")}");
                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        templatePath,
                        account.InstanceNumber,
                        threshold: TEMPLATE_MATCH_THRESHOLD,
                        scales: UnifiedTemplateMatchingService.StandardScales,
                        searchArea: searchArea,
                        verboseLogging: true
                    );

                    if (found)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found {templateName} with confidence {confidence:F3}");
                        return true;
                    }

                    await Task.Delay(SEARCH_INTERVAL_MS, cancellationToken);
                }

                logger.LogInfo($"[{account.AccountName}] ⏱️ {templateName} not found within {timeoutMs}ms timeout");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error checking for {templateName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Specialized method for finding and clicking green plus buttons with color-sensitive detection
        /// This avoids clicking on greyed-out/disabled plus buttons
        /// </summary>
        private async Task<bool> FindAndClickGreenPlusWithTimeoutAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            string templatePath, 
            string templateName, 
            int timeoutMs, 
            int intervalMs,
            Rectangle? searchArea = null)
        {
            var startTime = DateTime.UtcNow;
            int attemptCount = 0;
            double bestConfidence = 0.0;

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs && !cancellationToken.IsCancellationRequested)
            {
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return false;
                
                attemptCount++;
                logger.LogInfo($"[{account.AccountName}] 👀 Looking for ACTIVE {templateName} (attempt {attemptCount})...");
                
                var screenshotBytes = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshotBytes == null)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Screenshot failed for {templateName} attempt {attemptCount}");
                    await Task.Delay(intervalMs, cancellationToken);
                    continue;
                }

                // Convert byte array to bitmap for color validation
                System.Drawing.Bitmap? screenshot = null;
                try
                {
                    using var ms = new System.IO.MemoryStream(screenshotBytes);
                    screenshot = new System.Drawing.Bitmap(ms);
                    
                    if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                    
                    logger.LogInfo($"[{account.AccountName}] 🔍 Searching for {templateName} in area {searchArea?.ToString() ?? "full screen"}...");
                    
                    // Use UI element detection with color matching for better accuracy
                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshotBytes, // Template matching uses byte array
                        templatePath,
                        account.InstanceNumber,
                        threshold: 0.25,
                        scales: UnifiedTemplateMatchingService.StandardScales,
                        searchArea: searchArea,
                        isUIElement: true,
                        verboseLogging: false
                    );
                    

                    // Track best confidence found across all attempts
                    if (confidence > bestConfidence)
                        bestConfidence = confidence;

                    // Always log the confidence for debugging
                    logger.LogInfo($"[{account.AccountName}] 🎯 Template match result: found={found}, confidence={confidence:F3}, threshold=0.25");

                    if (found)
                    {
                        // Simple validation: Check if the matched area is green (not grey)
                        if (IsGreenNotGrey(screenshot, matchRect, logger, account.AccountName))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Found GREEN {templateName} at {matchRect} with confidence {confidence:F3}");
                            
                            logger.LogInfo($"[{account.AccountName}] 🎯 Clicking GREEN {templateName} at rectangle {matchRect}");
                            if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Successfully clicked GREEN {templateName} after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                                return true;
                            }
                            else
                            {
                                logger.LogWarning($"[{account.AccountName}] ⚠️ Found GREEN {templateName} at {matchRect} but click failed");
                            }
                        }
                        else
                        {
                            logger.LogInfo($"[{account.AccountName}] 🔍 Found GREY {templateName} at {matchRect} with confidence {confidence:F3} - skipping (button disabled)");
                        }
                    }
                    else
                    {
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to process screenshot: {ex.Message}");
                }
                finally
                {
                    screenshot?.Dispose();
                }

                await Task.Delay(intervalMs, cancellationToken);
            }

            logger.LogInfo($"[{account.AccountName}] ⏱️ {templateName} not found within {timeoutMs}ms timeout (best confidence: {bestConfidence:F3}, threshold: 0.25)");
            
            return false;
        }

        /// <summary>
        /// Simple check: Is the button region more green than grey?
        /// </summary>
        private bool IsGreenNotGrey(System.Drawing.Bitmap screenshot, System.Drawing.Rectangle region, LogService logger, string accountName)
        {
            try
            {
                // Extract the region of interest
                var regionBitmap = new System.Drawing.Bitmap(region.Width, region.Height);
                using (var g = System.Drawing.Graphics.FromImage(regionBitmap))
                {
                    g.DrawImage(screenshot, new System.Drawing.Rectangle(0, 0, region.Width, region.Height), region, System.Drawing.GraphicsUnit.Pixel);
                }

                int greenPixels = 0;
                int greyPixels = 0;
                int totalPixels = 0;
                
                for (int x = 0; x < regionBitmap.Width; x++)
                {
                    for (int y = 0; y < regionBitmap.Height; y++)
                    {
                        var pixel = regionBitmap.GetPixel(x, y);
                        totalPixels++;
                        
                        // Check if pixel is green-ish (green component higher than red and blue)
                        bool isGreen = pixel.G > pixel.R + 10 && pixel.G > pixel.B + 10 && pixel.G > 60;
                        
                        // Check if pixel is grey-ish (R, G, B values are close to each other)
                        bool isGrey = Math.Abs(pixel.R - pixel.G) <= 20 && Math.Abs(pixel.G - pixel.B) <= 20 && Math.Abs(pixel.R - pixel.B) <= 20;
                        
                        if (isGreen && !isGrey)
                        {
                            greenPixels++;
                        }
                        else if (isGrey)
                        {
                            greyPixels++;
                        }
                    }
                }
                
                logger.LogInfo($"[{accountName}] 🎨 Color analysis: Green pixels: {greenPixels}, Grey pixels: {greyPixels}, Total: {totalPixels}");
                
                regionBitmap.Dispose();
                
                // Button is green if it has more green pixels than grey pixels
                bool isGreenButton = greenPixels > greyPixels;
                logger.LogInfo($"[{accountName}] 🎨 Button color: {(isGreenButton ? "GREEN" : "GREY")} (Green: {greenPixels} vs Grey: {greyPixels})");
                
                return isGreenButton;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{accountName}] ❌ Error checking button color: {ex.Message}");
                return false; // Err on the side of caution - don't click if we can't determine color
            }
        }


        private enum RallyJoinResult
        {
            Success,
            Error,
            NoMoreRallies,
            MaxMarchesReached
        }

        private bool ShouldCheckAutoJoin(AutoRallySettings settings, string accountName, LogService logger)
        {
            var now = DateTime.UtcNow;
            
            // If never checked before, allow the check
            if (settings.LastAutoJoinCheck == null)
            {
                logger.LogInfo($"[{accountName}] ⏰ First auto join check, proceeding");
                return true;
            }
            
            // Calculate time since last check
            var timeSinceLastCheck = now - settings.LastAutoJoinCheck.Value;
            var requiredInterval = TimeSpan.FromHours(settings.AutoJoinCheckIntervalHours);
            
            logger.LogInfo($"[{accountName}] ⏰ Auto join timing: {timeSinceLastCheck.TotalHours:F1}h since last check, {settings.AutoJoinCheckIntervalHours}h interval required");
            
            if (timeSinceLastCheck >= requiredInterval)
            {
                logger.LogInfo($"[{accountName}] ✅ Auto join check interval met, proceeding");
                return true;
            }
            else
            {
                var timeUntilNext = requiredInterval - timeSinceLastCheck;
                logger.LogInfo($"[{accountName}] ⏱️ Auto join check not due for {timeUntilNext.TotalMinutes:F0} more minutes");
                return false;
            }
        }

        private async Task<bool> NavigateToRallyScreenAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Ensure we're in the correct view (base or map view) before looking for rally icon
                logger.LogInfo($"[{account.AccountName}] 🏠 Ensuring correct view before rally icon search...");
                var locatorService = new LocatorService(logger, account);
                
                // Try to get to base or map view
                var currentView = await locatorService.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] 📍 Current view: {currentView}");
                
                if (currentView != ViewType.BaseView && currentView != ViewType.MapView)
                {
                    logger.LogInfo($"[{account.AccountName}] 🔄 Not in base/map view, attempting to navigate to base view...");
                    if (!await locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Could not navigate to base view, proceeding anyway...");
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully navigated to base view");
                    }
                    
                    await Task.Delay(1000, cancellationToken); // Wait for view transition
                }

                // First try: Look for rally icon directly
                var rallyIconPath = Path.Combine(_templateFolder, RALLY_ICON);
                logger.LogInfo($"[{account.AccountName}] 🔍 First attempt: Looking for rally icon directly...");
                
                if (await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, rallyIconPath, "rally icon", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully clicked rally icon directly");
                    return true;
                }

                // Second try: Navigate via alliance → war → rally
                logger.LogInfo($"[{account.AccountName}] 🔍 Rally icon not found, trying alliance → war navigation...");
                
                // Ensure we're in the correct view before looking for alliance icon
                logger.LogInfo($"[{account.AccountName}] 🏠 Re-checking view before alliance icon search...");
                currentView = await locatorService.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] 📍 Current view: {currentView}");
                
                if (currentView != ViewType.BaseView && currentView != ViewType.MapView)
                {
                    logger.LogInfo($"[{account.AccountName}] 🔄 Not in base/map view for alliance search, attempting to navigate to base view...");
                    if (!await locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Could not navigate to base view for alliance search, proceeding anyway...");
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully navigated to base view for alliance search");
                    }
                    
                    await Task.Delay(1000, cancellationToken); // Wait for view transition
                }
                
                // Step 1: Click alliance icon
                var allianceIconPath = Path.Combine(_templateFolder, ALLIANCE_ICON);
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for alliance icon...");
                
                if (!await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, allianceIconPath, "alliance icon", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS))
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Alliance icon not found");
                    return false;
                }

                await Task.Delay(1000, cancellationToken); // Wait for alliance screen to load

                // Step 2: Click war icon
                var warIconPath = Path.Combine(_templateFolder, WAR_ICON);
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for war icon...");
                
                if (!await FindAndClickWithTimeoutAsync(account, logger, cancellationToken, warIconPath, "war icon", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS))
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ War icon not found");
                    return false;
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Successfully navigated to rally screen via alliance → war");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error navigating to rally screen: {ex.Message}");
                return false;
            }
        }

        private async Task<TaskExecutionDetails> HandleAutoJoinAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🤖 Starting Auto Join setup...");

                // Step 1: Try to navigate to rally screen (rally icon or alliance→war path)
                if (!await NavigateToRallyScreenAsync(account, logger, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Could not navigate to rally screen for Auto Join setup");
                }

                await Task.Delay(1000, cancellationToken); // Wait for rally screen to load

                // Step 2: Check for Auto-Join or Auto-joining text
                using var ocrService = new OCRService(logger, new OCRConfiguration
                {
                    ScaleFactor = 3,
                    AdaptiveC = 7,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-. :"
                });

                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    return TaskExecutionDetails.Failed("Failed to take screenshot for Auto Join setup");
                }

                logger.LogInfo($"[{account.AccountName}] 📖 Reading Auto Join text from area: {AutoJoinTextArea}");
                string autoJoinText = ocrService.ExtractTextFromScreenArea(screenshot, AutoJoinTextArea);
                logger.LogInfo($"[{account.AccountName}] Auto Join OCR result: '{autoJoinText}'");

                // More flexible string matching to handle OCR variations
                var cleanedText = autoJoinText.Replace(" ", "").Replace("-", "").ToLower();
                
                if (cleanedText.Contains("autojoining") || autoJoinText.Contains("joining", StringComparison.OrdinalIgnoreCase))
                {
                    // Already auto-joining, check timer
                    logger.LogInfo($"[{account.AccountName}] 🔄 Already auto-joining rallies detected, checking timer...");
                    return await HandleExistingAutoJoinAsync(account, logger, cancellationToken, ocrService);
                }
                else if (cleanedText.Contains("autojoin") || autoJoinText.Contains("Auto-Join", StringComparison.OrdinalIgnoreCase))
                {
                    // Need to enable auto join
                    logger.LogInfo($"[{account.AccountName}] 🎯 Found Auto-Join option, enabling...");
                    return await EnableAutoJoinAsync(account, logger, cancellationToken, ocrService);
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Auto-Join option not found in OCR text: '{autoJoinText}'");
                    logger.LogInfo($"[{account.AccountName}] 📝 Cleaned text for matching: '{cleanedText}'");
                    logger.LogInfo($"[{account.AccountName}] 🎯 Clicking close auto-join dialog at {CloseAutoJoinPoint}");
                    await ClickAsync(account.InstanceNumber, logger, CloseAutoJoinPoint); // Close dialog
                    await Task.Delay(500, cancellationToken);
                    return new TaskExecutionDetails(true, message: "Auto-Join option not available, continuing with manual rally");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in HandleAutoJoinAsync: {ex.Message}");
                return TaskExecutionDetails.Failed($"Auto Join setup failed: {ex.Message}");
            }
        }

        private async Task<TaskExecutionDetails> HandleExistingAutoJoinAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, OCRService ocrService)
        {
            try
            {
                // Read timer from TimerTextArea (287,1138 to 424,1172)
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    return TaskExecutionDetails.Failed("Failed to take screenshot for timer check");
                }

                logger.LogInfo($"[{account.AccountName}] 📖 Reading timer from area: {TimerTextArea}");
                string timerText = ocrService.ExtractTextFromScreenArea(screenshot, TimerTextArea);
                logger.LogInfo($"[{account.AccountName}] Timer OCR result: '{timerText}'");

                // Parse timer in HH:MM:SS format
                if (TimeSpan.TryParse(timerText.Trim(), out TimeSpan timer))
                {
                    logger.LogInfo($"[{account.AccountName}] ⏰ Auto-join timer: {timer}");

                    if (timer.TotalHours < account.AutoRallySettings.AutoJoinMinHours)
                    {
                        logger.LogInfo($"[{account.AccountName}] 🔄 Timer ({timer}) less than {account.AutoRallySettings.AutoJoinMinHours} hours, stopping and restarting auto-join...");

                        // Click on "Auto-joining..." text to open options
                        await ClickCenterOfAreaAsync(account.InstanceNumber, logger, AutoJoinTextArea);
                        await Task.Delay(500, cancellationToken);

                        // Look for "Stop" text and click it
                        screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                        if (screenshot != null)
                        {
                            logger.LogInfo($"[{account.AccountName}] 📖 Reading Stop text from area: {StopTextArea}");
                            string stopText = ocrService.ExtractTextFromScreenArea(screenshot, StopTextArea);
                            logger.LogInfo($"[{account.AccountName}] Stop OCR result: '{stopText}'");

                            if (stopText.Contains("Stop", StringComparison.OrdinalIgnoreCase))
                            {
                                await ClickCenterOfAreaAsync(account.InstanceNumber, logger, StopTextArea);
                                await Task.Delay(1000, cancellationToken);

                                // Now enable auto-join again
                                return await EnableAutoJoinAsync(account, logger, cancellationToken, ocrService);
                            }
                            else
                            {
                                logger.LogWarning($"[{account.AccountName}] ⚠️ Stop button not found");
                                await ClickAsync(account.InstanceNumber, logger, CloseAutoJoinPoint);
                                await Task.Delay(500, cancellationToken);
                                return new TaskExecutionDetails(true, message: "Could not restart auto-join, continuing with manual rally");
                            }
                        }
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Auto-join timer sufficient (>{account.AutoRallySettings.AutoJoinMinHours} hours), continuing with auto-join enabled");
                        await ClickAsync(account.InstanceNumber, logger, CloseAutoJoinPoint);
                        await Task.Delay(500, cancellationToken);
                        return new TaskExecutionDetails(true, message: "Auto-join already active with sufficient time");
                    }
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Could not parse timer: '{timerText}'");
                    await ClickAsync(account.InstanceNumber, logger, CloseAutoJoinPoint);
                    await Task.Delay(500, cancellationToken);
                    return new TaskExecutionDetails(true, message: "Could not read auto-join timer, continuing with manual rally");
                }

                return new TaskExecutionDetails(true, message: "Auto-join setup completed");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in HandleExistingAutoJoinAsync: {ex.Message}");
                return TaskExecutionDetails.Failed($"Auto Join timer check failed: {ex.Message}");
            }
        }

        private async Task<TaskExecutionDetails> EnableAutoJoinAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, OCRService ocrService)
        {
            try
            {
                // Click on Auto-Join text
                await ClickCenterOfAreaAsync(account.InstanceNumber, logger, AutoJoinTextArea);
                await Task.Delay(1000, cancellationToken);

                // Look for "Enable" text with up to 2 seconds timeout
                var startTime = DateTime.UtcNow;
                bool enableFound = false;

                while ((DateTime.UtcNow - startTime).TotalSeconds < 2 && !cancellationToken.IsCancellationRequested)
                {
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot != null)
                    {
                        logger.LogInfo($"[{account.AccountName}] 📖 Reading Enable text from area: {EnableTextArea}");
                        string enableText = ocrService.ExtractTextFromScreenArea(screenshot, EnableTextArea);
                        logger.LogInfo($"[{account.AccountName}] Enable OCR result: '{enableText}'");

                        if (enableText.Contains("Enable", StringComparison.OrdinalIgnoreCase))
                        {
                            await ClickCenterOfAreaAsync(account.InstanceNumber, logger, EnableTextArea);
                            enableFound = true;
                            break;
                        }
                    }
                    await Task.Delay(500, cancellationToken);
                }

                if (!enableFound)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Enable button not found within 2 seconds");
                    await ClickAsync(account.InstanceNumber, logger, CloseAutoJoinPoint);
                    await Task.Delay(500, cancellationToken);
                    return new TaskExecutionDetails(true, message: "Enable button not found, continuing with manual rally");
                }

                await Task.Delay(500, cancellationToken);

                // Click close button (642,259)
                await ClickAsync(account.InstanceNumber, logger, CloseAutoJoinPoint);
                await Task.Delay(500, cancellationToken);

                logger.LogInfo($"[{account.AccountName}] ✅ Auto-join enabled successfully");
                return new TaskExecutionDetails(true, message: "Auto-join enabled successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in EnableAutoJoinAsync: {ex.Message}");
                return TaskExecutionDetails.Failed($"Enable Auto Join failed: {ex.Message}");
            }
        }

        private async Task<bool> ClickCenterOfAreaAsync(int instanceNumber, LogService logger, Rectangle area)
        {
            var centerPoint = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            logger.LogInfo($"Clicking center of area {area} at calculated point {centerPoint}");
            return await ClickAsync(instanceNumber, logger, centerPoint);
        }

        private async Task<int> GetAvailableMarchesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var marchCapacityRect = new Rectangle(197, 205, 48, 20);
            var marchOcrConfig = new OCRConfiguration
            {
                ScaleFactor = 4,
                AdaptiveC = 5,
                MedianBlurKernelSize = 1,
                CharacterWhitelist = "0123456789/"
            };

            using var marchOcr = new OCRService(logger, marchOcrConfig);
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return 1;

            logger.LogInfo($"[{account.AccountName}] 📖 Reading march capacity from area: {marchCapacityRect}");
            string text = marchOcr.ExtractTextFromScreenArea(screenshot, marchCapacityRect);
            logger.LogInfo($"[{account.AccountName}] March capacity OCR text: '{text}'");

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogInfo($"[{account.AccountName}] March OCR returned no text. Assuming at least 1 march is available.");
                return 1;
            }

            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s*/\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int current) && 
                int.TryParse(match.Groups[2].Value, out int max))
            {
                int available = max - current;
                logger.LogInfo($"[{account.AccountName}] Parsed Marches: {current}/{max}. Available: {available}");
                return available > 0 ? available : 0;
            }

            logger.LogInfo($"[{account.AccountName}] Could not parse march capacity from '{text}'. Assuming at least 1 march is available.");
            return 1;
        }

        /// <summary>
        /// Helper method to find and click an image with timeout and interval checking
        /// </summary>
        private async Task<bool> FindAndClickWithTimeoutAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            string templatePath, 
            string templateName, 
            int timeoutMs, 
            int intervalMs)
        {
            var startTime = DateTime.UtcNow;
            int attemptCount = 0;
            double bestConfidence = 0.0;

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs && !cancellationToken.IsCancellationRequested)
            {
                // Check for pause before each search attempt
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return false;
                
                attemptCount++;
                logger.LogInfo($"[{account.AccountName}] 👀 Looking for {templateName} (attempt {attemptCount})...");
                
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Screenshot failed for {templateName} attempt {attemptCount}");
                    await Task.Delay(intervalMs, cancellationToken);
                    continue;
                }

                if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                
                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    account.InstanceNumber,
                    threshold: TEMPLATE_MATCH_THRESHOLD,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: true
                );

                // Track best confidence found across all attempts
                if (confidence > bestConfidence)
                    bestConfidence = confidence;

                if (found)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Found {templateName} at {matchRect} with confidence {confidence:F3}");
                    logger.LogInfo($"[{account.AccountName}] 🎯 Clicking {templateName} at rectangle {matchRect}");
                    if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully clicked {templateName} after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                        return true;
                    }
                    else
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Found {templateName} at {matchRect} but click failed");
                    }
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] 🔍 {templateName} not found (attempt {attemptCount}) - confidence: {confidence:F3}");
                    logger.LogInfo($"[{account.AccountName}] 📁 Template file path: {templatePath}");
                    logger.LogInfo($"[{account.AccountName}] 🎯 Search threshold: {TEMPLATE_MATCH_THRESHOLD}");
                }

                await Task.Delay(intervalMs, cancellationToken);
            }

            logger.LogInfo($"[{account.AccountName}] ⏱️ Timeout reached ({timeoutMs}ms) while looking for {templateName} after {attemptCount} attempts (best confidence: {bestConfidence:F3})");
            
            
            return false;
        }

    }
}