using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Exceptions;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Threading;
using System.Timers;
using System;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using Timer = System.Threading.Timer;

namespace Bot.Core.Tasks
{
    public abstract class BaseTaskWithCommonPatterns : ITask
    {
        public abstract TaskType TaskType { get; }
        public abstract string Name { get; }
        
        protected string ImageTemplateFolder => Path.Combine(AppContext.BaseDirectory, "templates", "images", GetImageFolderName());
        protected UnifiedTemplateMatchingService? _templateMatcher;
        protected LogService? _instanceLogger;
        
        // Static reference to cycle management service for pause checking
        private static CycleManagementService? _cycleManagementService;
        
        public static void SetCycleManagementService(CycleManagementService cycleService)
        {
            _cycleManagementService = cycleService;
        }
        
        protected async Task WaitIfPausedAsync(CancellationToken cancellationToken)
        {
            if (_cycleManagementService != null && _cycleManagementService.IsPaused)
            {
                _instanceLogger?.LogInfo("⏸️ Task paused - waiting for resume...");

                // Performance: use event-based waiting instead of polling
                // PauseEvent is Reset when paused, Set when resumed
                var pauseEvent = _cycleManagementService.PauseEvent;
                await Task.Run(() =>
                {
                    // Wait for either resume (event set) or cancellation
                    WaitHandle.WaitAny(new[] { pauseEvent.WaitHandle, cancellationToken.WaitHandle });
                }, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    _instanceLogger?.LogInfo("▶️ Task resumed from pause");
                }
            }
        }
        
        // Task-level ADB connection caching (V2 system)
        private object? _cachedController;
        private int _cachedInstanceNumber = -1;
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private const int HEALTH_CHECK_CACHE_SECONDS = 5;
        
        // Instance-specific screenshot caching to prevent conflicts between instances
        private static readonly ConcurrentDictionary<int, (byte[]? screenshot, DateTime time)> _instanceScreenshotCache = new();
        private static readonly TimeSpan ScreenshotCacheTimeout = TimeSpan.FromMilliseconds(500); // Optimized: increased for better cache hit rate
        private const int MAX_SCREENSHOT_CACHE_SIZE = 10; // Maximum cached screenshots

        // Performance: timer-based cleanup instead of per-operation cleanup
        private static readonly Timer _screenshotCacheCleanupTimer = new Timer(
            _ => CleanupOldScreenshots(),
            null,
            TimeSpan.FromSeconds(5),  // Initial delay
            TimeSpan.FromSeconds(5)); // Cleanup every 5 seconds
        
        // Global resource throttling - adjusted for multi-instance support
        // Performance: increased from 3 to 6 concurrent screenshots for better multi-instance throughput
        private static readonly SemaphoreSlim _globalOperationLimiter = new SemaphoreSlim(9, 9);
        private static readonly SemaphoreSlim _globalScreenshotLimiter = new SemaphoreSlim(6, 6);
        
        // Instance-specific operation locks to prevent same-instance conflicts
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _instanceOperationLocks = new();
        
        // Timing controls for auto tasks to prevent excessive triggering
        private static readonly ConcurrentDictionary<int, DateTime> _lastAllianceHelpTime = new();
        private static readonly ConcurrentDictionary<int, DateTime> _lastAutoHealTime = new();
        private static readonly TimeSpan AutoTaskCooldown = TimeSpan.FromMinutes(1); // 1 minute cooldown
        
        private static readonly string[] ResourceIcons = { "bread.png", "wood.png", "stone.png", "iron.png" };
        
        /// <summary>
        /// Checks current view and automatically runs Alliance Help and/or Auto Heal if enabled
        /// </summary>
        protected async Task<bool> RunAutoTasksIfNeededAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Skip auto tasks for Alliance Help, Auto Heal, and Startup to prevent recursion and startup interference
                if (TaskType == TaskType.AutoAllianceHelp || TaskType == TaskType.AutoHeal || TaskType == TaskType.Startup)
                {
                    logger.LogInfo($"[{account.AccountName}] Skipping auto tasks for {Name} (excluded task type)");
                    return true;
                }

                // Check if either task is enabled
                bool allianceHelpEnabled = account.EnabledTasks.Contains(TaskType.AutoAllianceHelp);
                bool autoHealEnabled = account.EnabledTasks.Contains(TaskType.AutoHeal);

                if (!allianceHelpEnabled && !autoHealEnabled)
                {
                    logger.LogInfo($"[{account.AccountName}] No auto tasks enabled for {Name} (Alliance Help: {allianceHelpEnabled}, Auto Heal: {autoHealEnabled})");
                    return true; // Nothing to do
                }

                // Detect current view
                var locator = new LocatorService(logger, account, TaskType);
                var currentView = await locator.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);

                logger.LogInfo($"[{account.AccountName}] Current view: {currentView}, checking for auto tasks (Alliance Help: {allianceHelpEnabled}, Auto Heal: {autoHealEnabled})...");

                // Run Alliance Help if enabled and we're in base view or map view
                if (allianceHelpEnabled && (currentView == ViewType.BaseView || currentView == ViewType.MapView))
                {
                    // Check cooldown period
                    var now = DateTime.UtcNow;
                    var lastAllianceHelpTime = _lastAllianceHelpTime.GetValueOrDefault(account.InstanceNumber, DateTime.MinValue);
                    var timeSinceLastRun = now - lastAllianceHelpTime;
                    
                    if (timeSinceLastRun < AutoTaskCooldown)
                    {
                        var remainingTime = AutoTaskCooldown - timeSinceLastRun;
                        logger.LogInfo($"[{account.AccountName}] Alliance Help skipped - cooldown active (last run {timeSinceLastRun.TotalSeconds:F0}s ago, next available in {remainingTime.TotalSeconds:F0}s)");
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] 🤝 Running Alliance Help before continuing with {Name}...");
                        userNotifications?.ShowStatus($"Running Alliance Help before {Name}...", NotificationType.Info);
                        
                        // Update last run time before execution
                        _lastAllianceHelpTime[account.InstanceNumber] = now;

                    try
                    {
                        var allianceHelpTask = new AllianceHelpTask();
                        allianceHelpTask.MaxAttempts = 1; // Only try once when called from auto-task system
                        await allianceHelpTask.InitializeAsync(logger, cancellationToken);
                        var allianceResult = await allianceHelpTask.ExecuteAsync(account, logger, cancellationToken, false, userNotifications);
                        
                        if (!allianceResult.Success)
                        {
                            logger.LogWarning($"[{account.AccountName}] Alliance Help failed: {allianceResult.Message}");
                            // Continue anyway - don't fail the main task
                        }
                        else
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Alliance Help completed successfully");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Error running Alliance Help: {ex.Message}");
                        // Continue with main task
                    }
                    }
                }
                else if (allianceHelpEnabled)
                {
                    logger.LogInfo($"[{account.AccountName}] Alliance Help enabled but skipped (current view: {currentView}, requires BaseView or MapView)");
                }

                // Run Auto Heal if enabled and we're in map view
                if (autoHealEnabled && currentView == ViewType.MapView)
                {
                    // Check cooldown period
                    var now = DateTime.UtcNow;
                    var lastAutoHealTime = _lastAutoHealTime.GetValueOrDefault(account.InstanceNumber, DateTime.MinValue);
                    var timeSinceLastRun = now - lastAutoHealTime;
                    
                    if (timeSinceLastRun < AutoTaskCooldown)
                    {
                        var remainingTime = AutoTaskCooldown - timeSinceLastRun;
                        logger.LogInfo($"[{account.AccountName}] Auto Heal skipped - cooldown active (last run {timeSinceLastRun.TotalSeconds:F0}s ago, next available in {remainingTime.TotalSeconds:F0}s)");
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] 🏥 Running Auto Heal before continuing with {Name}...");
                        userNotifications?.ShowStatus($"Running Auto Heal before {Name}...", NotificationType.Info);
                        
                        // Update last run time before execution
                        _lastAutoHealTime[account.InstanceNumber] = now;

                    try
                    {
                        var autoHealTask = new AutoHealTask(logger);
                        await autoHealTask.InitializeAsync(logger, cancellationToken);
                        var healResult = await autoHealTask.ExecuteAsync(account, logger, cancellationToken, false, userNotifications);
                        
                        if (!healResult.Success)
                        {
                            logger.LogWarning($"[{account.AccountName}] Auto Heal failed: {healResult.Message}");
                            // Continue anyway - don't fail the main task
                        }
                        else
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Auto Heal completed successfully");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Error running Auto Heal: {ex.Message}");
                        // Continue with main task
                    }
                    }
                }
                else if (autoHealEnabled)
                {
                    logger.LogInfo($"[{account.AccountName}] Auto Heal enabled but skipped (current view: {currentView}, requires MapView)");
                }

                logger.LogInfo($"[{account.AccountName}] Auto task check completed, continuing with {Name}");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error running auto tasks: {ex.Message}");
                // Continue with main task even if auto tasks fail
                return true;
            }
        }
        
        public async Task<TaskExecutionDetails> ExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                if (!await CanExecuteAsync(account, logger, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Task cannot execute");
                }

                // Store logger for pause checking
                _instanceLogger = logger;
                
                // Check for pause before executing task
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return TaskExecutionDetails.Failed("Task was cancelled");
                }

                await OnInitializeAsync(logger, cancellationToken);
                
                // Run automatic Alliance Help and Auto Heal tasks if needed
                await RunAutoTasksIfNeededAsync(account, logger, cancellationToken, userNotifications);
                
                var result = await ExecuteTaskLogicAsync(account, logger, cancellationToken, isReRun, userNotifications);
                
                // Clear cached controller on task completion to prevent stale connections
                if (result.Success)
                {
                    // Keep connection cached for successful tasks to benefit subsequent operations
                    // Only clear on errors or after a certain time
                }
                else
                {
                    logger.LogInfo($"Clearing cached ADB controller due to task failure: {Name}");
                    ClearCachedADBController();
                }
                
                return result;
            }
            catch (BotLostException ex) when (ex.NeedsRecovery)
            {
                logger.LogError($"Bot lost during {Name}, initiating recovery...");
                ClearCachedADBController(); // Clear cache before recovery
                var recoveryTask = new Modules.RecoveryTask();
                recoveryTask.SetLastFailedTask(TaskType);
                return await recoveryTask.ExecuteAsync(account, logger, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error executing {Name}: {ex.Message}");
                ClearCachedADBController(); // Clear cache on unexpected errors
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }
        
        public virtual async Task<bool> CanExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default)
        {
            // System tasks should always be able to execute
            var systemTasks = new[] { TaskType.Startup, TaskType.Recovery, TaskType.AccountDetection };
            if (systemTasks.Contains(TaskType))
            {
                return await IsInstanceRunningAsync(account.InstanceNumber, logger, cancellationToken);
            }
            
            // Other tasks require explicit enabling
            if (!account.EnabledTasks.Contains(TaskType))
            {
                return false;
            }
            
            return await IsInstanceRunningAsync(account.InstanceNumber, logger, cancellationToken);
        }
        
        public virtual async Task InitializeAsync(LogService logger, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(ImageTemplateFolder))
            {
                Directory.CreateDirectory(ImageTemplateFolder);
                logger.LogInfo($"Created image template folder: {ImageTemplateFolder}");
            }
            
            _templateMatcher = new UnifiedTemplateMatchingService(logger);
            
            await OnInitializeAsync(logger, cancellationToken);
        }
        
        protected abstract Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null);
        
        protected virtual Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
        
        protected virtual string GetImageFolderName()
        {
            return TaskType.ToString().ToLowerInvariant();
        }
        
        // UNIFIED ADB OPERATIONS WITH CONSISTENT ERROR HANDLING
        
        /// <summary>
        /// Execute operation with ADB controller, with consistent error handling and caching
        /// </summary>
        protected async Task<T> WithADBControllerAsync<T>(
            int instanceNumber, 
            LogService logger, 
            Func<object, Task<T>> operation, 
            T? defaultValue = default)
        {
            // Apply global throttling to prevent resource overload
            if (!await _globalOperationLimiter.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                logger.LogError("Global operation limiter timeout after 30s");
                return defaultValue!;
            }
            try
            {
                var adbController = await GetCachedADBController(instanceNumber, logger);
                if (adbController == null)
                {
                    logger.LogError("Could not get cached ADB controller");
                    return defaultValue!;
                }

                // Add timeout for ADB operations - reduced from 30s to 10s
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    return await operation(adbController).WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogError("ADB operation timed out after 10 seconds");
                    // Clear cache on timeout as connection might be problematic
                    ClearCachedADBController();
                    return defaultValue!;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"ADB operation failed: {ex.Message}");
                // Clear cache on error to force fresh connection next time
                if (ex.Message.Contains("device") || ex.Message.Contains("connection"))
                {
                    logger.LogInfo("Clearing cached ADB controller due to connection error");
                    ClearCachedADBController();
                }
                return defaultValue!;
            }
            finally
            {
                _globalOperationLimiter.Release();
            }
        }
        
        /// <summary>
        /// Click at specific coordinates with error handling
        /// </summary>
        protected async Task<bool> ClickAsync(int instanceNumber, LogService logger, Point point)
        {
            return await WithADBControllerAsync(instanceNumber, logger, async adb =>
            {
                await ADBMigrationHelper.TapAsync(adb, point.X, point.Y, logger);
                logger.LogInfo($"Clicked at ({point.X}, {point.Y})");
                return true;
            }, false);
        }
        
        /// <summary>
        /// Click at center location within rectangle bounds
        /// </summary>
        protected async Task<bool> ClickRandomInRectAsync(int instanceNumber, LogService logger, Rectangle rect, Random? random = null)
        {
            // Changed to click at center instead of random point to ensure reliable clicking
            var point = rect.GetCenter();
            var success = await ClickAsync(instanceNumber, logger, point);
            if (success)
            {
                logger.LogInfo($"Clicked center location ({point.X}, {point.Y}) in bounds {rect}");
            }
            return success;
        }
        
        /// <summary>
        /// Click at center of rectangle (explicit method for clarity)
        /// </summary>
        protected async Task<bool> ClickCenterInRectAsync(int instanceNumber, LogService logger, Rectangle rect)
        {
            var point = rect.GetCenter();
            var success = await ClickAsync(instanceNumber, logger, point);
            if (success)
            {
                logger.LogInfo($"Clicked center location ({point.X}, {point.Y}) in bounds {rect}");
            }
            return success;
        }
        
        /// <summary>
        /// Take a screenshot with instance-specific caching, global throttling, and reduced timeout
        /// </summary>
        protected async Task<byte[]?> TakeScreenshotAsync(int instanceNumber, LogService logger, int maxRetries = 2)
        {
            // Check instance-specific cache first
            if (_instanceScreenshotCache.TryGetValue(instanceNumber, out var cached) &&
                cached.screenshot != null &&
                DateTime.UtcNow - cached.time < ScreenshotCacheTimeout)
            {
                return cached.screenshot;
            }

            // Performance: removed double-locking - global semaphore is sufficient
            // The per-instance cache already prevents duplicate screenshots for the same instance
            if (!await _globalScreenshotLimiter.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                logger.LogError("Global screenshot limiter timeout after 30s");
                return Array.Empty<byte>();
            }

            try
            {
                // Double-check cache after acquiring lock
                if (_instanceScreenshotCache.TryGetValue(instanceNumber, out cached) &&
                    cached.screenshot != null &&
                    DateTime.UtcNow - cached.time < ScreenshotCacheTimeout)
                {
                    return cached.screenshot;
                }

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        // Use cached controller directly for screenshot operations (most frequent)
                        var adbController = await GetCachedADBController(instanceNumber, logger);
                        if (adbController == null)
                        {
                            logger.LogWarning($"Screenshot attempt {attempt}/{maxRetries} failed - no ADB controller");
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(1000);
                                continue;
                            }
                            return null;
                        }

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // Reduced from 10s to 5s
                        var screenshot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cts.Token);

                        if (screenshot != null && screenshot.Length > 0)
                        {
                            // Cache the screenshot for this specific instance
                            // Add to cache with size management
                            AddToScreenshotCache(instanceNumber, screenshot);
                            
                            return screenshot;
                        }

                        logger.LogWarning($"Screenshot attempt {attempt}/{maxRetries} failed - empty result");
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogError($"Screenshot attempt {attempt}/{maxRetries} timed out after 5 seconds");
                        // Clear cache on timeout as connection might be problematic
                        ClearCachedADBController();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"Screenshot attempt {attempt}/{maxRetries} failed: {ex.Message}");
                        // Clear cache on connection errors
                        if (ex.Message.Contains("device") || ex.Message.Contains("connection"))
                        {
                            ClearCachedADBController();
                        }
                    }

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(500); // Reduced delay from 1s to 500ms
                    }
                }

                return null;
            }
            finally
            {
                _globalScreenshotLimiter.Release();
            }
        }

        private static void AddToScreenshotCache(int instanceNumber, byte[] screenshot)
        {
            // If cache is at limit, remove oldest entries first
            if (_instanceScreenshotCache.Count >= MAX_SCREENSHOT_CACHE_SIZE)
            {
                var oldestKeys = _instanceScreenshotCache
                    .OrderBy(kvp => kvp.Value.time)
                    .Take(_instanceScreenshotCache.Count - MAX_SCREENSHOT_CACHE_SIZE + 1)
                    .Select(kvp => kvp.Key)
                    .ToList();
                    
                foreach (var key in oldestKeys)
                {
                    _instanceScreenshotCache.TryRemove(key, out _);
                }
            }
            
            // Add new screenshot to cache
            _instanceScreenshotCache[instanceNumber] = (screenshot, DateTime.UtcNow);
            // Performance: removed per-operation cleanup - now handled by timer
        }

        private static void CleanupOldScreenshots()
        {
            // Clean up screenshots older than cache timeout + buffer
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(3);
            var keysToRemove = _instanceScreenshotCache
                .Where(kvp => kvp.Value.time < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var key in keysToRemove)
            {
                _instanceScreenshotCache.TryRemove(key, out _);
            }
        }
        
        /// <summary>
        /// Swipe with error handling
        /// </summary>
        protected async Task<bool> SwipeAsync(int instanceNumber, LogService logger, Point from, Point to, int durationMs = 500)
        {
            return await WithADBControllerAsync(instanceNumber, logger, async adb =>
            {
                await ADBMigrationHelper.SwipeAsync(adb, from.X, from.Y, to.X, to.Y, durationMs, logger);
                logger.LogInfo($"Swiped from ({from.X}, {from.Y}) to ({to.X}, {to.Y})");
                return true;
            }, false);
        }
        
        // Method from TroopTrainingTask, now in base
        protected async Task<bool> ClickRandomInRectAsyncWithRetry(int instanceNumber, LogService logger, Rectangle rect, CancellationToken cancellationToken, int maxRetries = 3, Random? random = null)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                try
                {
                    // Use the existing ClickRandomInRectAsync from BaseTaskWithCommonPatterns
                    if (await ClickRandomInRectAsync(instanceNumber, logger, rect, random)) 
                    {
                        return true;
                    }
                    if (attempt < maxRetries)
                    {
                        logger.LogInfo($"Click attempt {attempt} in {rect} failed, retrying in {GameCoordinates.Delays.BetweenRetries}ms...");
                        await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                    }
                }
                catch (OperationCanceledException) {
                    logger.LogInfo($"ClickRandomInRectAsyncWithRetry cancelled during Task.Delay for {rect}");
                    return false;
                }
                catch (Exception ex)
                {
                    logger.LogError($"Click attempt {attempt} in {rect} exception: {ex.Message}");
                    if (ex.Message.Contains("device") && ex.Message.Contains("not found")) // Simplified ADB check
                    {
                        logger.LogInfo("Detected ADB connection loss, clearing cache and attempting to recover...");
                        ClearCachedADBController(); // Clear cache immediately on connection loss
                        try
                        {
                            var adbPath = Bot.Core.Utils.LDPlayerHelper.GetADBPath();
                            // 1. adb devices
                            var psi1 = new System.Diagnostics.ProcessStartInfo(adbPath, "devices")
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (var process = System.Diagnostics.Process.Start(psi1))
                            {
                                if (process != null)
                                {
                                    string output = await process.StandardOutput.ReadToEndAsync();
                                    string error = await process.StandardError.ReadToEndAsync();
                                    await process.WaitForExitAsync(cancellationToken);
                                    logger.LogInfo($"ADB devices output before recovery: {output.Trim()} {error.Trim()}");
                                }
                            }
                            // 2. adb kill-server
                            var psi2 = new System.Diagnostics.ProcessStartInfo(adbPath, "kill-server")
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (var process = System.Diagnostics.Process.Start(psi2))
                            {
                                if (process != null)
                                {
                                    await process.WaitForExitAsync(cancellationToken);
                                }
                            }
                            // 3. adb start-server
                            var psi3 = new System.Diagnostics.ProcessStartInfo(adbPath, "start-server")
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (var process = System.Diagnostics.Process.Start(psi3))
                            {
                                if (process != null)
                                {
                                    string output = await process.StandardOutput.ReadToEndAsync();
                                    string error = await process.StandardError.ReadToEndAsync();
                                    await process.WaitForExitAsync(cancellationToken);
                                    logger.LogInfo($"ADB start-server output: {output.Trim()} {error.Trim()}");
                                }
                            }
                            // 4. adb devices (again)
                            var psi4 = new System.Diagnostics.ProcessStartInfo(adbPath, "devices")
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (var process = System.Diagnostics.Process.Start(psi4))
                            {
                                if (process != null)
                                {
                                    string output = await process.StandardOutput.ReadToEndAsync();
                                    string error = await process.StandardError.ReadToEndAsync();
                                    await process.WaitForExitAsync(cancellationToken);
                                    logger.LogInfo($"ADB devices output after recovery: {output.Trim()} {error.Trim()}");
                                }
                            }
                        }
                        catch (Exception adbEx)
                        {
                            logger.LogError($"ADB recovery steps failed: {adbEx.Message}");
                        }
                        // V2 system handles connection cleanup automatically
                        await Task.Delay(2000, cancellationToken); // Allow time for device to reappear or connection to be ready for re-establishment
                        var newController = await GetADBController(instanceNumber, logger); // Attempt to re-get/re-establish
                        if (newController == null) logger.LogError("Failed to re-establish ADB connection after suspected loss.");
                        else logger.LogInfo("ADB connection re-established after suspected loss.");
                    }
                    if (attempt < maxRetries) 
                    {
                        try 
                        {
                            await Task.Delay(GameCoordinates.Delays.BetweenErrorRetries, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogInfo($"ClickRandomInRectAsyncWithRetry cancelled during error retry delay for {rect}");
                            return false;
                        }
                    }
                }
            }
            logger.LogError($"Failed to click in {rect} after {maxRetries} attempts");
            return false;
        }
        
        // UNIFIED TEMPLATE MATCHING OPERATIONS
        
        /// <summary>
        /// Wait for image to appear with unified template matching and return position
        /// </summary>
        protected async Task<(bool found, Rectangle position, double confidence)> WaitForImageWithPositionAsync(
            string imageName, 
            int instanceNumber, 
            LogService logger, 
            CancellationToken cancellationToken,
            int timeoutMs = 5000,
            double threshold = GameCoordinates.Thresholds.StandardConfidence,
            bool useEnhancedMatching = false,
            Rectangle? searchArea = null,
            double[]? scales = null)
        {
            if (_templateMatcher == null)
            {
                logger.LogError("CRITICAL: _templateMatcher is null. It should have been initialized before calling this method.");
                return (false, Rectangle.Empty, 0.0);
            }

            logger.LogInfo($"Looking for {imageName}...");
            var stopwatch = Stopwatch.StartNew();
            var lastLogTime = DateTime.UtcNow;
            double bestConfidence = 0.0;
            const int LOG_INTERVAL_MS = 5000; // Log every 5 seconds

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"Image search for '{imageName}' cancelled.");
                    return (false, Rectangle.Empty, 0.0);
                }

                var screenshotBytes = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshotBytes == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'. Retrying in {GameCoordinates.Delays.BetweenRetries}ms...");
                    await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                    continue;
                }

                string imagePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(imagePath))
                {
                    logger.LogError($"Image template not found: {imagePath}");
                    return (false, Rectangle.Empty, 0.0);
                }
                
                var result = _templateMatcher.MatchTemplate(
                    screenshotBytes,
                    imagePath,
                    instanceNumber,
                    threshold,
                    useEnhancedMatching ? UnifiedTemplateMatchingService.StandardScales : scales,
                    useEnhancedMatching,
                    searchArea
                );

                if (result.found)
                {
                    logger.LogInfo($"{imageName} detected {result.confidence:F3} {result.matchRect.Location}");
                    return (true, result.matchRect, result.confidence);
                }
                else
                {
                    // Track best confidence found
                    bestConfidence = Math.Max(bestConfidence, result.confidence);
                    
                    // Log progress every 5 seconds
                    if ((DateTime.UtcNow - lastLogTime).TotalMilliseconds >= LOG_INTERVAL_MS)
                    {
                        logger.LogInfo($"{imageName} not found after {stopwatch.ElapsedMilliseconds/1000:F1}s - best confidence: {bestConfidence:F3} (threshold: {threshold:F3})");
                        lastLogTime = DateTime.UtcNow;
                    }
                }

                // Wait before retrying, but not after the last attempt
                if (stopwatch.ElapsedMilliseconds + GameCoordinates.Delays.BetweenRetries < timeoutMs)
                {
                    await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                }
            }

            // Log final result if we didn't find the image
            if (bestConfidence > 0.0)
            {
                logger.LogInfo($"{imageName} not found after {stopwatch.ElapsedMilliseconds/1000:F1}s - best confidence: {bestConfidence:F3} (threshold: {threshold:F3})");
            }
            
            // Return the best confidence found even if no match
            return (false, Rectangle.Empty, 0.0);
        }

        /// <summary>
        /// Wait for image to appear with unified template matching
        /// </summary>
        protected async Task<bool> WaitForImageAsync(
            string imageName, 
            int instanceNumber, 
            LogService logger, 
            CancellationToken cancellationToken,
            int timeoutMs = 5000,
            double threshold = GameCoordinates.Thresholds.StandardConfidence,
            bool useEnhancedMatching = false,
            Rectangle? searchArea = null,
            double[]? scales = null)
        {
            if (_templateMatcher == null)
            {
                logger.LogError("CRITICAL: _templateMatcher is null. It should have been initialized before calling this method.");
                return false;
            }

            logger.LogInfo($"Looking for {imageName}...");
            var stopwatch = Stopwatch.StartNew();
            var lastLogTime = DateTime.UtcNow;
            double bestConfidence = 0.0;
            const int LOG_INTERVAL_MS = 5000; // Log every 5 seconds

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"Image search for '{imageName}' cancelled.");
                    return false;
                }

                var screenshotBytes = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshotBytes == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'. Retrying in {GameCoordinates.Delays.BetweenRetries}ms...");
                    await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                    continue;
                }

                string imagePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(imagePath))
                {
                    logger.LogError($"Image template not found: {imagePath}");
                    return false;
                }
                
                var result = _templateMatcher.MatchTemplate(
                    screenshotBytes,
                    imagePath,
                    instanceNumber,
                    threshold,
                    useEnhancedMatching ? UnifiedTemplateMatchingService.StandardScales : scales,
                    useEnhancedMatching,
                    searchArea
                );

                if (result.found)
                {
                    logger.LogInfo($"{imageName} detected {result.confidence:F3} {result.matchRect.Location}");
                    return true;
                }
                else
                {
                    // Track best confidence found
                    bestConfidence = Math.Max(bestConfidence, result.confidence);
                    
                    // Log progress every 5 seconds
                    if ((DateTime.UtcNow - lastLogTime).TotalMilliseconds >= LOG_INTERVAL_MS)
                    {
                        logger.LogInfo($"{imageName} not found after {stopwatch.ElapsedMilliseconds/1000:F1}s - best confidence: {bestConfidence:F3} (threshold: {threshold:F3})");
                        lastLogTime = DateTime.UtcNow;
                    }
                }

                // Wait before retrying, but not after the last attempt
                if (stopwatch.ElapsedMilliseconds + GameCoordinates.Delays.BetweenRetries < timeoutMs)
                {
                    await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                }
            }

            // Log final result if we didn't find the image
            if (bestConfidence > 0.0)
            {
                logger.LogInfo($"{imageName} not found after {stopwatch.ElapsedMilliseconds/1000:F1}s - best confidence: {bestConfidence:F3} (threshold: {threshold:F3})");
            }
            
            return false;
        }
        
        /// <summary>
        /// Find and click image with unified template matching
        /// </summary>
        protected async Task<bool> FindAndClickImageAsync(string imageName, int instanceNumber, LogService logger, double threshold = 0.6, bool useEnhancedMatching = false, Rectangle? searchArea = null)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) return false;

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
                    logger.LogInfo($"Found '{imageName}' at {matchRect} with confidence {confidence:F3}");
                    return await ClickRandomInRectAsync(instanceNumber, logger, matchRect);
                }

                logger.LogWarning($"Could not find '{imageName}' (best confidence: {confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickImageAsync: {ex.Message}");
                return false;
            }
        }
        
        // LEGACY COMPATIBILITY METHODS
        
        protected virtual async Task<bool> IsInstanceRunningAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken = default)
        {
            try
            {
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                if (string.IsNullOrEmpty(ldConsolePath) || string.IsNullOrEmpty(dnConsolePath))
                {
                    logger.LogError("LDPlayer path not found. Cannot check if instance is running.");
                    return false;
                }
                var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);
                return await instanceManager.IsInstanceRunningAsync(instanceNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error checking if instance {instanceNumber} is running: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Get cached ADB controller for improved performance during task execution
        /// </summary>
        protected async Task<object?> GetCachedADBController(int instanceNumber, LogService logger, CancellationToken cancellationToken = default)
        {
            // Check if we have a valid cached controller for this instance
            if (_cachedController != null && _cachedInstanceNumber == instanceNumber)
            {
                // Skip health check if recently verified
                bool skipHealthCheck = (DateTime.UtcNow - _lastHealthCheck).TotalSeconds < HEALTH_CHECK_CACHE_SECONDS;
                
                if (skipHealthCheck || (_cachedController is ADBControllerV2 v2Controller && await v2Controller.IsConnectedAndResponsiveAsync(cancellationToken)))
                {
                    if (!skipHealthCheck)
                    {
                        _lastHealthCheck = DateTime.UtcNow;
                    }
                    return _cachedController;
                }
                
                // Cached controller is no longer valid
                logger.LogInfo($"Cached ADB controller for instance {instanceNumber} is no longer responsive, acquiring new connection");
                _cachedController = null;
            }
            
            // Get new controller from connection manager (V2 system)
            _cachedController = await ADBMigrationHelper.GetConnectionAsync(instanceNumber, logger, cancellationToken);
            _cachedInstanceNumber = instanceNumber;
            _lastHealthCheck = DateTime.UtcNow;
            
            if (_cachedController == null)
            {
                logger.LogError($"Failed to get ADB controller for instance {instanceNumber}");
                return null;
            }
            else
            {
                logger.LogInfo($"Cached new ADB controller for instance {instanceNumber}");
            }
            
            return _cachedController;
        }

        /// <summary>
        /// Legacy method for backward compatibility - now uses caching
        /// </summary>
        protected async Task<object?> GetADBController(int instanceNumber, LogService logger)
        {
            return await GetCachedADBController(instanceNumber, logger);
        }
        
        /// <summary>
        /// Clear cached ADB controller (useful for task cleanup or error recovery)
        /// </summary>
        protected void ClearCachedADBController()
        {
            _cachedController = null;
            _cachedInstanceNumber = -1;
            _lastHealthCheck = DateTime.MinValue;
        }

        // ============== FAILURE HELPER METHODS ==============
        // Use these for consistent, user-friendly error messages

        /// <summary>
        /// Create a connection failure (ADB, emulator issues)
        /// </summary>
        protected TaskExecutionDetails FailConnection(string step, string detail, bool recoveryNeeded = false)
            => TaskExecutionDetails.FailedWith(FailureCategory.Connection, step, detail, recoveryNeeded: recoveryNeeded);

        /// <summary>
        /// Create a detection failure (template matching, OCR)
        /// </summary>
        protected TaskExecutionDetails FailDetection(string step, string what, bool recoveryNeeded = false)
            => TaskExecutionDetails.FailedWith(FailureCategory.Detection, step, $"Could not find {what}", recoveryNeeded: recoveryNeeded);

        /// <summary>
        /// Create a navigation failure (can't reach expected view)
        /// </summary>
        protected TaskExecutionDetails FailNavigation(string step, string detail, bool recoveryNeeded = true)
            => TaskExecutionDetails.FailedWith(FailureCategory.Navigation, step, detail, recoveryNeeded: recoveryNeeded);

        /// <summary>
        /// Create a timeout failure
        /// </summary>
        protected TaskExecutionDetails FailTimeout(string step, string operation, bool recoveryNeeded = false)
            => TaskExecutionDetails.FailedWith(FailureCategory.Timeout, step, $"{operation} timed out", recoveryNeeded: recoveryNeeded);

        /// <summary>
        /// Create a configuration failure
        /// </summary>
        protected TaskExecutionDetails FailConfiguration(string step, string detail)
            => TaskExecutionDetails.FailedWith(FailureCategory.Configuration, step, detail, recoveryNeeded: false);

        /// <summary>
        /// Create a game state failure (unexpected dialog, wrong screen)
        /// </summary>
        protected TaskExecutionDetails FailGameState(string step, string detail, bool recoveryNeeded = true)
            => TaskExecutionDetails.FailedWith(FailureCategory.GameState, step, detail, recoveryNeeded: recoveryNeeded);

        /// <summary>
        /// Notify user of task progress
        /// </summary>
        protected void NotifyProgress(IUserNotificationService? notifications, string step, int percentage)
        {
            notifications?.ShowProgress(Name, percentage, step);
        }
    }
}