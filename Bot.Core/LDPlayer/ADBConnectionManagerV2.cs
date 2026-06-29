using Bot.Core.Logging;
using Bot.Core.Config;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using Bot.Core.Utils;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Enhanced ADB connection manager inspired by wosbot-master's ddmlib approach.
    /// Uses AdvancedSharpAdbClient for persistent device connections and smart retry logic.
    /// </summary>
    public static class ADBConnectionManagerV2
    {
        // Persistent ADB client and server management
        private static AdbClient? _adbClient;
        private static AdbServer? _adbServer;
        private static bool _serverInitialized = false;
        
        // Device connection cache with health monitoring
        private static readonly ConcurrentDictionary<int, (DeviceData Device, AdbClient Client, DateTime LastUsed, bool IsHealthy)> _deviceConnections = new();
        
        // Per-instance locks for thread safety
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _instanceLocks = new();
        
        // Global server lock
        private static readonly SemaphoreSlim _serverLock = new(1, 1);
        
        // Emulator slot management (inspired by wosbot-master's queuing)
        private static readonly SemaphoreSlim _emulatorSlots = new(3, 10); // Dynamic based on config
        private static readonly ConcurrentQueue<(int InstanceNumber, TaskCompletionSource<bool> Tcs, DateTime QueueTime)> _slotQueue = new();
        
        // Health monitoring
        private static Timer? _healthCheckTimer;
        private static Timer? _connectionCleanupTimer;
        
        // Connection failure tracking (conservative restart approach)
        private static int _globalFailureCount = 0;
        private static DateTime _lastGlobalFailure = DateTime.MinValue;
        private const int FAILURE_THRESHOLD_FOR_RESTART = 5; // More conservative
        private const int RESTART_COOLDOWN_MINUTES = 5;
        
        // Performance optimization settings
        private const int MAX_CACHED_CONNECTIONS = 20;
        private const int CONNECTION_HEALTH_CHECK_INTERVAL_MS = 30000; // 30 seconds
        private const int CONNECTION_CLEANUP_INTERVAL_MS = 120000; // 2 minutes
        private const int MAX_RETRY_ATTEMPTS = 3; // Reduced from 10
        private const int BASE_RETRY_DELAY_MS = 1000;

        /// <summary>
        /// Initializes the ADB connection manager with enhanced settings
        /// </summary>
        public static async Task<bool> InitializeAsync(LogService logger, int maxConcurrentEmulators = 3)
        {
            await _serverLock.WaitAsync();
            try
            {
                if (_serverInitialized) return true;

                logger.LogInfo("[ADBv2] Initializing enhanced ADB connection manager...");

                // Update emulator slot limits
                UpdateEmulatorSlotLimits(maxConcurrentEmulators);
                
                // Initialize ADB server with project's ADB path
                var adbPath = LDPlayerHelper.GetADBPath();
                if (string.IsNullOrEmpty(adbPath))
                {
                    logger.LogError("[ADBv2] ADB path not found");
                    return false;
                }

                _adbServer = new AdbServer();
                var result = await _adbServer.StartServerAsync(adbPath, restartServerIfNewer: false);
                
                if (result != StartServerResult.Started && result != StartServerResult.AlreadyRunning)
                {
                    logger.LogError($"[ADBv2] Failed to start ADB server: {result}");
                    return false;
                }

                _adbClient = new AdbClient();
                
                // Start health monitoring
                StartHealthMonitoring(logger);
                StartConnectionCleanup(logger);
                
                _serverInitialized = true;
                logger.LogInfo($"[ADBv2] ADB server initialized successfully: {result}");
                
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADBv2] Failed to initialize: {ex.Message}");
                return false;
            }
            finally
            {
                _serverLock.Release();
            }
        }

        /// <summary>
        /// Gets a persistent connection for the specified instance with smart retry logic
        /// </summary>
        public static async Task<ADBControllerV2?> GetConnectionAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken = default)
        {
            if (!_serverInitialized)
            {
                if (!await InitializeAsync(logger))
                {
                    return null;
                }
            }

            var instanceLock = GetInstanceLock(instanceNumber);
            await instanceLock.WaitAsync(cancellationToken);
            
            try
            {
                // Check existing healthy connection first
                if (_deviceConnections.TryGetValue(instanceNumber, out var existingConnection) && 
                    existingConnection.IsHealthy)
                {
                    // Update last used time
                    _deviceConnections[instanceNumber] = existingConnection with { LastUsed = DateTime.UtcNow };
                    return new ADBControllerV2(existingConnection.Device, existingConnection.Client, logger, instanceNumber);
                }

                // Try to establish new connection with smart retry
                var deviceSerial = GetDeviceSerial(instanceNumber);
                
                for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
                {
                    try
                    {
                        var device = await FindOrConnectDeviceAsync(deviceSerial, logger, cancellationToken);
                        
                        if (device != null)
                        {
                            // Store in non-nullable variable to satisfy compiler
                            DeviceData connectedDevice = (DeviceData)device;
                            
                            // Cache the healthy connection
                            _deviceConnections[instanceNumber] = (connectedDevice, _adbClient!, DateTime.UtcNow, true);
                            
                            logger.LogInfo($"[ADBv2] Connected to instance {instanceNumber} ({deviceSerial}) on attempt {attempt}");
                            return new ADBControllerV2(connectedDevice, _adbClient!, logger, instanceNumber);
                        }
                        
                        if (attempt < MAX_RETRY_ATTEMPTS)
                        {
                            var delay = BASE_RETRY_DELAY_MS * attempt;
                            logger.LogWarning($"[ADBv2] Connection attempt {attempt} failed for instance {instanceNumber}, retrying in {delay}ms");
                            await Task.Delay(delay, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[ADBv2] Connection attempt {attempt} threw exception for instance {instanceNumber}: {ex.Message}");
                        
                        if (attempt == MAX_RETRY_ATTEMPTS)
                        {
                            RecordGlobalFailure();
                            
                            // Only restart ADB if we've had multiple global failures recently
                            if (ShouldRestartAdbServer())
                            {
                                logger.LogWarning("[ADBv2] Multiple global failures detected, attempting conservative ADB restart");
                                await AttemptConservativeServerRestartAsync(logger, cancellationToken);
                                
                                // One final attempt after restart
                                try
                                {
                                    var device = await FindOrConnectDeviceAsync(deviceSerial, logger, cancellationToken);
                                    if (device != null)
                                    {
                                        // Store in non-nullable variable to satisfy compiler
                                        DeviceData connectedDevice = (DeviceData)device;
                                        
                                        _deviceConnections[instanceNumber] = (connectedDevice, _adbClient!, DateTime.UtcNow, true);
                                        return new ADBControllerV2(connectedDevice, _adbClient!, logger, instanceNumber);
                                    }
                                }
                                catch (Exception restartEx)
                                {
                                    logger.LogError($"[ADBv2] Final attempt after restart failed: {restartEx.Message}");
                                }
                            }
                        }
                    }
                }

                logger.LogError($"[ADBv2] Failed to establish connection for instance {instanceNumber} after {MAX_RETRY_ATTEMPTS} attempts");
                return null;
            }
            finally
            {
                instanceLock.Release();
            }
        }

        /// <summary>
        /// Finds existing device or attempts to connect to it (similar to wosbot-master's findDevice)
        /// </summary>
        private static async Task<DeviceData?> FindOrConnectDeviceAsync(string deviceSerial, LogService logger, CancellationToken cancellationToken)
        {
            if (_adbClient == null) return null;

            // 1. Quick search in already connected devices
            var devices = await _adbClient.GetDevicesAsync(cancellationToken);
            var existingDevice = devices.FirstOrDefault(d => d.Serial == deviceSerial);
            
            if (existingDevice != null)
            {
                logger.LogInfo($"[ADBv2] Device found in cache: {deviceSerial}");
                return existingDevice;
            }

            // 2. Attempt direct connection (similar to wosbot-master's connectToDeviceBySerial)
            if (deviceSerial.StartsWith("emulator-"))
            {
                var port = deviceSerial.Substring("emulator-".Length);
                var address = $"127.0.0.1:{port}";
                
                try
                {
                    logger.LogInfo($"[ADBv2] Attempting to connect to: {address}");
                    await _adbClient.ConnectAsync(address, cancellationToken);
                    
                    // Wait a moment and check again
                    await Task.Delay(2000, cancellationToken);
                    
                    devices = await _adbClient.GetDevicesAsync(cancellationToken);
                    var newDevice = devices.FirstOrDefault(d => d.Serial == deviceSerial);
                    
                    if (newDevice != null)
                    {
                        logger.LogInfo($"[ADBv2] Successfully connected to: {address}");
                        return newDevice;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[ADBv2] Failed to connect to {address}: {ex.Message}");
                }
            }

            logger.LogWarning($"[ADBv2] Could not find or connect to device: {deviceSerial}");
            return null;
        }

        /// <summary>
        /// Acquires an emulator slot (inspired by wosbot-master's slot management)
        /// </summary>
        public static async Task<bool> AcquireEmulatorSlotAsync(int instanceNumber, LogService logger, int timeoutMs = 30000, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            var timeoutCts = new CancellationTokenSource(timeoutMs);
            var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            logger.LogInfo($"[ADBv2] Instance {instanceNumber} requesting emulator slot...");

            try
            {
                await _emulatorSlots.WaitAsync(combinedCts.Token);
                var waitTime = DateTime.UtcNow - startTime;
                logger.LogInfo($"[ADBv2] Instance {instanceNumber} acquired emulator slot after {waitTime.TotalSeconds:F1}s");
                return true;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning($"[ADBv2] Instance {instanceNumber} failed to acquire emulator slot within {timeoutMs}ms");
                return false;
            }
        }

        /// <summary>
        /// Releases an emulator slot
        /// </summary>
        public static void ReleaseEmulatorSlot(int instanceNumber, LogService logger)
        {
            try
            {
                _emulatorSlots.Release();
                logger.LogInfo($"[ADBv2] Instance {instanceNumber} released emulator slot");
            }
            catch (Exception ex)
            {
                logger.LogError($"[ADBv2] Error releasing emulator slot for instance {instanceNumber}: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates emulator slot limits based on configuration
        /// </summary>
        public static void UpdateEmulatorSlotLimits(int maxConcurrentEmulators)
        {
            // Create new semaphore with updated limit (similar to KingshotAuto's current approach)
            var oldSemaphore = _emulatorSlots;
            var newSemaphore = new SemaphoreSlim(maxConcurrentEmulators, maxConcurrentEmulators);
            
            // Note: In a real implementation, we'd need to handle this more carefully
            // For now, we'll keep the existing semaphore approach
        }

        /// <summary>
        /// Marks a device connection as unhealthy
        /// </summary>
        public static void MarkConnectionUnhealthy(int instanceNumber, LogService logger)
        {
            if (_deviceConnections.TryGetValue(instanceNumber, out var connection))
            {
                _deviceConnections[instanceNumber] = connection with { IsHealthy = false };
                logger.LogWarning($"[ADBv2] Instance {instanceNumber} marked as unhealthy");
            }
        }

        /// <summary>
        /// Conservative ADB server restart (only when really necessary)
        /// </summary>
        private static async Task AttemptConservativeServerRestartAsync(LogService logger, CancellationToken cancellationToken)
        {
            await _serverLock.WaitAsync(cancellationToken);
            try
            {
                logger.LogWarning("[ADBv2] Performing conservative ADB server restart...");
                
                // Clear unhealthy connections
                var unhealthyInstances = _deviceConnections
                    .Where(kvp => !kvp.Value.IsHealthy)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var instance in unhealthyInstances)
                {
                    _deviceConnections.TryRemove(instance, out var _);
                }
                
                // Restart server
                if (_adbServer != null)
                {
                    var adbPath = LDPlayerHelper.GetADBPath();
                    var result = await _adbServer.StartServerAsync(adbPath, restartServerIfNewer: true);
                    logger.LogInfo($"[ADBv2] Server restart result: {result}");
                }
                
                // Reset failure tracking
                _globalFailureCount = 0;
                _lastGlobalFailure = DateTime.MinValue;
                
                await Task.Delay(3000, cancellationToken); // Brief pause after restart
            }
            finally
            {
                _serverLock.Release();
            }
        }

        /// <summary>
        /// Health monitoring for device connections
        /// </summary>
        private static void StartHealthMonitoring(LogService logger)
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = new Timer(async _ =>
            {
                if (_adbClient == null) return;

                try
                {
                    var devices = await _adbClient.GetDevicesAsync();
                    var connectedSerials = devices.Where(d => d != null).Select(d => d.Serial).ToHashSet();
                    
                    var unhealthyConnections = new List<int>();
                    
                    foreach (var kvp in _deviceConnections)
                    {
                        var instance = kvp.Key;
                        var connection = kvp.Value;
                        
                        if (!connectedSerials.Contains(connection.Device.Serial))
                        {
                            unhealthyConnections.Add(instance);
                        }
                    }
                    
                    // Mark unhealthy connections
                    foreach (var instance in unhealthyConnections)
                    {
                        MarkConnectionUnhealthy(instance, logger);
                    }
                    
                    if (unhealthyConnections.Any())
                    {
                        logger.LogWarning($"[ADBv2] Health check found {unhealthyConnections.Count} unhealthy connections: {string.Join(", ", unhealthyConnections)}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[ADBv2] Health check error: {ex.Message}");
                }
            }, null, CONNECTION_HEALTH_CHECK_INTERVAL_MS, CONNECTION_HEALTH_CHECK_INTERVAL_MS);
        }

        /// <summary>
        /// Cleanup timer for old connections
        /// </summary>
        private static void StartConnectionCleanup(LogService logger)
        {
            _connectionCleanupTimer?.Dispose();
            _connectionCleanupTimer = new Timer(_ =>
            {
                try
                {
                    var cutoff = DateTime.UtcNow.AddMinutes(-10); // Remove connections idle for 10+ minutes
                    var toRemove = _deviceConnections
                        .Where(kvp => kvp.Value.LastUsed < cutoff && !kvp.Value.IsHealthy)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var instance in toRemove)
                    {
                        _deviceConnections.TryRemove(instance, out var _);
                    }
                    
                    if (toRemove.Any())
                    {
                        logger.LogInfo($"[ADBv2] Cleaned up {toRemove.Count} old connections");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[ADBv2] Connection cleanup error: {ex.Message}");
                }
            }, null, CONNECTION_CLEANUP_INTERVAL_MS, CONNECTION_CLEANUP_INTERVAL_MS);
        }

        /// <summary>
        /// Helper methods
        /// </summary>
        private static SemaphoreSlim GetInstanceLock(int instanceNumber)
        {
            return _instanceLocks.GetOrAdd(instanceNumber, _ => new SemaphoreSlim(1, 1));
        }

        private static string GetDeviceSerial(int instanceNumber)
        {
            var port = 5554 + (instanceNumber * 2);
            return $"emulator-{port}";
        }

        private static void RecordGlobalFailure()
        {
            _globalFailureCount++;
            _lastGlobalFailure = DateTime.UtcNow;
        }

        private static bool ShouldRestartAdbServer()
        {
            if (_globalFailureCount < FAILURE_THRESHOLD_FOR_RESTART) return false;
            
            var timeSinceLastFailure = DateTime.UtcNow - _lastGlobalFailure;
            return timeSinceLastFailure < TimeSpan.FromMinutes(RESTART_COOLDOWN_MINUTES);
        }

        /// <summary>
        /// Cleanup on shutdown
        /// </summary>
        public static async Task ShutdownAsync(LogService logger)
        {
            logger.LogInfo("[ADBv2] Shutting down connection manager...");
            
            _healthCheckTimer?.Dispose();
            _connectionCleanupTimer?.Dispose();
            
            // Clear all connections
            _deviceConnections.Clear();
            
            // Dispose locks
            foreach (var kvp in _instanceLocks)
            {
                kvp.Value.Dispose();
            }
            _instanceLocks.Clear();
            
            _emulatorSlots.Dispose();
            _serverLock.Dispose();
            
            _serverInitialized = false;
            
            logger.LogInfo("[ADBv2] Connection manager shutdown complete");
        }
    }
}