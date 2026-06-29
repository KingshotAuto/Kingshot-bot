using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Task to detect and cache the unique account ID for the currently active game account.
    /// This should run early to establish account identity for timer tracking in other tasks.
    /// </summary>
    public class AccountDetectionTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => Models.TaskType.AccountDetection;
        public override string Name => "Account Detection";
        
        private static readonly Dictionary<int, string> _cachedAccountIds = new Dictionary<int, string>();
        private static readonly object _cacheLock = new object();

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🔍 Starting account ID detection and caching...");
                logger.LogInfo($"[{account.AccountName}] Detecting account ID...", context: null, category: LogCategories.UserAction);

                // Check if we already have a cached account ID for this instance
                lock (_cacheLock)
                {
                    if (_cachedAccountIds.TryGetValue(account.InstanceNumber, out var cachedId))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Using cached account ID: '{cachedId}'");
                        logger.LogInfo($"[{account.AccountName}] Account {cachedId} found (cached)", context: null, category: LogCategories.UserAction);
                        return new TaskExecutionDetails(true, message: $"Account ID already cached: {cachedId}");
                    }
                }

                // Detect account ID using AccountDetectionService
                logger.LogInfo($"[{account.AccountName}] 🔍 Detecting unique account ID from game interface...");
                var accountDetection = new AccountDetectionService(logger);
                string accountId = await accountDetection.GetCurrentAccountIdAsync(account, cancellationToken);

                // Cache the detected account ID
                lock (_cacheLock)
                {
                    _cachedAccountIds[account.InstanceNumber] = accountId;
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Account ID detected and cached: '{accountId}'");
                logger.LogInfo($"[{account.AccountName}] Account {accountId} found", context: null, category: LogCategories.UserAction);
                userNotifications?.ShowSuccess($"Account {accountId} detected for {account.AccountName}");
                
                return new TaskExecutionDetails(true, message: $"Account ID detected and cached: {accountId}");
            }
            catch (System.OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] Account detection was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (System.Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error during account detection: {ex.Message}");
                logger.LogInfo($"[{account.AccountName}] Account detection failed: {ex.Message}", context: null, category: LogCategories.UserAction);

                // Categorize the error type
                FailureCategory category = FailureCategory.Unknown;
                if (ex.Message.Contains("screenshot") || ex.Message.Contains("OCR"))
                    category = FailureCategory.Detection;
                else if (ex.Message.Contains("connection") || ex.Message.Contains("ADB"))
                    category = FailureCategory.Connection;

                return TaskExecutionDetails.FailedWith(
                    category,
                    "Account detection",
                    ex.Message,
                    customHint: "Account ID will use fallback - task timers may not track correctly across accounts");
            }
        }

        /// <summary>
        /// Get the cached account ID for a specific instance
        /// </summary>
        public static string GetCachedAccountId(int instanceNumber)
        {
            lock (_cacheLock)
            {
                return _cachedAccountIds.TryGetValue(instanceNumber, out var accountId) ? accountId : string.Empty;
            }
        }

        /// <summary>
        /// Clear cached account IDs (useful when switching accounts)
        /// </summary>
        public static void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachedAccountIds.Clear();
            }
        }

        /// <summary>
        /// Clear cached account ID for a specific instance
        /// </summary>
        public static void ClearCacheForInstance(int instanceNumber)
        {
            lock (_cacheLock)
            {
                _cachedAccountIds.Remove(instanceNumber);
            }
        }
    }
}