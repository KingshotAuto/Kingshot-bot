using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using Bot.Core.Logging;
using System.IO;
using Bot.Core.Tasks.Modules;
using Bot.Core.Models;
using Bot.Core.Utils;
using System.Text.Json.Serialization;

namespace Bot.Core.LDPlayer
{
    public class InstanceManager
    {
        private readonly string _ldConsolePath;
        private readonly string _dnConsolePath;
        private readonly LogService _logger;
        private readonly SemaphoreSlim _concurrencyLimiter;
        private const int MaxConcurrentOperations = 10;

        // Fast status checking cache
        private static readonly ConcurrentDictionary<int, (InstanceDetailedStatus Status, DateTime CacheTime)> _statusCache = new();
        private static readonly TimeSpan CacheTTL = TimeSpan.FromSeconds(2); // Performance: increased from 1s to 2s for better cache hit rate

        // Instance configuration defaults
        private readonly int _instanceWidth = 720;
        private readonly int _instanceHeight = 1280;
        private readonly int _instanceDpi = 320;
        private readonly int _instanceCpu = 2;
        private readonly int _instanceMemory = 2048;

        public InstanceManager(string ldConsolePath, string dnConsolePath, LogService logger)
        {
            _ldConsolePath = ldConsolePath;
            _dnConsolePath = dnConsolePath;
            _logger = logger;
            _concurrencyLimiter = new SemaphoreSlim(MaxConcurrentOperations, MaxConcurrentOperations);
            
            // Set the logger in LDPlayerHelper for telemetry
            LDPlayerHelper.SetLogger(logger);
            
            _logger.LogInfo($"InstanceManager initialized with LDPlayer path: {_ldConsolePath}");
            _logger.LogInfo($"LDPlayer console executable exists: {File.Exists(_ldConsolePath)}");
            _logger.LogInfo($"Max concurrent operations: {MaxConcurrentOperations}");
        }

        private async Task<bool> ModifyInstanceSettingsAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"Modifying settings for instance {instanceNumber}: Resolution={_instanceWidth}x{_instanceHeight}@{_instanceDpi}dpi, CPU={_instanceCpu}, Memory={_instanceMemory}MB");
                var result = await RunDnConsoleCommandAsync($"modify --index {instanceNumber} --resolution {_instanceWidth},{_instanceHeight},{_instanceDpi} --cpu {_instanceCpu} --memory {_instanceMemory}", cancellationToken);
                
                if (string.IsNullOrEmpty(result) || !result.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInfo($"✅ Successfully modified settings for instance {instanceNumber}");
                    return true;
                }
                
                _logger.LogError($"Failed to modify instance {instanceNumber} settings: {result}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error modifying instance {instanceNumber} settings: {ex.Message}");
                return false;
            }
        }

        // Performance: batch size for parallel instance startup
        private const int InstanceStartupBatchSize = 2;
        private const int InstanceStartupDelayMs = 1500; // Reduced from 3000ms

        public async Task<Dictionary<int, bool>> StartInstancesAsync(IEnumerable<int> instanceNumbers, CancellationToken cancellationToken = default)
        {
            var results = new ConcurrentDictionary<int, bool>();
            var instanceList = instanceNumbers.ToList();

            // Initialize ADB server before starting any instances
            try
            {
                _logger.LogInfo("Ensuring ADB server is ready before starting instances...");
                var adbController = await ADBConnectionManager.GetConnectionAsync(0, _logger, cancellationToken);
                if (adbController == null)
                {
                    _logger.LogWarning("Initial ADB server check failed, but proceeding with instance starts...");
                }
                else
                {
                    _logger.LogInfo("✅ ADB server initialized successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error during initial ADB check: {ex.Message}. Proceeding with instance starts...");
            }

            // Wait a moment after ADB initialization
            await Task.Delay(1500, cancellationToken);

            // Performance: process instances in parallel batches instead of sequentially
            var batches = instanceList
                .Select((instance, index) => new { instance, index })
                .GroupBy(x => x.index / InstanceStartupBatchSize)
                .Select(g => g.Select(x => x.instance).ToList())
                .ToList();

            _logger.LogInfo($"Starting {instanceList.Count} instances in {batches.Count} batches (batch size: {InstanceStartupBatchSize})");

            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];

                // Add delay between batches (not before first batch)
                if (batchIndex > 0)
                {
                    _logger.LogInfo($"Waiting {InstanceStartupDelayMs}ms before batch {batchIndex + 1}...");
                    await Task.Delay(InstanceStartupDelayMs, cancellationToken);
                }

                _logger.LogInfo($"Starting batch {batchIndex + 1}/{batches.Count}: instances {string.Join(", ", batch)}");

                // Start all instances in this batch in parallel
                var batchTasks = batch.Select(async instanceNumber =>
                {
                    try
                    {
                        var success = await StartInstanceAsync(instanceNumber, cancellationToken);
                        results[instanceNumber] = success;

                        if (success)
                        {
                            _logger.LogInfo($"✅ Instance {instanceNumber} started successfully");

                            // Verify ADB connection
                            try
                            {
                                var adbController = await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger, cancellationToken);
                                if (adbController != null)
                                {
                                    _logger.LogInfo($"✅ ADB connection verified for instance {instanceNumber}");
                                }
                                else
                                {
                                    _logger.LogWarning($"⚠️ ADB connection not established for instance {instanceNumber}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Error verifying ADB for instance {instanceNumber}: {ex.Message}");
                            }
                        }
                        else
                        {
                            _logger.LogError($"❌ Failed to start instance {instanceNumber}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error starting instance {instanceNumber}: {ex.Message}");
                        results[instanceNumber] = false;
                    }
                });

                await Task.WhenAll(batchTasks);
            }

            // Final status report
            var successCount = results.Count(r => r.Value);
            _logger.LogInfo($"Instance start summary: {successCount}/{results.Count} instances started successfully");
            foreach (var result in results.OrderBy(r => r.Key))
            {
                _logger.LogInfo($"Instance {result.Key}: {(result.Value ? "✅ Success" : "❌ Failed")}");
            }

            return new Dictionary<int, bool>(results);
        }

        public async Task<bool> StartInstanceAsync(int instanceNumber, CancellationToken cancellationToken = default, int timeoutSeconds = 60)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.LogInfo($"[Attempt {attempt}/{maxAttempts}] Starting LDPlayer instance {instanceNumber}");
                    
                    // Step 1: Modify instance settings before starting
                    if (!await ModifyInstanceSettingsAsync(instanceNumber, cancellationToken))
                    {
                        _logger.LogWarning($"Failed to modify instance settings, but continuing with launch...");
                        // Continue even if modification fails as the instance might still work with default settings
                    }
                    
                    // Step 2: Launch the instance
                    // Use --index parameter with quotes to handle special characters
                    var startResult = await RunLdConsoleCommandAsync($"launch --index \"{instanceNumber}\"", cancellationToken);
                    
                    // Check for common error messages
                    if (!string.IsNullOrEmpty(startResult))
                    {
                        if (startResult.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                            startResult.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogError($"Instance {instanceNumber} not found. Please verify the instance exists.");
                            return false;
                        }
                        if (startResult.Contains("already running", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInfo($"Instance {instanceNumber} is already running. Proceeding with boot check.");
                            return await WaitUntilFullyBootedAsync(instanceNumber, cancellationToken, timeoutSeconds);
                        }
                        if (startResult.Contains("Usage:", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogError($"Invalid command format. Retrying with alternative format...");
                            // Try alternative command format
                            startResult = await RunLdConsoleCommandAsync($"launch --index={instanceNumber}", cancellationToken);
                        }
                    }

                    // If no output or no error messages, assume success
                    if (string.IsNullOrEmpty(startResult) || (!startResult.Contains("error", StringComparison.OrdinalIgnoreCase) && 
                        !startResult.Contains("Usage:", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInfo($"✅ LDPlayer instance {instanceNumber} launched. Waiting for it to become responsive...");
                        return await WaitUntilFullyBootedAsync(instanceNumber, cancellationToken, timeoutSeconds);
                    }
                    
                    _logger.LogError($"Failed to start instance {instanceNumber}: {startResult}");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(2000, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error starting instance {instanceNumber} (attempt {attempt}): {ex.Message}");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(2000, cancellationToken);
                    }
                }
            }
            
            return false;
        }

        public async Task<bool> StopInstanceAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                // First check if instance exists to avoid unnecessary commands
                var instances = await RunDnConsoleCommandAsync("list2", cancellationToken);
                var instanceExists = instances.Split('\n')
                    .Select(line => line.Split(','))
                    .Where(parts => parts.Length >= 2)
                    .Any(parts => int.TryParse(parts[0], out int index) && index == instanceNumber);

                if (!instanceExists)
                {
                    _logger.LogInfo($"Instance {instanceNumber} doesn't exist, skipping stop command.");
                    return true; // Consider it a success since the instance is already not running
                }

                // Check if instance is running before trying to stop it
                var isRunning = await IsInstanceRunningAsync(instanceNumber, cancellationToken);
                if (!isRunning)
                {
                    _logger.LogInfo($"Instance {instanceNumber} is already stopped.");
                    return true;
                }

                _logger.LogInfo($"Stopping LDPlayer instance {instanceNumber}");
                var result = await RunLdConsoleCommandAsync($"quit --index {instanceNumber}", cancellationToken);
                
                // Wait briefly for the instance to stop
                for (int i = 0; i < 5; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    isRunning = await IsInstanceRunningAsync(instanceNumber, cancellationToken);
                    if (!isRunning)
                    {
                        _logger.LogInfo($"Instance {instanceNumber} stopped successfully.");
                        return true;
                    }
                    
                    await Task.Delay(1000, cancellationToken);
                }

                // If still running, try force quit
                if (await IsInstanceRunningAsync(instanceNumber, cancellationToken))
                {
                    _logger.LogWarning($"Instance {instanceNumber} didn't stop gracefully, attempting force quit...");
                    result = await RunLdConsoleCommandAsync($"quit --index {instanceNumber} --force", cancellationToken);
                    await Task.Delay(2000, cancellationToken); // Give it time to force quit
                }

                return !await IsInstanceRunningAsync(instanceNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping instance {instanceNumber}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RebootInstanceAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"Rebooting LDPlayer instance {instanceNumber}");
                var result = await RunLdConsoleCommandAsync($"reboot --index {instanceNumber}", cancellationToken);
                return string.IsNullOrEmpty(result); // No output on success
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error rebooting instance {instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Performs zoom out operation on the specified instance using dnconsole zoomOut command.
        /// Runs the command multiple times for better effectiveness.
        /// </summary>
        public async Task<bool> ZoomOutAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"Performing zoom out on instance {instanceNumber}");
                
                // Run zoom out command 20 times with 0.2 second intervals
                for (int i = 0; i < 20; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                        
                    await RunDnConsoleCommandAsync($"zoomOut --index {instanceNumber}", cancellationToken);
                    
                    // Wait 200ms between commands, except for the last one
                    if (i < 19)
                        await Task.Delay(200, cancellationToken);
                }
                
                _logger.LogInfo($"Zoom out completed on instance {instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error performing zoom out on instance {instanceNumber}: {ex.Message}");
                return false;
            }
        }
        
        private async Task<string> RunLdConsoleCommandAsync(string arguments, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"Running ldconsole command: {_ldConsolePath} {arguments}");
                
                var psi = new ProcessStartInfo
                {
                    FileName = $"\"{_ldConsolePath}\"",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_ldConsolePath) ?? "",
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                
                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogError("Process.Start returned null for ldconsole command.");
                    return "Process failed to start.";
                }
                
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);
                
                if (!string.IsNullOrWhiteSpace(output))
                    _logger.LogInfo($"ldconsole output: {output.Trim()}");
                
                if (!string.IsNullOrWhiteSpace(error))
                    _logger.LogError($"ldconsole error: {error.Trim()}");
                    
                return string.IsNullOrEmpty(output) ? error : output;
            }
            catch (Exception ex)
            {
                _logger.LogError($"ldconsole command failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Checks if an instance is running using fast detailed status check with fallback to legacy method.
        /// This is the new optimized version that uses list2 for better performance and diagnostics.
        /// </summary>
        public async Task<bool> IsInstanceRunningAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                // Try new fast method first
                var detailedStatus = await GetInstanceDetailedStatusAsync(instanceNumber, cancellationToken);
                if (detailedStatus != null)
                {
                    _logger.LogInfo($"Instance {instanceNumber} fast status: Running={detailedStatus.IsRunning}, " +
                                   $"Android={detailedStatus.AndroidStarted}, PID={detailedStatus.ProcessId}, " +
                                   $"Status={detailedStatus.StatusDescription}");
                    return detailedStatus.IsRunning;
                }
                
                _logger.LogWarning($"Fast status check returned null for instance {instanceNumber}, falling back to legacy method");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Fast status check failed for instance {instanceNumber}, falling back to legacy method: {ex.Message}");
            }
            
            // Fallback to legacy method
            return await IsInstanceRunningLegacyAsync(instanceNumber, cancellationToken);
        }

        /// <summary>
        /// Legacy instance running check using ldconsole isrunning command.
        /// Kept for fallback when detailed status is unavailable.
        /// </summary>
        private async Task<bool> IsInstanceRunningLegacyAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"[Legacy] Checking if instance {instanceNumber} is running...");
                var result = await RunLdConsoleCommandAsync($"isrunning --index {instanceNumber}", cancellationToken);
                _logger.LogInfo($"[Legacy] isrunning check result for instance {instanceNumber}: '{result.Trim()}'");
                
                // Check for both English and localized responses
                var isRunning = result.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                               result.Contains("运行", StringComparison.OrdinalIgnoreCase) ||   // Chinese
                               result.Contains("運行", StringComparison.OrdinalIgnoreCase) ||   // Traditional Chinese
                               result.Contains("実行中", StringComparison.OrdinalIgnoreCase);   // Japanese
                
                _logger.LogInfo(isRunning 
                    ? $"✅ [Legacy] Instance {instanceNumber} is confirmed running" 
                    : $"❌ [Legacy] Instance {instanceNumber} is not running");
                    
                return isRunning;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Legacy] Error checking if instance {instanceNumber} is running: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> WaitUntilFullyBootedAsync(int instanceNumber, CancellationToken cancellationToken, int timeoutSeconds = 120)
        {
            _logger.LogInfo($"[BootWait] Waiting for instance {instanceNumber} to fully boot...");
            var startTime = DateTime.UtcNow;
            var maxWaitTime = TimeSpan.FromSeconds(timeoutSeconds);
            bool recoveryAttempted = false;
            bool serverRecoveryAttempted = false;
            bool initialDelayDone = false;
            int rebootAttempts = 0;
            int adbRetryCount = 0;
            const int MAX_REBOOT_ATTEMPTS = 3;
            // MAX_ADB_RETRIES removed as we now use a more aggressive approach with earlier recovery
            
            // Configurable timeouts (in seconds)
            const int INITIAL_BOOT_WAIT = 60;  // Wait 60 seconds for initial boot before trying reboot
            const int FULL_RECOVERY_WAIT = 90;  // Try full recovery after 90 seconds

            while (DateTime.UtcNow - startTime < maxWaitTime)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"[BootWait] Wait cancelled for instance {instanceNumber}.");
                    return false;
                }

                // Use new detailed status for comprehensive check
                var detailedStatus = await GetInstanceDetailedStatusAsync(instanceNumber, cancellationToken);
                
                if (detailedStatus == null)
                {
                    _logger.LogInfo($"[BootWait] Instance {instanceNumber} not found in list2 output, waiting...");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }
                
                // Check emulator is running
                if (!detailedStatus.IsRunning)
                {
                    _logger.LogInfo($"[BootWait] Instance {instanceNumber} not running (PID: {detailedStatus.ProcessId}), waiting...");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }
                
                // Check Android OS is started
                if (!detailedStatus.AndroidStarted)
                {
                    _logger.LogInfo($"[BootWait] Instance {instanceNumber} emulator running but Android not yet started (Status: {detailedStatus.StatusDescription}), waiting...");
                    
                    // Add initial delay when instance is first detected as running but Android not started
                    if (!initialDelayDone)
                    {
                        _logger.LogInfo($"[BootWait] Instance {instanceNumber} is running. Waiting 5 seconds for Android to start...");
                        await Task.Delay(5000, cancellationToken);
                        initialDelayDone = true;
                    }
                    else
                    {
                        await Task.Delay(2000, cancellationToken);
                    }
                    continue;
                }
                
                // Instance is fully booted, now check ADB responsiveness
                _logger.LogInfo($"[BootWait] Instance {instanceNumber} fully booted ({detailedStatus.StatusDescription}), checking ADB responsiveness...");
                
                // Skip the initial delay since we know Android is started
                initialDelayDone = true;

                var adbController = await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger, cancellationToken);
                if (adbController == null || !await adbController.IsConnectedAndResponsive(cancellationToken))
                {
                    var elapsedTime = DateTime.UtcNow - startTime;
                    adbRetryCount++;

                    // Log more detailed info about ADB connection attempts
                    _logger.LogWarning($"[BootWait] Instance {instanceNumber} not yet responsive to ADB (attempt {adbRetryCount}), waiting... (Elapsed: {elapsedTime.TotalMinutes:F1} minutes)");

                    // Try ADB server recovery earlier and more aggressively for offline devices
                    if (adbRetryCount >= 2 && !serverRecoveryAttempted) // Reduced from MAX_ADB_RETRIES (3) to 2
                    {
                        serverRecoveryAttempted = true;
                        _logger.LogWarning($"[BootWait] ADB connection failed {adbRetryCount} times. Attempting comprehensive recovery...");
                        
                        // Notify user about recovery attempt
                        if (_logger is GUILogService guiLogger)
                        {
                            guiLogger.SendUserNotification($"|USER|Warning|{DateTime.Now:HH:mm:ss}|⚠️ Connection issues detected. Attempting automatic recovery for Instance {instanceNumber}...");
                        }
                        
                        // Try comprehensive recovery
                        var recoveryService = new ADBRecoveryService(_logger);
                        var recoverySuccess = await recoveryService.AttemptFullRecoveryAsync(instanceNumber, cancellationToken);
                        
                        if (recoverySuccess)
                        {
                            _logger.LogInfo($"[BootWait] Recovery successful! Retrying connection...", category: LogCategories.ADB);
                            
                            // Notify user about successful recovery
                            if (_logger is GUILogService guiLogger2)
                            {
                                guiLogger2.SendUserNotification($"|USER|Success|{DateTime.Now:HH:mm:ss}|✅ Automatic recovery successful! Connection restored for Instance {instanceNumber}.");
                            }
                            
                            adbRetryCount = 0; // Reset retry count after recovery
                            await Task.Delay(5000, cancellationToken); // Wait a bit after recovery
                            continue;
                        }
                        else
                        {
                            _logger.LogError($"[BootWait] Comprehensive recovery failed for instance {instanceNumber}", category: LogCategories.ADB);
                            // Fall back to the old recovery method
                            await ADBConnectionManager.AttemptServerRecoveryAsync(instanceNumber, _logger, cancellationToken);
                            adbRetryCount = 0;
                            await Task.Delay(5000, cancellationToken);
                            continue;
                        }
                    }
                    
                    // If not responsive after initial boot wait, try a reboot cycle
                    if (elapsedTime.TotalSeconds >= INITIAL_BOOT_WAIT && rebootAttempts < MAX_REBOOT_ATTEMPTS)
                    {
                        rebootAttempts++;
                        _logger.LogWarning($"[BootWait] Instance {instanceNumber} not responsive after {INITIAL_BOOT_WAIT} seconds. Attempting reboot cycle ({rebootAttempts}/{MAX_REBOOT_ATTEMPTS})...");
                        
                        // Stop the instance
                        await StopInstanceAsync(instanceNumber, cancellationToken);
                        await Task.Delay(5000, cancellationToken);
                        
                        // Start it again
                        await StartInstanceAsync(instanceNumber, cancellationToken);
                        
                        // Reset flags for the new boot attempt
                        initialDelayDone = false;
                        recoveryAttempted = false;
                        serverRecoveryAttempted = false;
                        adbRetryCount = 0;
                        startTime = DateTime.UtcNow; // Reset the timer for the new attempt
                        continue;
                    }
                    
                    if (elapsedTime.TotalSeconds >= FULL_RECOVERY_WAIT && !recoveryAttempted)
                    {
                        recoveryAttempted = true;
                        _logger.LogWarning($"[BootWait] Instance {instanceNumber} still not responsive after {FULL_RECOVERY_WAIT} seconds. Attempting full instance recovery...");
                        if (await RecoverAsync(instanceNumber, cancellationToken))
                        {
                            _logger.LogInfo($"[BootWait] Instance {instanceNumber} recovered successfully.");
                            startTime = DateTime.UtcNow; // Reset timer after successful recovery
                            adbRetryCount = 0;
                        }
                        continue;
                    }

                    _logger.LogInfo($"[BootWait] Instance {instanceNumber} not yet responsive to ADB (attempt {adbRetryCount}), waiting... (Elapsed: {elapsedTime.TotalMinutes:F1} minutes)");
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }

                try
                {
                    var screenshot = await adbController.TakeScreenshotAsync(cancellationToken);
                    if (screenshot != null && screenshot.Length > 0)
                    {
                        _logger.LogInfo($"✅ [BootWait] Instance {instanceNumber} is fully booted and responsive.");
                        return true;
                    }
                    _logger.LogInfo($"[BootWait] Instance {instanceNumber} is responsive but screenshots are not ready yet, waiting...");
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"[BootWait] Screenshot failed for instance {instanceNumber} (this is normal during boot): {ex.Message}");
                }

                await Task.Delay(5000, cancellationToken);
            }

            _logger.LogError($"[BootWait] ❌ Instance {instanceNumber} failed to fully boot within {timeoutSeconds} seconds after {rebootAttempts} reboot attempts and {adbRetryCount} ADB connection attempts.");
            return false;
        }

        private async Task<string> RunDnConsoleCommandAsync(string arguments, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"Running dnconsole command: {_dnConsolePath} {arguments}");

                var psi = new ProcessStartInfo
                {
                    FileName = $"\"{_dnConsolePath}\"",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_dnConsolePath) ?? "",
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogError("Process.Start returned null for dnconsole command.");
                    return "Process failed to start.";
                }

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);

                // Removed verbose dnconsole output logging

                if (!string.IsNullOrWhiteSpace(error))
                    _logger.LogError($"dnconsole error: {error.Trim()}");

                return string.IsNullOrEmpty(output) ? error : output;
            }
            catch (Exception ex)
            {
                _logger.LogError($"dnconsole command failed: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> RecoverAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"[Recover] Starting recovery for instance {instanceNumber}...");

                // First, attempt a graceful reboot using DNCONSOLE.
                _logger.LogInfo($"[Recover] Attempting to reboot instance {instanceNumber} via dnconsole...");
                var rebootResult = await RunDnConsoleCommandAsync($"reboot --index {instanceNumber}", cancellationToken);
                
                // If reboot command succeeds (or at least doesn't fail instantly), wait for it to boot.
                if (string.IsNullOrEmpty(rebootResult))
                {
                    _logger.LogInfo($"[Recover] Reboot command sent. Waiting for instance to become fully responsive...");
                    if (await WaitUntilFullyBootedAsync(instanceNumber, cancellationToken, 180))
                    {
                        _logger.LogInfo($"✅ [Recover] Instance {instanceNumber} recovered successfully via reboot.");
                        return true;
                    }
                    _logger.LogWarning($"[Recover] Reboot seemed successful, but instance {instanceNumber} did not become responsive. Proceeding to harsher recovery.");
                }
                else
                {
                    _logger.LogWarning($"[Recover] Reboot command failed: {rebootResult}. Proceeding to harsher recovery.");
                }

                // If reboot fails, attempt a more forceful quit-and-launch cycle using LDCONSOLE.
                _logger.LogInfo($"[Recover] Attempting to forcefully stop instance {instanceNumber}...");
                await StopInstanceAsync(instanceNumber, cancellationToken);

                // Wait until the instance is confirmed to be NOT running.
                var shutdownSuccess = false;
                for (int i = 0; i < 15; i++) // Wait up to 30 seconds
                {
                    if (!await IsInstanceRunningAsync(instanceNumber, CancellationToken.None))
                    {
                        shutdownSuccess = true;
                        _logger.LogInfo($"[Recover] Instance {instanceNumber} confirmed to be shut down.");
                        break;
                    }
                    await Task.Delay(2000, cancellationToken);
                }

                if (!shutdownSuccess)
                {
                    _logger.LogError($"[Recover] ❌ Instance {instanceNumber} failed to shut down. Recovery failed.");
                    return false;
                }
                
                // Now, try to launch it again.
                _logger.LogInfo($"[Recover] Attempting to relaunch instance {instanceNumber}...");
                var launchResult = await RunLdConsoleCommandAsync($"launch --index {instanceNumber}", cancellationToken);
                if (!string.IsNullOrEmpty(launchResult))
                {
                    _logger.LogError($"[Recover] ❌ Failed to launch instance {instanceNumber} after shutdown: {launchResult}. Recovery failed.");
                    return false;
                }

                _logger.LogInfo($"[Recover] Instance {instanceNumber} relaunched. Waiting for it to become fully responsive...");
                if (await WaitUntilFullyBootedAsync(instanceNumber, cancellationToken, 180))
                {
                    _logger.LogInfo($"✅ [Recover] Instance {instanceNumber} recovered successfully via quit/launch cycle.");
                    return true;
                }
                
                _logger.LogError($"[Recover] ❌ Instance {instanceNumber} failed to become responsive after relaunch. Recovery failed.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recover] ❌ An unexpected error occurred during recovery of instance {instanceNumber}: {ex.Message}");
                return false;
            }
        }

        public async Task<List<string>> GetRunningInstancesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await RunDnConsoleCommandAsync("list2", cancellationToken);
                return result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => 
                    {
                        var parts = line.Split(',');
                        return parts.Length >= 5 && parts[4] == "1"; // Index 4 is running status (1 = running)
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting running instances: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Fast batch status check for all instances using list2 command.
        /// Returns detailed status information including process IDs, window handles, and Android boot state.
        /// </summary>
        public async Task<Dictionary<int, InstanceDetailedStatus>> GetAllInstanceStatusesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await RunDnConsoleCommandAsync("list2", cancellationToken);
                var statuses = ParseList2Output(result);
                
                // Cache all statuses for rapid subsequent queries
                var cacheTime = DateTime.UtcNow;
                foreach (var status in statuses.Values)
                {
                    _statusCache[status.Index] = (status, cacheTime);
                }
                
                _logger.LogInfo($"Retrieved status for {statuses.Count} instances");
                return statuses;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting all instance statuses: {ex.Message}");
                return new Dictionary<int, InstanceDetailedStatus>();
            }
        }

        /// <summary>
        /// Fast single instance status with rich data using cached list2 results.
        /// Falls back to fresh list2 call if cache is stale.
        /// </summary>
        public async Task<InstanceDetailedStatus?> GetInstanceDetailedStatusAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                // Check cache first
                if (_statusCache.TryGetValue(instanceNumber, out var cached))
                {
                    var cacheAge = DateTime.UtcNow - cached.CacheTime;
                    if (cacheAge <= CacheTTL)
                    {
                        _logger.LogInfo($"Instance {instanceNumber} status from cache (age: {cacheAge.TotalMilliseconds:F0}ms)");
                        return cached.Status;
                    }
                }

                // Cache miss or stale - get fresh data
                var allStatuses = await GetAllInstanceStatusesAsync(cancellationToken);
                return allStatuses.TryGetValue(instanceNumber, out var status) ? status : null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting detailed status for instance {instanceNumber}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses the output from LDPlayer's list2 command into structured status objects.
        /// Format: index,title,top window handle,bind window handle,android started,pid,vbox pid
        /// </summary>
        private Dictionary<int, InstanceDetailedStatus> ParseList2Output(string output)
        {
            var statuses = new Dictionary<int, InstanceDetailedStatus>();
            
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning("Empty output from list2 command");
                return statuses;
            }

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Parsing {lines.Length} instances

            foreach (var line in lines)
            {
                try
                {
                    var parts = line.Split(',');
                    if (parts.Length < 7)
                    {
                        _logger.LogWarning($"Skipping malformed list2 line (expected 7 parts, got {parts.Length}): {line}");
                        continue;
                    }

                    var status = InstanceDetailedStatus.FromList2Parts(parts);
                    if (status != null)
                    {
                        statuses[status.Index] = status;
                        // Removed verbose per-instance status logging
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to parse list2 line: {line}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error parsing list2 line '{line}': {ex.Message}");
                }
            }
            
            return statuses;
        }

        /// <summary>
        /// Clears the instance status cache. Useful for forcing fresh status queries.
        /// </summary>
        public static void ClearStatusCache()
        {
            _statusCache.Clear();
        }

        /// <summary>
        /// Gets cache statistics for monitoring and debugging.
        /// </summary>
        public static Dictionary<string, object> GetCacheStatistics()
        {
            var now = DateTime.UtcNow;
            var freshEntries = _statusCache.Values.Count(c => now - c.CacheTime <= CacheTTL);
            var staleEntries = _statusCache.Count - freshEntries;

            return new Dictionary<string, object>
            {
                ["TotalCachedInstances"] = _statusCache.Count,
                ["FreshEntries"] = freshEntries,
                ["StaleEntries"] = staleEntries,
                ["CacheTTL"] = CacheTTL.TotalSeconds
            };
        }
    }
    
    public class InstanceStatus
    {
        public int InstanceNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsRunning { get; set; }
        public int ProcessId { get; set; }
        public long MemoryUsage { get; set; }
        public long ResponseTimeMs { get; set; }
        public DateTime LastChecked { get; set; } = DateTime.Now;
    }
} 