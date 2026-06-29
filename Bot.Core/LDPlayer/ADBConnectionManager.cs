using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Config;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Manages ADB server lifecycle, connections to emulator instances, and recovery operations.
    /// Provides thread-safe access to ADBController objects for each LDPlayer instance.
    /// </summary>
    public static class ADBConnectionManager
    {
        // Holds active ADBController connections, keyed by instance number
        private static readonly ConcurrentDictionary<int, ADBController> _connections = new();
        // Lock for connection management - CHANGED TO PER-INSTANCE LOCKS
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _instanceLocks = new();
        // Tracks if the ADB server is started
        private static bool _adbServerStarted = false;
        // Lock for ADB server operations
        private static readonly SemaphoreSlim _serverLock = new SemaphoreSlim(1, 1);
        private static Timer? _healthCheckTimer;
        
        
        // Connection throttling to prevent ADB server overload - now dynamically scaled
        private static SemaphoreSlim _connectionThrottle = new SemaphoreSlim(3, 3); // Will be updated based on TotalRunningInstances
        private static int _connectionFailureCount = 0;
        private static DateTime _lastConnectionFailure = DateTime.MinValue;
        private static int _maxConcurrentConnections = 3; // Dynamic based on configuration
        private const int CONNECTION_FAILURE_THRESHOLD = 3;
        private const int CONNECTION_BACKOFF_SECONDS = 10;
        private const int MIN_CONCURRENT_CONNECTIONS = 2; // Minimum safety limit
        private const int MAX_CONCURRENT_CONNECTIONS = 10; // Maximum safety limit

        
        /// <summary>
        /// Updates connection limits based on the total running instances configuration
        /// </summary>
        public static void UpdateConnectionLimits(int totalRunningInstances)
        {
            // Calculate optimal connection limit: 1.5x the running instances, with safety bounds
            var optimalLimit = Math.Max(MIN_CONCURRENT_CONNECTIONS, Math.Min(MAX_CONCURRENT_CONNECTIONS, (int)(totalRunningInstances * 1.5)));
            
            if (optimalLimit != _maxConcurrentConnections)
            {
                _maxConcurrentConnections = optimalLimit;
                
                // Create new semaphore with updated limit
                var oldSemaphore = _connectionThrottle;
                _connectionThrottle = new SemaphoreSlim(optimalLimit, optimalLimit);
                
                // Dispose old semaphore safely
                oldSemaphore?.Dispose();
                
                // Log the change
                var logger = new LogService();
                logger.LogInfo($"[ADB] Connection limits updated: {optimalLimit} concurrent connections for {totalRunningInstances} running instances");
            }
        }

        /// <summary>
        /// Gets or creates a per-instance lock
        /// </summary>
        private static SemaphoreSlim GetInstanceLock(int instanceNumber)
        {
            return _instanceLocks.GetOrAdd(instanceNumber, _ => new SemaphoreSlim(1, 1));
        }

        private static async Task<bool> VerifyDeviceAsync(string adbPath, string deviceSerial, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(5000); // 5 second timeout
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                var verifyProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "devices",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                if (verifyProcess != null)
                {
                    string output = await verifyProcess.StandardOutput.ReadToEndAsync();
                    await verifyProcess.WaitForExitAsync(linkedCts.Token);
                    
                    bool isConnected = output.Contains($"{deviceSerial}\tdevice");
                    bool isOffline = output.Contains($"{deviceSerial}\toffline");
                    bool isUnauthorized = output.Contains($"{deviceSerial}\tunauthorized");
                    
                    return isConnected;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning($"[ADB] Device verification timed out for {deviceSerial}");
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADB] Error verifying device {deviceSerial}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Ensures the ADB server is started. Starts it if not already running.
        /// </summary>
        private static async Task<bool> EnsureADBServerStartedAsync(LogService logger, CancellationToken cancellationToken)
        {
            if (_adbServerStarted) return true;
            
            if (!await _serverLock.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
            {
                logger.LogError("Server lock timeout after 30s");
                throw new TimeoutException("ADB server lock timeout");
            }
            try
            {
                if (_adbServerStarted) return true;
                
                var adbPath = LDPlayerHelper.GetADBPath();
                if (string.IsNullOrEmpty(adbPath))
                {
                    logger.LogError("[ADB] ADB path not found.");
                    return false;
                }

                // First check if server is already running
                var checkProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "devices",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                if (checkProcess != null)
                {
                    using var timeoutCts = new CancellationTokenSource(10000); // 10 second timeout
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    
                    string output = await checkProcess.StandardOutput.ReadToEndAsync();
                    await checkProcess.WaitForExitAsync(linkedCts.Token);
                    
                    if (!output.Contains("* daemon not running"))
                    {
                        _adbServerStarted = true;
                        logger.LogInfo("[ADB] ADB server already running");
                        return true;
                    }
                }

                // Only start server if it's not running
                logger.LogInfo("[ADB] Starting ADB server...");
                var startProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                
                if (startProcess != null)
                {
                    using var timeoutCts = new CancellationTokenSource(10000); // 10 second timeout
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    
                    await startProcess.WaitForExitAsync(linkedCts.Token);
                    await Task.Delay(1000, cancellationToken); // Short delay for server startup
                    
                    _adbServerStarted = true;
                    logger.LogInfo("[ADB] ADB server started successfully");
                    return true;
                }
                
                logger.LogError("[ADB] Failed to start ADB server");
                return false;
            }
            catch (OperationCanceledException)
            {
                logger.LogError("[ADB] ADB server startup timed out");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADB] Failed to ensure ADB server: {ex.Message}");
                return false;
            }
            finally
            {
                _serverLock.Release();
            }
        }


        /// <summary>
        /// Gets or creates an ADBController for the specified instance number.
        /// Ensures the ADB server is running and manages connection reuse.
        /// </summary>
        public static async Task<ADBController?> GetConnectionAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken = default)
        {
            try
            {
                
                // Check if we should apply backoff due to recent failures
                if (ShouldApplyConnectionBackoff())
                {
                    var backoffTime = TimeSpan.FromSeconds(CONNECTION_BACKOFF_SECONDS);
                    logger.LogWarning($"[ADB] Applying connection backoff ({backoffTime.TotalSeconds}s) due to recent failures");
                    await Task.Delay(backoffTime, cancellationToken);
                }

                // Throttle concurrent ADB connections to prevent server overload
                var throttleStartTime = DateTime.UtcNow;
                if (!await _connectionThrottle.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                {
                    logger.LogError($"Connection throttle timeout after 30s for instance {instanceNumber}");
                    throw new TimeoutException($"Connection throttle timeout for instance {instanceNumber}");
                }
                var throttleWaitTime = DateTime.UtcNow - throttleStartTime;
                
                try
                {

                    // Check existing connection first
                    if (_connections.TryGetValue(instanceNumber, out var existingController))
                    {
                        // Quick health check
                        if (await existingController.IsConnectedAndResponsive(cancellationToken).ConfigureAwait(false))
                        {
                            return existingController;
                        }
                        // Remove stale connection
                        _connections.TryRemove(instanceNumber, out _);
                        existingController.Dispose();
                    }

                // Ensure server is running
                if (!await EnsureADBServerStartedAsync(logger, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var instanceLock = GetInstanceLock(instanceNumber);
                if (!await instanceLock.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                {
                    logger.LogError($"Instance lock timeout after 30s for instance {instanceNumber}");
                    throw new TimeoutException($"Instance lock timeout for instance {instanceNumber}");
                }
                try
                {
                    // Double-check after acquiring lock
                    if (_connections.TryGetValue(instanceNumber, out existingController))
                    {
                        return existingController;
                    }

                    var adbPath = LDPlayerHelper.GetADBPath();
                    var deviceSerial = $"emulator-{5554 + instanceNumber * 2}";


                    
                    // Wait for device to appear (up to 20 seconds)
                    bool deviceFound = false;
                    
                    for (int i = 0; i < 20; i++)
                    {
                        if (await VerifyDeviceAsync(adbPath, deviceSerial, logger, cancellationToken))
                        {
                            deviceFound = true;
                            break;
                        }
                        
                        if (i < 19) // Don't delay on last attempt
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }

                    if (!deviceFound)
                    {
                        logger.LogError($"[ADB] Device {deviceSerial} not found after 20 seconds", category: LogCategories.ADB);
                        
                        // Final attempt with comprehensive recovery
                        logger.LogWarning($"[ADB] Attempting comprehensive recovery for instance {instanceNumber}...");
                        try
                        {
                            var recoveryService = new ADBRecoveryService(logger);
                            await recoveryService.AttemptFullRecoveryAsync(instanceNumber, cancellationToken);
                            
                            // Try one more time after recovery
                            await Task.Delay(3000, cancellationToken); // Wait 3 seconds after recovery
                            if (await VerifyDeviceAsync(adbPath, deviceSerial, logger, cancellationToken))
                            {
                                logger.LogInfo($"[ADB] Device {deviceSerial} recovered after comprehensive recovery");
                                deviceFound = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"[ADB] Comprehensive recovery failed: {ex.Message}");
                        }
                        
                        if (!deviceFound)
                        {
                            return null;
                        }
                    }

                    // Create controller
                    var controller = new ADBController(adbPath, deviceSerial, logger);
                    _connections[instanceNumber] = controller;
                    
                    
                    logger.LogInfo($"[ADB] Created connection for instance {instanceNumber}");
                    return controller;
                }
                finally
                {
                    instanceLock.Release();
                }
                }
                finally
                {
                    _connectionThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                RecordConnectionFailure();
                
                
                logger.LogError($"[ADB] Error getting connection for instance {instanceNumber}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Closes and disposes the ADBController for a specific instance.
        /// </summary>
        public static void CloseConnection(int instanceNumber, LogService logger)
        {
            try
            {
                if (_connections.TryRemove(instanceNumber, out var controller))
                {
                    controller?.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADB] Error closing connection for instance {instanceNumber}: {ex.Message}");
            }
        }

        /// <summary>
        /// Closes and disposes all ADBController connections and resets the server started flag.
        /// </summary>
        public static async Task CloseAllConnections(LogService logger)
        {
            try
            {
                // Stop health monitoring first
                StopHealthMonitoring();
                
                // Kill ADB server to force all connections closed
                var adbPath = LDPlayerHelper.GetADBPath();
                if (!string.IsNullOrEmpty(adbPath))
                {
                    try
                    {
                        var killProcess = Process.Start(new ProcessStartInfo
                        {
                            FileName = adbPath,
                            Arguments = "kill-server",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        });
                        if (killProcess != null)
                        {
                            using var cts = new CancellationTokenSource(2000); // 2 second timeout
                            try
                            {
                                await killProcess.WaitForExitAsync(cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // Timeout - kill process if still running
                                if (!killProcess.HasExited)
                                {
                                    killProcess.Kill();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[ADB] Non-critical error killing ADB server: {ex.Message}");
                    }
                }
                
                // Dispose all controllers
                foreach (var kvp in _connections)
                {
                    try
                    {
                        kvp.Value?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[ADB] Error closing connection for instance {kvp.Key}: {ex.Message}");
                    }
                }
                
                // Clear all connections
                _connections.Clear();
                _adbServerStarted = false;
                
                // Clear all instance locks
                foreach (var kvp in _instanceLocks)
                {
                    try
                    {
                        kvp.Value?.Dispose();
                    }
                    catch { /* Ignore lock disposal errors */ }
                }
                _instanceLocks.Clear();
                
                
                logger.LogInfo("[ADB] All connections and resources cleaned up.");
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADB] Error closing all connections: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to recover the ADB server by killing and restarting it. Ensures exclusive access during recovery.
        /// </summary>
        public static async Task AttemptServerRecoveryAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[ADB] Attempting ADB server recovery for instance {instanceNumber}...");
            if (!await _serverLock.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
            {
                logger.LogError("Server lock timeout after 30s");
                throw new TimeoutException("ADB server lock timeout");
            }
            try
            {
                var adbPath = LDPlayerHelper.GetADBPath();
                if (string.IsNullOrEmpty(adbPath))
                {
                    logger.LogError("[ADB] Cannot perform recovery, ADB path not found.");
                    return;
                }

                // Get list of currently connected devices
                var devicesProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "devices",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                string? devicesOutput = null;
                if (devicesProcess != null)
                {
                    devicesOutput = await devicesProcess.StandardOutput.ReadToEndAsync();
                    await devicesProcess.WaitForExitAsync(cancellationToken);
                }

                // Check if other instances are connected
                var deviceSerial = $"emulator-{5554 + instanceNumber * 2}";
                var otherInstancesConnected = false;
                if (!string.IsNullOrEmpty(devicesOutput))
                {
                    var connectedDevices = devicesOutput.Split('\n')
                        .Where(line => line.Contains("\tdevice"))
                        .Select(line => line.Split('\t')[0])
                        .Where(device => device != deviceSerial)
                        .ToList();

                    otherInstancesConnected = connectedDevices.Any();
                    if (otherInstancesConnected)
                    {
                        logger.LogWarning($"[ADB] Other instances are connected: {string.Join(", ", connectedDevices)}");
                        logger.LogWarning("[ADB] Skipping full server restart to avoid disrupting other instances.");
                        
                        // Try to reconnect just this instance
                        var connectProcess = Process.Start(new ProcessStartInfo
                        {
                            FileName = adbPath,
                            Arguments = $"connect {deviceSerial}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        });

                        if (connectProcess != null)
                        {
                            string connectOutput = await connectProcess.StandardOutput.ReadToEndAsync();
                            await connectProcess.WaitForExitAsync(cancellationToken);
                            logger.LogInfo($"[ADB] Reconnect attempt result: {connectOutput.Trim()}");
                        }
                        return;
                    }
                }

                // Only do a full server restart if no other instances are connected
                _adbServerStarted = false;

                // Kill Server
                logger.LogInfo($"[ADB] Killing ADB server with: '{adbPath} kill-server'");
                var killPsi = new ProcessStartInfo(adbPath, "kill-server")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(killPsi))
                {
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync(cancellationToken);
                        logger.LogInfo($"[ADB] kill-server output: {output.Trim()}");
                        if (!string.IsNullOrWhiteSpace(error))
                            logger.LogError($"[ADB] kill-server error: {error.Trim()}");
                    }
                }
                
                await Task.Delay(5000, cancellationToken); // Brief pause after killing the server

                // Start Server using the existing robust method
                await EnsureADBServerStartedAsync(logger, cancellationToken);
                
                logger.LogInfo("[ADB] ADB server recovery attempt finished.");
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADB] Error during ADB server recovery: {ex.Message}");
            }
            finally
            {
                _serverLock.Release();
            }
        }

        /// <summary>
        /// Checks if a connection exists for the given instance number.
        /// </summary>
        public static bool HasConnection(int instanceNumber)
        {
            return _connections.ContainsKey(instanceNumber);
        }

        public static async Task<string> ListAvailableDevicesAsync(LogService logger, CancellationToken cancellationToken = default)
        {
            try
            {
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                
                var psi = new ProcessStartInfo
                {
                    FileName = dnConsolePath,
                    Arguments = "list2",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    logger.LogError("[ADB] Process.Start returned null for dnconsole.");
                    return string.Empty;
                }

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);
                
                if (!string.IsNullOrWhiteSpace(error))
                {
                    logger.LogError($"[ADB] Error listing devices with dnconsole: {error.Trim()}");
                }
                
                return output;
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADB] Error listing devices: {ex.Message}");
                return string.Empty;
            }
        }

        public static void StartHealthMonitoring(LogService logger)
        {
            _healthCheckTimer?.Dispose();
            
            _healthCheckTimer = new Timer(async _ =>
            {
                var staleConnections = new List<int>();
                
                foreach (var kvp in _connections)
                {
                    try
                    {
                        if (!await kvp.Value.IsConnectedAndResponsive())
                        {
                            staleConnections.Add(kvp.Key);
                        }
                    }
                    catch
                    {
                        staleConnections.Add(kvp.Key);
                    }
                }
                
                // Remove stale connections
                foreach (var instanceNumber in staleConnections)
                {
                    if (_connections.TryRemove(instanceNumber, out var controller))
                    {
                        controller.Dispose();
                        logger.LogWarning($"[ADB] Removed stale connection for instance {instanceNumber}");
                    }
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public static void StopHealthMonitoring()
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
        }

        /// <summary>
        /// Connection throttling and monitoring helper methods
        /// </summary>
        public static int GetActiveConnectionCount()
        {
            return _connections.Count;
        }

        /// <summary>
        /// Gets the current maximum concurrent connections limit
        /// </summary>
        public static int GetMaxConcurrentConnections()
        {
            return _maxConcurrentConnections;
        }

        private static bool ShouldApplyConnectionBackoff()
        {
            if (_connectionFailureCount >= CONNECTION_FAILURE_THRESHOLD)
            {
                var timeSinceLastFailure = DateTime.UtcNow - _lastConnectionFailure;
                return timeSinceLastFailure < TimeSpan.FromSeconds(CONNECTION_BACKOFF_SECONDS);
            }
            return false;
        }

        private static void RecordConnectionFailure()
        {
            _connectionFailureCount++;
            _lastConnectionFailure = DateTime.UtcNow;
        }
    }
} 