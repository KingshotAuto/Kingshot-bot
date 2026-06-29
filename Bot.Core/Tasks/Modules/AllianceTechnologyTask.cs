using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Bot.Core.ImageDetection;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Handles automated alliance technology contributions by navigating through alliance menus
    /// and contributing to the green thumb technology.
    /// </summary>
    public class AllianceTechnologyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AllianceTechnology;
        public override string Name => "Alliance Technology";

        private readonly string _templateFolder;
        private const int SEARCH_TIMEOUT_MS = 2000; // 2 seconds for image searches
        private const int SEARCH_INTERVAL_MS = 500; // Check every 0.5 seconds
        private const int DEFAULT_CONTRIBUTE_CLICKS = 25; // Default number of contribution clicks if OCR fails
        private const int CLICK_DELAY_MS = 100; // Delay between contribution clicks
        private const double TEMPLATE_MATCH_THRESHOLD = 0.6; // Threshold for template matching
        
        // UI element coordinates
        private static readonly Rectangle ContributeButtonArea = new Rectangle(376, 987, 266, 82); // 376,987 to 642,1069
        private static readonly Rectangle ContributeCountRect = new Rectangle(525, 939, 40, 28); // 525,939 to 565,967
        
        // Default timer settings (used as fallback)
        private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(10);
        private static readonly Random _random = new Random();
        
        // Static dictionary to track last execution times per account
        private static readonly ConcurrentDictionary<string, DateTime> _lastExecutionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "alliance_tech_times.json"
        );

        public AllianceTechnologyTask()
        {
            // Get the application's base directory and combine with template path
            string baseDir = AppContext.BaseDirectory;
            _templateFolder = Path.Combine(baseDir, "templates", "images", "alliancetech");

            // Ensure template directory exists
            if (!Directory.Exists(_templateFolder))
            {
                Directory.CreateDirectory(_templateFolder);
            }
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            bool isReRun = false, 
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Get unique account ID for accurate timer tracking - use cached ID if available
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                if (string.IsNullOrEmpty(accountId))
                {
                    logger.LogWarning($"[{account.AccountName}] No cached account ID found, using fallback key");
                    accountId = $"Account_{account.AccountName}_{account.InstanceNumber}";
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Using cached account ID: '{accountId}'");
                }
                string accountKey = accountId; // Use account ID directly as the key
                
                // Check if enough time has passed since last execution for this specific account
                if (!ShouldExecute(accountKey, account))
                {
                    var nextExecution = GetNextExecutionTime(accountKey, account);
                    logger.LogInfo($"[{account.AccountName}] Too soon to run Alliance Technology (Account ID: {accountId}). Next execution at: {nextExecution}. Wait time: {account.AllianceTechnologySettings.WaitHours} hours");
                    return new TaskExecutionDetails(true, nextExecution, "Skipped - Too soon to execute");
                }

                userNotifications?.ShowStatus("Starting Alliance Technology task...", NotificationType.Info);
                logger.LogInfo($"[{account.AccountName}] 🏛️ Starting Alliance Technology task");

                // Check for pause before starting
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");

                // Step 1: Find and click alliance menu
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for alliance menu button...");
                var allianceMenuPath = Path.Combine(_templateFolder, "alliance-menu.png");
                var allianceMenuFound = await FindAndClickWithTimeoutAsync(
                    account, logger, cancellationToken, allianceMenuPath, 
                    "alliance menu", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS);

                if (!allianceMenuFound)
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Could not find alliance menu button");
                    return FailDetection("Step 1: Opening alliance menu", "alliance menu button", recoveryNeeded: true);
                }

                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(500, cancellationToken); // Wait for menu to open

                // Step 2: Find and click alliance tech button
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for alliance tech button...");
                var allianceTechPath = Path.Combine(_templateFolder, "alliance-tech.png");
                var allianceTechFound = await FindAndClickWithTimeoutAsync(
                    account, logger, cancellationToken, allianceTechPath, 
                    "alliance tech", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS);

                if (!allianceTechFound)
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Could not find alliance tech button");
                    return FailDetection("Step 2: Selecting technology tab", "alliance tech button", recoveryNeeded: true);
                }

                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(1000, cancellationToken); // Wait for tech screen to load

                // Step 3: Look for green thumb
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for green thumb technology...");
                var greenThumbPath = Path.Combine(_templateFolder, "green-thumb.png");
                var greenThumbFound = await FindAndClickWithTimeoutAsync(
                    account, logger, cancellationToken, greenThumbPath, 
                    "green thumb", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS);

                if (!greenThumbFound)
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ Green thumb not found, attempting to go back...");
                    userNotifications?.ShowStatus("Green thumb not found, going back...", NotificationType.Warning);
                    
                    // Click back button twice
                    var backButtonPath = Path.Combine(AppContext.BaseDirectory, "templates", "images", "buttons", "deploy-back.png");
                    for (int i = 0; i < 2; i++)
                    {
                        await WaitIfPausedAsync(cancellationToken);
                        if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                        
                        logger.LogInfo($"[{account.AccountName}] 🔍 Looking for back button (attempt {i + 1}/2)...");
                        var backFound = await FindAndClickWithTimeoutAsync(
                            account, logger, cancellationToken, backButtonPath, 
                            "back button", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS);
                        
                        if (!backFound)
                        {
                            logger.LogWarning($"[{account.AccountName}] ⚠️ Back button not found on attempt {i + 1}");
                        }
                        else
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Clicked back button");
                        }
                        
                        if (i < 1) // Don't wait after the last click
                        {
                            await Task.Delay(500, cancellationToken);
                        }
                    }
                    
                    return new TaskExecutionDetails(true, message: "Green thumb not available, task completed");
                }

                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(1000, cancellationToken); // Wait for green thumb screen to load

                // Step 4: Find and click contribute button based on OCR count
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for contribute button in area {ContributeButtonArea}...");
                var contributePath = Path.Combine(_templateFolder, "contribute.png");
                
                // Find the contribute button location first
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to take screenshot for contribute button");
                    return FailConnection("Step 4: Finding contribute button", "Failed to capture screen");
                }

                if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                
                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    contributePath,
                    account.InstanceNumber,
                    threshold: TEMPLATE_MATCH_THRESHOLD,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: true,
                    searchArea: ContributeButtonArea
                );

                if (!found)
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ Could not find contribute button in area {ContributeButtonArea}");
                    return FailDetection("Step 4: Contributing", "contribute button", recoveryNeeded: true);
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Found contribute button at {matchRect} with confidence {confidence:F3}");
                
                // Use OCR to determine how many contributions are needed
                int contributionCount = DEFAULT_CONTRIBUTE_CLICKS;
                logger.LogInfo($"[{account.AccountName}] 🔍 Reading contribution count using OCR on area {ContributeCountRect}...");
                
                try
                {
                    // Create OCR configuration for reading numbers
                    var ocrConfig = new OCRConfiguration
                    {
                        ScaleFactor = 4,
                        AdaptiveC = 5,
                        MedianBlurKernelSize = 1,
                        CharacterWhitelist = "0123456789" // Only expect numbers
                    };
                    using var ocr = new OCRService(logger, ocrConfig);
                    
                    string ocrText = ocr.ExtractTextFromScreenArea(screenshot, ContributeCountRect);
                    logger.LogInfo($"[{account.AccountName}] OCR extracted text: '{ocrText}'");
                    
                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        // Extract numbers from OCR text
                        var match = Regex.Match(ocrText, @"\d+");
                        if (match.Success && int.TryParse(match.Value, out int detectedCount) && detectedCount > 0)
                        {
                            // Cap at 25 since that's the maximum allowed in game
                            contributionCount = Math.Min(detectedCount, 25);
                            if (detectedCount > 25)
                            {
                                logger.LogWarning($"[{account.AccountName}] ⚠️ OCR detected {detectedCount} but capping at maximum of 25 contributions");
                            }
                            else
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ OCR detected {contributionCount} contributions needed");
                            }
                        }
                        else
                        {
                            logger.LogError($"[{account.AccountName}] ❌ OCR failed to parse valid contribution count from '{ocrText}', using default {DEFAULT_CONTRIBUTE_CLICKS}");
                        }
                    }
                    else
                    {
                        logger.LogError($"[{account.AccountName}] ❌ OCR returned empty text, using default {DEFAULT_CONTRIBUTE_CLICKS} contributions");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] ❌ OCR error: {ex.Message}, using default {DEFAULT_CONTRIBUTE_CLICKS} contributions");
                }
                
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking contribute button {contributionCount} times...");
                userNotifications?.ShowStatus($"Contributing {contributionCount} times to green thumb technology...", NotificationType.Info);

                // Click the contribute button the determined number of times
                for (int i = 0; i < contributionCount; i++)
                {
                    // Check for pause every 5 clicks
                    if (i % 5 == 0)
                    {
                        await WaitIfPausedAsync(cancellationToken);
                        if (cancellationToken.IsCancellationRequested) break;
                    }
                    
                    if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
                    {
                        logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click contribute button on attempt {i + 1}");
                    }
                    
                    if ((i + 1) % 5 == 0) // Log progress every 5 clicks
                    {
                        logger.LogInfo($"[{account.AccountName}] 📊 Contribution progress: {i + 1}/{contributionCount}");
                    }
                    
                    await Task.Delay(CLICK_DELAY_MS, cancellationToken);
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Completed {contributionCount} contributions");
                userNotifications?.ShowSuccess($"Contributed {contributionCount} times to green thumb");

                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");

                // Step 5: Find and click exit cross
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for exit cross...");
                var exitCrossPath = Path.Combine(_templateFolder, "exit-cross.png");
                var exitFound = await FindAndClickWithTimeoutAsync(
                    account, logger, cancellationToken, exitCrossPath, 
                    "exit cross", SEARCH_TIMEOUT_MS, SEARCH_INTERVAL_MS);

                if (!exitFound)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Exit cross not found, continuing with back buttons");
                }
                else
                {
                    await Task.Delay(500, cancellationToken);
                }

                // Step 6: Click back button twice to ensure we're back at main screen
                var finalBackButtonPath = Path.Combine(AppContext.BaseDirectory, "templates", "images", "buttons", "deploy-back.png");
                for (int i = 0; i < 2; i++)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                    
                    logger.LogInfo($"[{account.AccountName}] 🔍 Looking for back button to exit (attempt {i + 1}/2)...");
                    var backFound = await FindAndClickWithTimeoutAsync(
                        account, logger, cancellationToken, finalBackButtonPath, 
                        "back button", 500, SEARCH_INTERVAL_MS); // Shorter timeout for back buttons
                    
                    if (!backFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] ℹ️ Back button not found on attempt {i + 1}, likely already at main screen");
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Clicked back button");
                    }
                    
                    if (i < 1) // Don't wait after the last click
                    {
                        await Task.Delay(500, cancellationToken);
                    }
                }

                // Update execution time for this specific account
                UpdateExecutionTime(accountKey);
                
                logger.LogInfo($"[{account.AccountName}] ✅ Alliance Technology task completed successfully");
                userNotifications?.ShowSuccess("Alliance Technology task completed");
                return new TaskExecutionDetails(true, message: "Alliance Technology task completed successfully");
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Technology task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error during Alliance Technology task: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
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

                if (found)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Found {templateName} at {matchRect} with confidence {confidence:F3}");
                    if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Clicked {templateName} after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                        return true;
                    }
                }

                await Task.Delay(intervalMs, cancellationToken);
            }

            logger.LogInfo($"[{account.AccountName}] ⏱️ Timeout reached ({timeoutMs}ms) while looking for {templateName}");
            return false;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        #region Timer Management


        private bool ShouldExecute(string accountKey, AccountSettings account)
        {
            LoadExecutionTimes();
            if (!_lastExecutionTimes.TryGetValue(accountKey, out DateTime lastExecution))
            {
                return true; // First time execution
            }

            var nextExecution = GetNextExecutionTime(accountKey, account);
            return DateTime.UtcNow >= nextExecution;
        }

        private DateTime GetNextExecutionTime(string accountKey, AccountSettings account)
        {
            if (!_lastExecutionTimes.TryGetValue(accountKey, out DateTime lastExecution))
            {
                return DateTime.UtcNow; // First time execution
            }

            // Use the configured wait hours from the account settings
            var waitHours = account.AllianceTechnologySettings?.WaitHours ?? (int)DefaultInterval.TotalHours;

            // Add a small random variance (±10%) to prevent predictable timing
            var variance = _random.NextDouble() * 0.2 - 0.1; // -10% to +10%
            var actualWaitHours = waitHours * (1 + variance);

            return lastExecution.AddHours(actualWaitHours);
        }

        private void UpdateExecutionTime(string accountKey)
        {
            _lastExecutionTimes[accountKey] = DateTime.UtcNow;
            SaveExecutionTimes();
        }

        private void LoadExecutionTimes()
        {
            try
            {
                if (File.Exists(TimeStorageFile))
                {
                    var json = File.ReadAllText(TimeStorageFile);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var times = JsonSerializer.Deserialize<ConcurrentDictionary<string, DateTime>>(json, options);
                    if (times != null)
                    {
                        // Validate and clean up old entries
                        var now = DateTime.UtcNow;
                        foreach (var kvp in times)
                        {
                            // Only keep entries from the last 48 hours
                            if (now.Subtract(kvp.Value).TotalHours <= 48)
                            {
                                _lastExecutionTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AllianceTechnologyTask] Error loading execution times: {ex.Message}");
                _lastExecutionTimes.Clear();
            }
        }

        private void SaveExecutionTimes()
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
                var validTimes = _lastExecutionTimes
                    .Where(kvp => now.Subtract(kvp.Value).TotalHours <= 48)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(validTimes, options);
                File.WriteAllText(TimeStorageFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AllianceTechnologyTask] Error saving execution times: {ex.Message}");
            }
        }

        #endregion
    }
}