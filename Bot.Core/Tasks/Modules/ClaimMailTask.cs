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
using Bot.Core.ImageDetection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    public class ClaimMailTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ClaimMail;
        public override string Name => "Claim Mail";

        // Mail menu tab areas
        private static readonly Rectangle SystemMailTab = new Rectangle(173, 101, 108, 34);
        private static readonly Rectangle AllianceMailTab = new Rectangle(312, 104, 98, 35);
        private static readonly Rectangle BattleMailTab = new Rectangle(444, 101, 96, 30);
        private static readonly Rectangle CloseMailButton = new Rectangle(671, 33, 18, 19);

        // Search areas for mail and claim buttons
        private static readonly Rectangle MailIconSearchArea = new Rectangle(618, 1004, 78, 77);  // 618-696, 1004-1081
        private static readonly Rectangle ClaimButtonSearchArea = new Rectangle(387, 1196, 88, 78);  // 387-475, 1196-1274

        // Collection interval settings (same as ConquestCollectTask)
        private static readonly TimeSpan MinCollectionInterval = TimeSpan.FromHours(3);
        private static readonly TimeSpan MaxCollectionInterval = TimeSpan.FromHours(6);
        private static readonly Random _random = new Random();

        // Static dictionary to track last collection times per account (instance + account name)
        private static readonly ConcurrentDictionary<string, DateTime> _lastCollectionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "mail_collection_times.json"
        );

        // Last known claim button location for optimization
        private Rectangle? _lastKnownClaimLocation = null;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Get unique account ID for accurate timer tracking
                // First try to get cached account ID, fallback if needed
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
                
                // Check if enough time has passed since last collection for this specific account
                if (!ShouldCollect(accountKey))
                {
                    var nextCollection = GetNextCollectionTime(accountKey);
                    logger.LogInfo($"[{account.AccountName}] Too soon to collect mail (Account ID: {accountId}). Next collection at: {nextCollection}");
                    return new TaskExecutionDetails(true, nextCollection, "Skipped - Too soon to collect");
                }

                logger.LogInfo($"[{account.AccountName}] 📬 Starting mail claim task. IsReRun = {isReRun}");

                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber);

                if (cancellationToken.IsCancellationRequested)
                {
                    return TaskExecutionDetails.Failed("Mail claim task cancelled during view location.");
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Bot confirmed in BaseView.");

                // Open mail menu
                if (!await OpenMailMenu(account, logger, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Failed to open mail menu.");
                }

                // Process each mail tab
                await ProcessMailTab(account, logger, cancellationToken, SystemMailTab, "System");
                await ProcessMailTab(account, logger, cancellationToken, AllianceMailTab, "Alliance");
                await ProcessMailTab(account, logger, cancellationToken, BattleMailTab, "Battle");

                // Close mail menu
                logger.LogInfo($"[{account.AccountName}] 🚪 Closing mail menu...");
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, CloseMailButton))
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click close button, attempting fallback close.");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, new Rectangle(0, 0, 50, 50)); // Fallback to top-left corner
                }

                await Task.Delay(GameCoordinates.Delays.AfterClick, cancellationToken);
                
                // Update collection time for this specific account
                UpdateCollectionTime(accountKey);
                
                logger.LogInfo($"[{account.AccountName}] ✅ Mail claim task completed.");
                return TaskExecutionDetails.Succeeded();
            }
            catch (BotLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error executing Mail Claim task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error executing Mail Claim task: {ex.Message}");
            }
        }


        private bool ShouldCollect(string accountKey)
        {
            LoadCollectionTimes();
            if (!_lastCollectionTimes.TryGetValue(accountKey, out DateTime lastCollection))
            {
                return true; // First time collection
            }

            var nextCollection = GetNextCollectionTime(accountKey);
            return DateTime.UtcNow >= nextCollection;
        }

        private DateTime GetNextCollectionTime(string accountKey)
        {
            if (!_lastCollectionTimes.TryGetValue(accountKey, out DateTime lastCollection))
            {
                return DateTime.UtcNow; // First time collection
            }

            // Calculate random interval between min and max
            var intervalHours = _random.NextDouble() * 
                (MaxCollectionInterval.TotalHours - MinCollectionInterval.TotalHours) + 
                MinCollectionInterval.TotalHours;
            
            return lastCollection.AddHours(intervalHours);
        }

        private void UpdateCollectionTime(string accountKey)
        {
            _lastCollectionTimes[accountKey] = DateTime.UtcNow;
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
                    
                    // Try to load new format first (string-based keys)
                    try
                    {
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
                    catch
                    {
                        // Fall back to old format (int-based keys) for backwards compatibility
                        var oldTimes = JsonSerializer.Deserialize<ConcurrentDictionary<int, DateTime>>(json, options);
                        if (oldTimes != null)
                        {
                            var now = DateTime.UtcNow;
                            foreach (var kvp in oldTimes)
                            {
                                // Only keep entries from the last 24 hours
                                if (now.Subtract(kvp.Value).TotalHours <= 24)
                                {
                                    // Convert old instance-based key to account-based key format
                                    // Note: We can't know the account name from old data, so this is just for migration
                                    _lastCollectionTimes[$"{kvp.Key}_unknown"] = kvp.Value;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Can't use logger here as it's not available in this context anymore
                Console.WriteLine($"[ClaimMailTask] Error loading collection times: {ex.Message}");
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
                Console.WriteLine($"[ClaimMailTask] Error saving collection times: {ex.Message}");
            }
        }

        private async Task<bool> OpenMailMenu(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int POLL_INTERVAL_MS = 200;
            const int MAX_WAIT_TIME_MS = 5000;
            var startTime = DateTime.UtcNow;
            bool mailIconClicked = false;

            logger.LogInfo($"[{account.AccountName}] 🔍 Looking for mail icon in specified area...");

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
            {
                if (await WaitForImageAsync("mail.png", account.InstanceNumber, logger, cancellationToken,
                    timeoutMs: POLL_INTERVAL_MS, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                {
                    if (await FindAndClickImageAsync("mail.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked mail icon after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms.");
                        mailIconClicked = true;
                        break;
                    }
                }
                await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
            }

            if (!mailIconClicked)
            {
                logger.LogError($"[{account.AccountName}] Error: Failed to find mail-icon.png, calling locator module for recovery");
                var locator = new LocatorService(logger, account);
                try
                {
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying mail icon detection");
                    
                    // Retry after locator service
                    var retryStartTime = DateTime.UtcNow;
                    bool retryMailIconClicked = false;
                    
                    while ((DateTime.UtcNow - retryStartTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
                    {
                        if (await WaitForImageAsync("mail.png", account.InstanceNumber, logger, cancellationToken,
                            timeoutMs: POLL_INTERVAL_MS, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                        {
                            if (await FindAndClickImageAsync("mail.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked mail icon after locator recovery in {(DateTime.UtcNow - retryStartTime).TotalMilliseconds:F0}ms.");
                                retryMailIconClicked = true;
                                break;
                            }
                        }
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                    }
                    
                    if (!retryMailIconClicked)
                    {
                        logger.LogError($"[{account.AccountName}] Failed to find or click mail icon even after locator recovery");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                    return false;
                }
            }

            await Task.Delay(1000, cancellationToken); // Wait for mail menu to open
            return true;
        }

        private async Task<bool> ProcessMailTab(AccountSettings account, LogService logger, CancellationToken cancellationToken, Rectangle tabArea, string tabName)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 📨 Processing {tabName} mail tab...");

                // Click the mail tab
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, tabArea))
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to click {tabName} mail tab.");
                    return false;
                }

                await Task.Delay(500, cancellationToken); // Wait for tab switch

                // Try to claim using last known location first if available
                if (_lastKnownClaimLocation.HasValue)
                {
                    logger.LogInfo($"[{account.AccountName}] 🎯 Trying last known claim button location...");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, ClaimButtonSearchArea);
                    await Task.Delay(200, cancellationToken);
                    
                    // Click in the same area again
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, ClaimButtonSearchArea);
                }

                // Look for and click claim button
                const int POLL_INTERVAL_MS = 200;
                const int MAX_WAIT_TIME_MS = 5000;
                var startTime = DateTime.UtcNow;
                bool claimFound = false;

                while ((DateTime.UtcNow - startTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
                {
                    if (await WaitForImageAsync("claim.png", account.InstanceNumber, logger, cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS, threshold: 0.7, useEnhancedMatching: true, searchArea: ClaimButtonSearchArea))
                    {
                        if (await FindAndClickImageAsync("claim.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true, searchArea: ClaimButtonSearchArea))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked claim button in {tabName} tab after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms.");
                            claimFound = true;
                            
                            // Store the successful claim location
                            _lastKnownClaimLocation = ClaimButtonSearchArea;
                            
                            // Wait 500ms after clicking claim
                            await Task.Delay(500, cancellationToken);
                            
                            // Look for vip-rewards2.png and click if found
                            if (await WaitForImageAsync("../collectvip/vip-rewards2.png", account.InstanceNumber, logger, cancellationToken,
                                timeoutMs: 3000, threshold: 0.7, useEnhancedMatching: true))
                            {
                                if (await FindAndClickImageAsync("../collectvip/vip-rewards2.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true))
                                {
                                    logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked vip-rewards2 after claim.");
                                }
                            }
                            else
                            {
                                logger.LogInfo($"[{account.AccountName}] ℹ️ vip-rewards2 not found after claim, continuing...");
                                // Click at the same location again as fallback
                                await ClickRandomInRectAsync(account.InstanceNumber, logger, ClaimButtonSearchArea);
                            }
                            
                            break;
                        }
                    }
                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }

                if (!claimFound)
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ No claim button found in {tabName} tab after {MAX_WAIT_TIME_MS}ms. This may be normal if no mail to claim.");
                }

                await Task.Delay(500, cancellationToken); // Wait for claim animation
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing {tabName} mail tab: {ex.Message}");
                return false;
            }
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath,
                    "Mail Claim Image Templates:\n\n" +
                    "Required images:\n\n" +
                    "1. mail.png - The mail icon in the game UI (search area: 618,1004 to 696,1081)\n" +
                    "2. claim.png - The claim button in mail interface (search area: 392,1203 to 483,1272)\n\n" +
                    "Instructions:\n" +
                    "- Take clear screenshots of each element\n" +
                    "- Crop to just the unique part you want to detect\n" +
                    "- Save with exact names above\n" +
                    "- Images should be clear and distinctive\n\n" +
                    "Enhanced Features:\n" +
                    "- Polling-based image detection\n" +
                    "- Restricted search areas for better accuracy\n" +
                    "- Caches claim button location for optimization\n" +
                    "- Robust error handling\n" +
                    "- Detailed logging");
            }
            return Task.CompletedTask;
        }
    }
} 