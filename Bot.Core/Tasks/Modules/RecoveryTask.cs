using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.LDPlayer;
using Bot.Core.Services;
using System.Threading.Tasks;
using System.Threading;
using System;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Config;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    public class RecoveryTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.Recovery;
        public override string Name => "Recovery Mode";
        protected override string GetImageFolderName() => "recovery";
        
        private new readonly UnifiedTemplateMatchingService _templateMatcher;
        private TaskType? _lastFailedTaskType;


        public RecoveryTask()
        {
            _templateMatcher = new UnifiedTemplateMatchingService(new LogService());
        }

        public void SetLastFailedTask(TaskType taskType)
        {
            _lastFailedTaskType = taskType;
        }

        private string GetUserFriendlyTaskName(TaskType taskType)
        {
            return taskType switch
            {
                TaskType.AutoHunt => "Auto Hunt",
                TaskType.AutoHeal => "Auto Heal",
                TaskType.Farming => "Farming",
                TaskType.AutoAllianceHelp => "Alliance Help",
                TaskType.ClaimMail => "Claim Mail",
                TaskType.ConquestCollect => "Conquest Collect",
                TaskType.TroopTraining => "Troop Training",
                TaskType.ChangeAccount => "Change Account",
                TaskType.AutoBuild => "Auto Build",
                TaskType.Startup => "Startup",
                TaskType.Recovery => "Recovery",
                TaskType.AutoShield => "Auto Shield",
                TaskType.CollectVip => "Collect VIP",
                TaskType.ClaimMissions => "Claim Missions",
                _ => taskType.ToString()
            };
        }

        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _recoveryLocks = new();

        private static SemaphoreSlim GetRecoveryLock(int instanceNumber)
        {
            return _recoveryLocks.GetOrAdd(instanceNumber, _ => new SemaphoreSlim(1, 1));
        }

        public override async Task<bool> CanExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default)
        {
            try
            {
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);

                var isRunning = await instanceManager.IsInstanceRunningAsync(account.InstanceNumber, cancellationToken);
                if (!isRunning)
                {
                    logger.LogError($"[{account.AccountName}] Instance {account.InstanceNumber} is not running");
                    return true; // We can still execute recovery even if instance is not running
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Recovery task can execute - instance {account.InstanceNumber} is running");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error checking if recovery task can execute: {ex.Message}");
                return true; // Allow recovery attempt even if check fails
            }
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo("Recovery task initialized");
            return Task.CompletedTask;
        }

        private async Task<bool> TryEnhancedRecoveryAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            IUserNotificationService? userNotifications = null)
        {
            logger.LogInfo($"[{account.AccountName}] Starting enhanced recovery process...");

            var baseDir = AppContext.BaseDirectory;
            var locatorFolder = Path.Combine(baseDir, "templates", "images", "locator");
            var autohuntFolder = Path.Combine(baseDir, "templates", "images", "autohunt");

            // Define image paths
            var adClosePath = Path.Combine(baseDir, "templates", "images", "startup", "ad-close.png");
            var adClose2Path = Path.Combine(baseDir, "templates", "images", "startup", "ad-close2.png");
            var backArrowPath = Path.Combine(autohuntFolder, "back-arrow.png");
            var baseViewPath = Path.Combine(locatorFolder, "base-view.png");
            var mapViewPath = Path.Combine(locatorFolder, "map-view.png");

            // Verify all required images exist
            var requiredImages = new Dictionary<string, string>
            {
                { "ad-close.png", adClosePath },
                { "ad-close2.png", adClose2Path },
                { "back-arrow.png", backArrowPath },
                { "base-view.png", baseViewPath },
                { "map-view.png", mapViewPath }
            };

            foreach (var image in requiredImages)
            {
                if (!File.Exists(image.Value))
                {
                    logger.LogError($"[{account.AccountName}] Required image not found: {image.Key}");
                    return false;
                }
            }

            const int MAX_RECOVERY_ATTEMPTS = 4;
            for (int attempt = 1; attempt <= MAX_RECOVERY_ATTEMPTS; attempt++)
            {
                logger.LogInfo($"[{account.AccountName}] Enhanced recovery attempt {attempt}/{MAX_RECOVERY_ATTEMPTS}");
                
                try
                {
                    // Take a screenshot
                    var adbController = await ADBMigrationHelper.GetConnectionAsync(account.InstanceNumber, logger, cancellationToken);
                    if (adbController == null) return false;

                    var screenshot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                    if (screenshot == null || screenshot.Length == 0)
                    {
                        logger.LogError($"[{account.AccountName}] Failed to capture screenshot");
                        continue;
                    }

                    // Check for all navigation elements simultaneously
                    var (foundAdClose, adCloseRect, adCloseConf) = _templateMatcher.MatchTemplate(screenshot, adClosePath, account.InstanceNumber, 0.6f);
                    var (foundAdClose2, adClose2Rect, adClose2Conf) = _templateMatcher.MatchTemplate(screenshot, adClose2Path, account.InstanceNumber, 0.6f);
                    var (foundBackArrow, backArrowRect, backArrowConf) = _templateMatcher.MatchTemplate(screenshot, backArrowPath, account.InstanceNumber, 0.6f);

                    // If any navigation element is found, click it
                    bool clickedSomething = false;
                    if (foundAdClose)
                    {
                        logger.LogInfo($"[{account.AccountName}] Found ad-close.png (confidence: {adCloseConf:F3}), clicking...");
                        await ADBMigrationHelper.TapAsync(adbController, adCloseRect.GetCenter().X, adCloseRect.GetCenter().Y, logger, cancellationToken);
                        clickedSomething = true;
                    }
                    if (foundAdClose2)
                    {
                        logger.LogInfo($"[{account.AccountName}] Found ad-close2.png (confidence: {adClose2Conf:F3}), clicking...");
                        await ADBMigrationHelper.TapAsync(adbController, adClose2Rect.GetCenter().X, adClose2Rect.GetCenter().Y, logger, cancellationToken);
                        clickedSomething = true;
                    }
                    if (foundBackArrow)
                    {
                        logger.LogInfo($"[{account.AccountName}] Found back-arrow.png (confidence: {backArrowConf:F3}), clicking...");
                        await ADBMigrationHelper.TapAsync(adbController, backArrowRect.GetCenter().X, backArrowRect.GetCenter().Y, logger, cancellationToken);
                        clickedSomething = true;
                    }

                    if (clickedSomething)
                    {
                        await Task.Delay(500, cancellationToken); // Wait 0.5s after clicking
                    }

                    // Check for map views
                    screenshot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                    if (screenshot == null || screenshot.Length == 0) continue;

                    var (foundBase, _, baseConf) = _templateMatcher.MatchTemplate(screenshot, baseViewPath, account.InstanceNumber, 0.6f);
                    var (foundMap, _, mapConf) = _templateMatcher.MatchTemplate(screenshot, mapViewPath, account.InstanceNumber, 0.6f);

                    if (foundBase || foundMap)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Successfully recovered! Found {(foundBase ? "base" : "map")} view");
                        userNotifications?.ShowSuccess($"Navigation recovered! Found {(foundBase ? "base" : "map")} view");
                        
                        // If we have a last failed task, restart it
                        if (_lastFailedTaskType.HasValue)
                        {
                            var taskName = GetUserFriendlyTaskName(_lastFailedTaskType.Value);
                            logger.LogInfo($"[{account.AccountName}] Restarting failed {taskName} task...");
                            userNotifications?.ShowStatus($"Restarting {taskName} task...", NotificationType.Info);
                            var taskInstance = TaskFactory.CreateTask(_lastFailedTaskType.Value);
                            if (taskInstance != null)
                            {
                                await taskInstance.InitializeAsync(logger, cancellationToken);
                                await taskInstance.ExecuteAsync(account, logger, cancellationToken, false, userNotifications);
                            }
                        }
                        
                        return true;
                    }

                    logger.LogInfo($"[{account.AccountName}] Maps not found yet, continuing recovery loop...");
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] Error during enhanced recovery attempt {attempt}: {ex.Message}");
                }
            }

            logger.LogInfo($"[{account.AccountName}] Enhanced recovery failed after {MAX_RECOVERY_ATTEMPTS} attempts, falling back to instance restart");
            return false;
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
                // Log which task caused the recovery with detailed information
                string triggerInfo = _lastFailedTaskType.HasValue 
                    ? $"Recovery triggered by {GetUserFriendlyTaskName(_lastFailedTaskType.Value)} task"
                    : "Recovery triggered by unknown task";
                
                logger.LogInfo($"[{account.AccountName}] 🚀 {triggerInfo} - starting recovery process for instance {account.InstanceNumber}...");
                
                // Show GUI notification including the task that caused recovery
                string warningMessage = _lastFailedTaskType.HasValue 
                    ? $"Bot lost during {GetUserFriendlyTaskName(_lastFailedTaskType.Value)} task, attempting recovery..."
                    : "Bot appears to be lost, attempting recovery...";
                
                userNotifications?.ShowWarning(warningMessage, "The bot will try to navigate back to a known state");
                
                // Acquire recovery lock to prevent other instances from starting
                if (!await GetRecoveryLock(account.InstanceNumber).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
                {
                    logger.LogError($"[{account.AccountName}] Could not acquire recovery lock - another recovery may be in progress");
                    userNotifications?.ShowError("Recovery failed: Another recovery already in progress", true);
                    return TaskExecutionDetails.Failed("Could not acquire recovery lock");
                }

                try
                {
                    // Try enhanced recovery first
                    userNotifications?.ShowStatus("Attempting to recover bot navigation...", NotificationType.Info);
                    if (await TryEnhancedRecoveryAsync(account, logger, cancellationToken, userNotifications))
                    {
                        userNotifications?.ShowSuccess("Bot successfully recovered and back on track!");
                        return TaskExecutionDetails.Succeeded();
                    }

                    // If enhanced recovery fails, proceed with full instance restart
                    userNotifications?.ShowWarning("Navigation recovery failed, restarting emulator...", "This may take a few minutes");
                    
                    var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                    var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                    var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);

                    // Step 1: Stop the instance
                    userNotifications?.ShowStatus("Stopping emulator instance...", NotificationType.Info);
                    logger.LogInfo($"[{account.AccountName}] Stopping instance {account.InstanceNumber}...");
                    await instanceManager.StopInstanceAsync(account.InstanceNumber, cancellationToken);
                    await Task.Delay(5000, cancellationToken); // Wait for instance to fully stop

                    // Step 2: Start the instance
                    userNotifications?.ShowStatus("Starting emulator instance...", NotificationType.Info);
                    logger.LogInfo($"[{account.AccountName}] Starting instance {account.InstanceNumber}...");
                    var success = await instanceManager.StartInstanceAsync(account.InstanceNumber, cancellationToken);
                    if (!success)
                    {
                        logger.LogError($"[{account.AccountName}] Failed to start instance {account.InstanceNumber}");
                        userNotifications?.ShowError("Failed to restart emulator instance", false, "Check if LDPlayer is properly installed");
                        return TaskExecutionDetails.Failed("Failed to start instance");
                    }

                    // Step 3: Wait for instance to be fully booted (with timeout protection for parallel instances)
                    userNotifications?.ShowStatus("Waiting for emulator to fully boot...", NotificationType.Info);
                    logger.LogInfo($"[{account.AccountName}] Waiting for instance to be fully booted...");
                    await Task.Delay(20000, cancellationToken); // Wait for instance to fully boot
                    
                    // Note: We maintain semaphore ownership during recovery to ensure this instance
                    // slot is not accidentally given to another account during the restart process

                    // Step 4: Run startup task
                    userNotifications?.ShowStatus("Initializing game after restart...", NotificationType.Info);
                    logger.LogInfo($"[{account.AccountName}] Running startup task...");
                    var startupTask = new StartupTask();
                    await startupTask.InitializeAsync(logger, cancellationToken);
                    var startupResult = await startupTask.ExecuteAsync(account, logger, cancellationToken, false, userNotifications);
                    if (!startupResult.Success)
                    {
                        logger.LogError($"[{account.AccountName}] Startup task failed after recovery");
                        userNotifications?.ShowError("Game initialization failed after restart", false, "The bot may need manual intervention");
                        return TaskExecutionDetails.Failed("Startup task failed after recovery");
                    }

                    logger.LogInfo($"[{account.AccountName}] ✅ Instance {account.InstanceNumber} confirmed fully booted and responsive after recovery");
                    userNotifications?.ShowSuccess("Emulator successfully restarted and ready!");
                    return TaskExecutionDetails.Succeeded();
                }
                finally
                {
                    GetRecoveryLock(account.InstanceNumber).Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] 💥 Error during recovery: {ex.Message}");
                logger.LogError($"[{account.AccountName}] Stack trace: {ex.StackTrace}");
                userNotifications?.ShowError("Recovery process failed with an error", false, "Check logs for details or try manual restart");
                return TaskExecutionDetails.Failed($"Error during recovery: {ex.Message}");
            }
        }

    }

    // Helper class to create task instances
    public static class TaskFactory
    {
        private static readonly IConfigurationManager _configManager = ConfigurationManager.Instance;

        public static BaseTaskWithCommonPatterns? CreateTask(TaskType taskType)
        {
            var logger = new LogService(); // Create a new logger for each task
            return taskType switch
            {
                TaskType.AutoHunt => new AutoHuntTask(),
                TaskType.AutoHeal => new AutoHealTask(logger),
                TaskType.Farming => new FarmingTask(),
                TaskType.AutoAllianceHelp => new AllianceHelpTask(),
                TaskType.ClaimMail => new ClaimMailTask(),
                TaskType.ConquestCollect => new ConquestCollectTask(),
                TaskType.TroopTraining => new TroopTrainingTask(),
                TaskType.ChangeAccount => new ChangeAccountTask(),
                TaskType.AutoBuild => new AutoBuildTask(),
                TaskType.Startup => new StartupTask(),
                TaskType.Recovery => new RecoveryTask(),
                TaskType.AutoShield => new AutoShieldTask(_configManager),
                _ => null
            };
        }
    }
} 