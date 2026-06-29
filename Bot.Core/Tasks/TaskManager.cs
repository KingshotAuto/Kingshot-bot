using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using Bot.Core.Exceptions;
using Bot.Core.Services;
using Bot.Core.LDPlayer;
using System.IO;
using Bot.Core.Utils;
using System.Text.Json;
using Bot.Core.ImageDetection;
using Bot.Core.Config;

namespace Bot.Core.Tasks
{
    public class TaskManager
    {
        private readonly LogService _logger;
        private readonly IUserNotificationService? _userNotifications;
        private readonly ConcurrentDictionary<TaskType, ITask> _availableTasks = new();
        private readonly ConcurrentDictionary<int, bool> _instanceStartupComplete = new();
        private readonly StartupTask _startupTask;
        private readonly List<AccountSettings> _accounts = new();
        private readonly InstanceManager _instanceManager;
        private readonly IServiceProvider _serviceProvider;

        public TaskManager(LogService logger, IServiceProvider serviceProvider, IUserNotificationService? userNotifications = null)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _userNotifications = userNotifications;
            
            // Purge all logs at the beginning of a new session
            // LogService.PurgeAllLogs(); // Disabled to prevent file access errors
            
            // Initialize startup task
            _startupTask = new StartupTask();
            _availableTasks[TaskType.Startup] = _startupTask;
            
            // Initialize LDPlayer instance manager with path and logger
            var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
            var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
            _instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);

            // Initialize available task modules with new unified base class
            _availableTasks[TaskType.AccountDetection] = new AccountDetectionTask();
            _availableTasks[TaskType.AutoHunt] = new AutoHuntTask();
            _availableTasks[TaskType.Farming] = new FarmingTask();
            _availableTasks[TaskType.TroopTraining] = new TroopTrainingTask();
            _availableTasks[TaskType.ConquestCollect] = new ConquestCollectTask();
            _availableTasks[TaskType.AutoAllianceHelp] = new AllianceHelpTask();
            _availableTasks[TaskType.Recovery] = new RecoveryTask();
            _availableTasks[TaskType.ClaimMail] = new ClaimMailTask();
            _availableTasks[TaskType.ChangeAccount] = new ChangeAccountTask();
            _availableTasks[TaskType.AutoHeal] = new AutoHealTask(logger);
            _availableTasks[TaskType.AutoBuild] = new AutoBuildTask();
            _availableTasks[TaskType.AutoClaimHero] = new AutoClaimHeroTask(
                (_serviceProvider.GetService(typeof(UnifiedTemplateMatchingService)) as UnifiedTemplateMatchingService)!);
            _availableTasks[TaskType.AutoShield] = new AutoShieldTask(ConfigurationManager.Instance, _serviceProvider);
            _availableTasks[TaskType.CollectVip] = new CollectVipTask();
            _availableTasks[TaskType.ClaimMissions] = new ClaimMissionsTask();
            _availableTasks[TaskType.ResidentWelcome] = new ResidentWelcomeTask();
            _availableTasks[TaskType.AllianceTechnology] = new AllianceTechnologyTask();
            _availableTasks[TaskType.AutoTechnology] = new AutoTechnologyTask();
            _availableTasks[TaskType.AutoRally] = new AutoRallyTask();
            
            _logger.LogInfo($"TaskManager initialized with {_availableTasks.Count} tasks plus startup task");
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInfo("Initializing Task Manager...");
            
            // Initialize startup task first
            _logger.LogInfo("Initializing startup task...");
            await _startupTask.InitializeAsync(_logger, cancellationToken);
            _logger.LogInfo("Startup task initialized successfully");

            // Initialize other tasks
            foreach (var task in _availableTasks.Values)
            {
                await task.InitializeAsync(_logger, cancellationToken);
            }
            
            _logger.LogInfo($"Task Manager initialized with {_availableTasks.Count + 1} tasks");
        }

        public async Task<bool> RunTasksForAccountAsync(AccountSettings account, CancellationToken cancellationToken = default)
        {
            return await RunTasksForAccountAsync(account, cancellationToken, runStartup: true);
        }

        public async Task<bool> RunTasksForAccountAsync(AccountSettings account, CancellationToken cancellationToken = default, bool runStartup = true)
        {
            // Create instance-specific logger if not provided
            using var contextLogger = LogService.ForInstance(account.InstanceNumber, account.AccountName);
            
            try
            {
                contextLogger.LogInfo($"🎯 Starting tasks for account {account.AccountName} (Instance {account.InstanceNumber})");

                if (runStartup)
                {
                    // --- STARTUP SEQUENCE WITH TIMEOUT AND RECOVERY ---
                    TaskExecutionDetails startupResult;
                    bool initialStartupAttempt = true;

                    while (true) // Loop for startup attempts
                {
                    const int startupTimeoutMs = 300_000; // 5 minutes
                    using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    var startupTask = _startupTask.ExecuteAsync(account, contextLogger, startupCts.Token, false, _userNotifications);
                    var timeoutTask = Task.Delay(startupTimeoutMs, startupCts.Token);

                    contextLogger.LogInfo($"🚀 Running startup sequence{(initialStartupAttempt ? "" : " again")} with a {startupTimeoutMs / 1000}s timeout...");

                    var completedTask = await Task.WhenAny(startupTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        await startupCts.CancelAsync();
                        contextLogger.LogError($"❌ Startup sequence for instance {account.InstanceNumber} timed out after {startupTimeoutMs / 1000} seconds.");
                        contextLogger.LogInfo($"🔄 Attempting to reboot instance {account.InstanceNumber}...");

                        var rebootSuccess = await _instanceManager.RebootInstanceAsync(account.InstanceNumber, cancellationToken);
                        if (rebootSuccess)
                        {
                            contextLogger.LogInfo($"✅ Instance {account.InstanceNumber} reboot command sent. Waiting for it to be ready...");
                            var isReady = await _instanceManager.WaitUntilFullyBootedAsync(account.InstanceNumber, cancellationToken, 120);
                            if (isReady)
                            {
                                contextLogger.LogInfo($"✅ Instance {account.InstanceNumber} is ready after reboot. Retrying startup sequence...");
                                initialStartupAttempt = false;
                                continue; // Retry the while loop for startup
                            }
                            contextLogger.LogError($"❌ Instance {account.InstanceNumber} did not become ready after reboot. Halting tasks.");
                            return false;
                        }
                        contextLogger.LogError($"❌ Failed to reboot instance {account.InstanceNumber}. Halting tasks.");
                        return false;
                    }

                    startupResult = await startupTask;
                    contextLogger.LogInfo($"🚀 Startup sequence result: Success={startupResult.Success}, Message='{startupResult.Message}', RecoveryNeeded={startupResult.RecoveryNeeded}");

                    if (startupResult.Success)
                    {
                        break; // Startup successful, exit loop
                    }

                    // Startup failed, check for recovery
                    contextLogger.LogError($"❌ Startup sequence failed: {startupResult.Message}");
                    if (startupResult.RecoveryNeeded)
                    {
                        contextLogger.LogInfo($"🚨 Startup requested recovery. Attempting recovery for {account.AccountName}...");
                        if (_availableTasks.TryGetValue(TaskType.Recovery, out var recoveryTask))
                        {
                            contextLogger.LogInfo($"🔧 Found recovery task, executing...");
                            var recoveryResult = await recoveryTask.ExecuteAsync(account, contextLogger, cancellationToken);
                            contextLogger.LogInfo($"🔧 Recovery result: Success={recoveryResult.Success}, Message='{recoveryResult.Message}'");

                            if (!recoveryResult.Success)
                            {
                                contextLogger.LogError($"❌ Recovery failed: {recoveryResult.Message}. Halting tasks for this account.");
                                return false;
                            }

                            // After recovery, loop back to try startup again
                            initialStartupAttempt = false;
                            continue;
                        }
                        contextLogger.LogError($"❌ Recovery task not found in available tasks.");
                        return false;
                    }
                    contextLogger.LogError($"❌ Startup failed but did not request recovery. Stopping task execution for this account.");
                    return false;
                }
                // --- END OF STARTUP SEQUENCE ---
                }
                else
                {
                    contextLogger.LogInfo($"🔄 Skipping startup sequence for persistent account {account.AccountName} (subsequent cycle)");
                }

                // Run all configured tasks in sequence
                contextLogger.LogInfo($"📋 Found {account.EnabledTasks.Count} enabled tasks: {string.Join(", ", account.EnabledTasks)}");
                
                await ExecuteTasksForAccount(account, cancellationToken, runStartup);

                contextLogger.LogInfo($"✅ All tasks completed successfully");
                return true;
            }
            catch (OperationCanceledException)
            {
                contextLogger.LogInfo($"Task sequence was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                contextLogger.LogError($"❌ Error running tasks: {ex.Message}");
                contextLogger.LogError($"❌ Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        public async Task<Dictionary<string, bool>> RunTasksForMultipleAccountsAsync(
            IEnumerable<AccountSettings> accounts,
            CancellationToken cancellationToken = default)
        {
            var results = new ConcurrentDictionary<string, bool>();
            var accountsList = accounts.ToList();
            _logger.LogInfo($"Starting task execution for {accountsList.Count} accounts.");

            foreach (var account in accountsList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo("Task execution for multiple accounts cancelled.");
                    break;
                }
                var success = await RunTasksForAccountAsync(account, cancellationToken);
                results[account.AccountName] = success;
            }

            return new Dictionary<string, bool>(results);
        }

        private async Task ExecuteTasksForAccount(AccountSettings account, CancellationToken cancellationToken, bool runStartup = true)
        {
            try
            {
                // Use TaskSequence if populated, otherwise use EnabledTasks with proper ordering
                IList<TaskType> tasksToRun;
                if (account.TaskSequence.Any())
                {
                    tasksToRun = account.TaskSequence;
                }
                else
                {
                    tasksToRun = GetProperlyOrderedTasks(account.EnabledTasks, runStartup, account.InstanceNumber).ToList();
                }
                
                if (!tasksToRun.Any())
                {
                    _logger.LogWarning($"[{account.AccountName}] No tasks to run - both TaskSequence and EnabledTasks are empty!");
                    return;
                }
                
                _logger.LogInfo($"[{account.AccountName}] Executing {tasksToRun.Count} tasks: {string.Join(", ", tasksToRun)}");
                _userNotifications?.ShowStatus($"Starting {tasksToRun.Count} tasks for {account.AccountName}: {string.Join(", ", tasksToRun)}");
                
                // Track farming retries to prevent infinite loops
                var farmingRetryCount = 0;
                const int maxFarmingRetries = 3;
                
                for (int i = 0; i < tasksToRun.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var taskType = tasksToRun[i];

                    // Skip disabled tasks (only check if we're using TaskSequence, but never skip system tasks)
                    var systemTasks = new[] { TaskType.Startup, TaskType.Recovery, TaskType.AccountDetection };
                    if (account.TaskSequence.Any() && !account.EnabledTasks.Contains(taskType) && !systemTasks.Contains(taskType)) continue;

                    // Check if task can run (e.g., AutoHunt before Farming)
                    if (!CanRunTask(taskType, account))
                    {
                        _logger.LogInfo($"[{account.AccountName}] Skipping {taskType}: Task cannot run at this time");
                        continue;
                    }

                    var task = GetTaskInstance(taskType);
                    if (task != null)
                    {
                        _logger.LogInfo($"[{account.AccountName}] Starting task: {taskType}");
                        
                        // Show task-specific status for AutoShield
                        if (taskType == TaskType.AutoShield)
                        {
                            var shieldTimeRemaining = ShieldTimeManager.Instance.GetRemainingTime(account.InstanceNumber);
                            if (shieldTimeRemaining.HasValue && shieldTimeRemaining.Value.TotalSeconds > 0)
                            {
                                var shieldText = shieldTimeRemaining.Value.TotalHours >= 1 
                                    ? $"{shieldTimeRemaining.Value.TotalHours:F1}h" 
                                    : $"{shieldTimeRemaining.Value.TotalMinutes:F0}m";
                                _userNotifications?.ShowStatus($"Running {taskType} for {account.AccountName} (Shield: {shieldText})");
                            }
                            else
                            {
                                _userNotifications?.ShowStatus($"Running {taskType} for {account.AccountName}");
                            }
                        }
                        else
                        {
                            _userNotifications?.ShowStatus($"Running {taskType} for {account.AccountName}");
                        }
                        try
                        {
                            // Add timeout for each task
                            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            taskCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute timeout per task

                            var taskStartTime = DateTime.UtcNow;
                            var result = await task.ExecuteAsync(account, _logger, taskCts.Token, false, _userNotifications);
                            var taskDuration = DateTime.UtcNow - taskStartTime;

                            // Log performance warning for tasks that take longer than 5 minutes
                            if (taskDuration.TotalMinutes > 5)
                            {
                                _logger.LogWarning($"[{account.AccountName}] Performance Warning: Task {taskType} took {taskDuration.TotalMinutes:F1} minutes to complete");
                            }
                            else
                            {
                                _logger.LogInfo($"[{account.AccountName}] Task {taskType} completed in {taskDuration.TotalSeconds:F1} seconds");
                            }
                            if (!result.Success)
                            {
                                // Check for special "SKIP_AND_RETRY_LATER" message from farming task
                                if (result.Message == "SKIP_AND_RETRY_LATER")
                                {
                                    if (taskType == TaskType.Farming)
                                    {
                                        farmingRetryCount++;
                                        if (farmingRetryCount >= maxFarmingRetries)
                                        {
                                            _logger.LogWarning($"[{account.AccountName}] Farming task reached maximum retry limit ({maxFarmingRetries}), skipping for this cycle");
                                            _userNotifications?.ShowWarning($"Farming skipped for {account.AccountName} - marches still active after {maxFarmingRetries} attempts");
                                            continue; // Skip farming entirely
                                        }
                                        
                                        _logger.LogInfo($"[{account.AccountName}] Farming delayed due to active march timers - retry {farmingRetryCount}/{maxFarmingRetries}");
                                        _userNotifications?.ShowStatus($"Farming delayed for {account.AccountName} - waiting for marches to return (retry {farmingRetryCount}/{maxFarmingRetries})", NotificationType.Warning);
                                        
                                        // Check if there are other tasks to run first
                                        var remainingTasks = tasksToRun.Skip(i + 1).ToList();
                                        var nonFarmingTasks = remainingTasks.Where(t => t != TaskType.Farming).ToList();
                                        
                                        if (nonFarmingTasks.Any())
                                        {
                                            // Add farming to the end of the queue to retry later
                                            var taskList = tasksToRun.ToList();
                                            taskList.Add(TaskType.Farming);
                                            tasksToRun = taskList;
                                            _logger.LogInfo($"[{account.AccountName}] Added Farming to end of task queue for retry after other tasks");
                                        }
                                        else
                                        {
                                            // No other tasks, wait a bit and retry farming
                                            _logger.LogInfo($"[{account.AccountName}] No other tasks available, waiting 60 seconds before retrying farming");
                                            _userNotifications?.ShowStatus($"Waiting for marches to return for {account.AccountName} - no other tasks to run", NotificationType.Info);
                                            
                                            await WaitIfPausedAsync(cancellationToken);
                                            if (!cancellationToken.IsCancellationRequested)
                                            {
                                                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                                            }
                                            
                                            // Retry farming immediately
                                            i--; // Retry the current task
                                        }
                                    }
                                    else
                                    {
                                        // Handle other tasks that might use SKIP_AND_RETRY_LATER in the future
                                        _logger.LogInfo($"[{account.AccountName}] Task {taskType} delayed - will retry after other tasks");
                                        _userNotifications?.ShowStatus($"{taskType} delayed for {account.AccountName}", NotificationType.Warning);
                                        
                                        var taskList = tasksToRun.ToList();
                                        taskList.Add(taskType);
                                        tasksToRun = taskList;
                                    }
                                    continue; // Skip to next task
                                }
                                // Check if this is just a "cannot execute" case (like ChangeAccount already executed)
                                else if (result.Message == "Task cannot execute")
                                {
                                    _logger.LogInfo($"[{account.AccountName}] Task {taskType} skipped: {result.Message}");
                                    // Don't show error notification for expected skips
                                }
                                else
                                {
                                    // Use enhanced error message if available
                                    var userMessage = result.FailureCategory.HasValue
                                        ? result.GetUserMessage()
                                        : result.Message;
                                    _logger.LogWarning($"[{account.AccountName}] Task {taskType} failed: {result.Message}");
                                    _userNotifications?.ShowError(
                                        $"Task {taskType} failed for {account.AccountName}: {userMessage}",
                                        result.RecoveryNeeded,
                                        result.TroubleshootingHint);
                                }
                                
                                // If task needs recovery, try recovery before continuing
                                if (result.RecoveryNeeded)
                                {
                                    _logger.LogInfo($"[{account.AccountName}] Task {taskType} requested recovery, attempting recovery...");
                                    _userNotifications?.ShowStatus($"Attempting recovery for {taskType} on {account.AccountName}...");
                                    var recoveryTask = GetTaskInstance(TaskType.Recovery) as RecoveryTask;
                                    if (recoveryTask != null)
                                    {
                                        recoveryTask.SetLastFailedTask(taskType);
                                        var recoveryResult = await recoveryTask.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications);
                                        if (!recoveryResult.Success)
                                        {
                                            _logger.LogError($"[{account.AccountName}] Recovery failed, stopping task execution");
                                            return;
                                        }
                                        
                                        // After successful recovery, retry the failed task
                                        _logger.LogInfo($"[{account.AccountName}] Recovery successful, retrying task {taskType}");
                                        _userNotifications?.ShowSuccess($"Recovery successful for {account.AccountName}, retrying {taskType}");
                                        result = await task.ExecuteAsync(account, _logger, cancellationToken, isReRun: true, _userNotifications);
                                        if (!result.Success)
                                        {
                                            _logger.LogError($"[{account.AccountName}] Task {taskType} failed again after recovery");
                                            return;
                                        }
                                    }
                                }
                            }
                            else if (result.RequiresTaskRestart)
                            {
                                _logger.LogInfo($"[{account.AccountName}] Task {taskType} requested task restart. Restarting from loading screen...");
                                
                                // Wait for loading screen to appear and disappear
                                var startupTask = GetTaskInstance(TaskType.Startup) as StartupTask;
                                if (startupTask != null)
                                {
                                    await startupTask.WaitForLoadingToComplete(account, _logger, cancellationToken);
                                }

                                // Reset task index to start after Startup task
                                i = -1; // Will be incremented to 0 in next loop iteration
                                continue;
                            }
                            else if (result.Success)
                            {
                                // Include task-specific message if provided
                                var successMessage = !string.IsNullOrEmpty(result.Message) 
                                    ? $"Task {taskType} completed for {account.AccountName}: {result.Message}"
                                    : $"Task {taskType} completed successfully for {account.AccountName}";
                                _userNotifications?.ShowSuccess(successMessage);
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogInfo($"[{account.AccountName}] Task {taskType} cancelled by user");
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogError($"[{account.AccountName}] Task {taskType} timed out after 15 minutes");
                            // Try recovery after timeout
                            var recoveryTask = GetTaskInstance(TaskType.Recovery) as RecoveryTask;
                            if (recoveryTask != null)
                            {
                                recoveryTask.SetLastFailedTask(taskType);
                                await recoveryTask.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications);
                            }
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[{account.AccountName}] Error executing task {taskType}: {ex.Message}");
                            // Try recovery after any unhandled exception
                            var recoveryTask = GetTaskInstance(TaskType.Recovery) as RecoveryTask;
                            if (recoveryTask != null)
                            {
                                recoveryTask.SetLastFailedTask(taskType);
                                await recoveryTask.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications);
                            }
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogError($"[{account.AccountName}] ❌ Task {taskType} is enabled but not registered in TaskManager! Skipping.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{account.AccountName}] Error in task execution loop: {ex.Message}");
            }
        }

        private bool CanRunTask(TaskType task, AccountSettings account)
        {
            return true;
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

        private bool IsAutoHuntCompleted(AutoHuntSettings settings)
        {
            // AutoHunt is always ready to run - no cooldown restrictions
            return false;
        }

        public void ResetStartupStatus()
        {
            _instanceStartupComplete.Clear();
            _logger.LogInfo("All instance startup statuses have been reset.");
        }

        /// <summary>
        /// Wait if the cycle management service is paused
        /// </summary>
        private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
        {
            // This is a simplified implementation - ideally this should reference the actual CycleManagementService
            // For now, just yield control to allow for cancellation
            await Task.Yield();
        }


        public void SetAccounts(IEnumerable<AccountSettings> accounts)
        {
            _accounts.Clear();
            _accounts.AddRange(accounts);
        }

        private ITask GetTaskInstance(TaskType taskType)
        {
            if (_availableTasks.TryGetValue(taskType, out var task))
            {
                return task;
            }
            return null;
        }

        private IEnumerable<TaskType> GetProperlyOrderedTasks(IEnumerable<TaskType> enabledTasks, bool runStartup = true, int instanceNumber = -1)
        {
            var taskList = enabledTasks.ToList();
            
            // Auto-enable AccountDetection as a system task only on first run/startup
            // On subsequent cycles for persistent accounts, only run if account ID is not cached
            bool shouldAddAccountDetection = false;
            if (!taskList.Contains(TaskType.AccountDetection))
            {
                if (runStartup)
                {
                    // First run - always add AccountDetection
                    shouldAddAccountDetection = true;
                    _logger.LogInfo("Auto-enabled AccountDetection system task (first run)");
                }
                else
                {
                    // Subsequent cycle - only add if no cached account ID exists for this instance
                    // This handles cases where the account might have changed manually
                    var cachedId = Tasks.Modules.AccountDetectionTask.GetCachedAccountId(instanceNumber);
                    if (string.IsNullOrEmpty(cachedId))
                    {
                        shouldAddAccountDetection = true;
                        _logger.LogInfo("Auto-enabled AccountDetection system task (no cached ID found)");
                    }
                    else
                    {
                        _logger.LogInfo("Skipping AccountDetection - account ID already cached for subsequent cycle");
                    }
                }
                
                if (shouldAddAccountDetection)
                {
                    taskList.Insert(0, TaskType.AccountDetection);
                }
            }
            
            // Define proper task execution order
            var taskPriority = new Dictionary<TaskType, int>
            {
                { TaskType.AccountDetection, -1 }, // AccountDetection must run first (after Startup)
                { TaskType.AutoShield, 0 },        // AutoShield should run first to ensure protection
                { TaskType.AutoHunt, 1 },          // AutoHunt should run after shield
                { TaskType.Farming, 2 },           // Farming should run after AutoHunt
                { TaskType.AutoAllianceHelp, 3 },
                { TaskType.TroopTraining, 4 },
                { TaskType.ConquestCollect, 5 },
                { TaskType.ClaimMail, 6 },
                { TaskType.AutoHeal, 7 },
                { TaskType.AutoBuild, 8 },
                { TaskType.AutoClaimHero, 9 },     // Add AutoClaimHero to the priority list
                { TaskType.AllianceTechnology, 10 },
                { TaskType.AutoTechnology, 11 },   // Auto Technology after AllianceTechnology
                { TaskType.CollectVip, 12 },
                { TaskType.ClaimMissions, 13 },
                { TaskType.ResidentWelcome, 14 },
                { TaskType.ChangeAccount, 999 }    // ChangeAccount should always run last
            };

            // Filter out system tasks (but keep AccountDetection since it's auto-enabled above) and sort by priority
            return taskList
                .Where(task => task != TaskType.Startup && task != TaskType.Recovery)
                .OrderBy(task => taskPriority.ContainsKey(task) ? taskPriority[task] : 998);
        }
    }

} 