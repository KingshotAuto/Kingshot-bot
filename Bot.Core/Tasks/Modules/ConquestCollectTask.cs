using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
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
    public class ConquestCollectTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ConquestCollect;
        public override string Name => "Conquest Collection";

        // Static dictionary to track last collection times per account
        private static readonly ConcurrentDictionary<string, DateTime> _lastCollectionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "conquest_collection_times.json"
        );

        // UI Element coordinates for navigation sequence
        private static readonly Rectangle FirstClickArea = new Rectangle(40, 1199, 65, 48);    // x=40, y=1199 & x=105, y=1247
        private static readonly Rectangle SecondClickArea = new Rectangle(575, 908, 76, 23);   // x=575, y=908 & x=651, y=931
        private static readonly Rectangle CollectArea = new Rectangle(300, 902, 135, 47);      // x=300, y=902 & x=435, y=949
        private static readonly Rectangle BackButtonArea = new Rectangle(35, 28, 26, 22);      // x=35, y=28 & x=61, y=50

        // Default collection interval settings (used as fallback)
        private static readonly TimeSpan DefaultCollectionInterval = TimeSpan.FromHours(4);
        private static readonly Random _random = new Random();

        public ConquestCollectTask()
        {
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
                // Get unique account ID for accurate timer tracking
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

                // Check if enough time has passed since last collection
                if (!ShouldCollect(accountId, account))
                {
                    var nextCollection = GetNextCollectionTime(accountId, account);
                    logger.LogInfo($"[{account.AccountName}] Too soon to collect conquest rewards. Next collection at: {nextCollection}. Wait time: {account.ConquestCollectSettings.WaitHours} hours");
                    return new TaskExecutionDetails(true, nextCollection, "Skipped - Too soon to collect");
                }

                // Step 1: Use LocatorService to detect current view
                logger.LogInfo($"[{account.AccountName}] 🧭 Detecting current view...");
                var locator = new LocatorService(logger, account);
                var currentView = await locator.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);

                if (currentView == ViewType.Unknown)
                {
                    return TaskExecutionDetails.Failed("Could not detect current view");
                }

                logger.LogInfo($"[{account.AccountName}] 📍 Current view detected: {currentView}");

                // Step 2: Click first area (bottom menu)
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking first navigation area...");
                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, FirstClickArea, cancellationToken, maxRetries: 2))
                {
                    return TaskExecutionDetails.Failed("Failed to click first navigation area");
                }

                // Wait 1.5 seconds
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(1500, cancellationToken);

                // Step 3: Click second area
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking second navigation area...");
                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, SecondClickArea, cancellationToken, maxRetries: 2))
                {
                    return TaskExecutionDetails.Failed("Failed to click second navigation area");
                }

                // Wait 1 second
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(1000, cancellationToken);

                // Step 4: Click collect area
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking collect area...");
                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, CollectArea, cancellationToken, maxRetries: 2))
                {
                    return TaskExecutionDetails.Failed("Failed to click collect area");
                }

                // Wait 0.5 seconds
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(500, cancellationToken);

                // Step 5: Click collect area again
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking collect area again...");
                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, CollectArea, cancellationToken, maxRetries: 2))
                {
                    return TaskExecutionDetails.Failed("Failed to click collect area second time");
                }

                // Wait random time between 1-1.5 seconds
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(_random.Next(1000, 1500), cancellationToken);

                // Step 6: Click back button
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking back button...");
                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, BackButtonArea, cancellationToken, maxRetries: 2))
                {
                    return TaskExecutionDetails.Failed("Failed to click back button");
                }

                // Update collection time
                UpdateCollectionTime(accountId);

                logger.LogInfo($"[{account.AccountName}] ✅ Conquest collection completed successfully");
                return TaskExecutionDetails.Succeeded();
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during conquest collection: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during conquest collection: {ex.Message}");
            }
        }

        private bool ShouldCollect(string accountId, AccountSettings account)
        {
            LoadCollectionTimes();
            if (!_lastCollectionTimes.TryGetValue(accountId, out DateTime lastCollection))
            {
                return true; // First time collection
            }

            var nextCollection = GetNextCollectionTime(accountId, account);
            return DateTime.UtcNow >= nextCollection;
        }

        private DateTime GetNextCollectionTime(string accountId, AccountSettings account)
        {
            if (!_lastCollectionTimes.TryGetValue(accountId, out DateTime lastCollection))
            {
                return DateTime.UtcNow; // First time collection
            }

            // Use the configured wait hours from the account settings
            var waitHours = account.ConquestCollectSettings?.WaitHours ?? (int)DefaultCollectionInterval.TotalHours;

            // Add a small random variance (±10%) to prevent predictable timing
            var variance = _random.NextDouble() * 0.2 - 0.1; // -10% to +10%
            var actualWaitHours = waitHours * (1 + variance);

            return lastCollection.AddHours(actualWaitHours);
        }

        private void UpdateCollectionTime(string accountId)
        {
            _lastCollectionTimes[accountId] = DateTime.UtcNow;
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
                    var times = JsonSerializer.Deserialize<ConcurrentDictionary<string, DateTime>>(json, options);
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
                // Can't use logger here as it's not available in this context anymore
                Console.WriteLine($"[ConquestCollectTask] Error loading collection times: {ex.Message}");
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
                // Can't use logger here as it's not available in this context anymore
                Console.WriteLine($"[ConquestCollectTask] Error saving collection times: {ex.Message}");
            }
        }

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            // Load existing collection times during initialization
            LoadCollectionTimes();

            // Create README if it doesn't exist
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath,
                    "Conquest Collection Task - Image Templates\n\n" +
                    "Required images:\n\n" +
                    "1. conquest-icon.png - The conquest menu icon\n" +
                    "2. collect-button.png - The collect rewards button\n" +
                    "3. close-button.png - The close interface button\n\n" +
                    "Image Requirements:\n" +
                    "- Must be clear, high-contrast screenshots\n" +
                    "- Should include some surrounding context\n" +
                    "- Minimum size: 50x50 pixels\n" +
                    "- Maximum size: 200x200 pixels\n" +
                    "- Format: PNG with transparency where applicable\n\n" +
                    "Collection Timing:\n" +
                    "- Minimum interval: 3 hours\n" +
                    "- Maximum interval: 6 hours\n" +
                    "- Random interval chosen between min and max\n" +
                    "- Times stored per instance in conquest_collection_times.json",
                    cancellationToken);
            }
        }
    }
} 