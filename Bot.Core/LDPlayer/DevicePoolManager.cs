using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Models;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Centralized device pool manager for ADB connections and device lifecycle management
    /// </summary>
    public class DevicePoolManager : IDisposable
    {
        private readonly LogService _logger;
        private readonly ConcurrentDictionary<string, DeviceState> _devicePool = new();
        private readonly ConcurrentDictionary<string, string> _accountDeviceMapping = new();
        private readonly Timer _healthCheckTimer;
        private readonly Timer _metricsTimer;
        private readonly SemaphoreSlim _poolLock = new(1, 1);
        private bool _disposed = false;
        
        // Configuration
        private const int HEALTH_CHECK_INTERVAL_MS = 30000; // 30 seconds
        private const int METRICS_INTERVAL_MS = 60000; // 1 minute
        private const int MAX_RECOVERY_ATTEMPTS = 3;
        private const double MIN_HEALTHY_SCORE = 70.0;
        
        public DevicePoolManager(LogService logger)
        {
            _logger = logger;
            
            // Start health monitoring
            _healthCheckTimer = new Timer(async _ => await PerformHealthCheckAsync(), null, 
                HEALTH_CHECK_INTERVAL_MS, HEALTH_CHECK_INTERVAL_MS);
                
            // Start metrics collection
            _metricsTimer = new Timer(async _ => await CollectMetricsAsync(), null, 
                METRICS_INTERVAL_MS, METRICS_INTERVAL_MS);
                
            _logger.LogInfo("[DevicePool] Device pool manager initialized with health monitoring");
        }
        
        /// <summary>
        /// Registers a device in the pool
        /// </summary>
        public async Task<DeviceState> RegisterDeviceAsync(int instanceNumber, string deviceSerial)
        {
            if (!await _poolLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogError("Device pool lock timeout after 30s");
                throw new TimeoutException("Device pool lock timeout");
            }
            try
            {
                if (_devicePool.TryGetValue(deviceSerial, out var existingDevice))
                {
                    _logger.LogInfo($"[DevicePool] Device {deviceSerial} already registered, updating state");
                    existingDevice.LastHealthCheck = DateTime.UtcNow;
                    return existingDevice;
                }
                
                var deviceState = new DeviceState
                {
                    DeviceSerial = deviceSerial,
                    InstanceNumber = instanceNumber,
                    Status = DeviceStatus.Unknown,
                    FirstDiscovered = DateTime.UtcNow,
                    LastHealthCheck = DateTime.UtcNow
                };
                
                _devicePool[deviceSerial] = deviceState;
                _logger.LogInfo($"[DevicePool] Registered new device: {deviceState}");
                
                return deviceState;
            }
            finally
            {
                _poolLock.Release();
            }
        }
        
        /// <summary>
        /// Assigns a device to an account
        /// </summary>
        public async Task<DeviceState?> AssignDeviceToAccountAsync(string accountName, int? preferredInstanceNumber = null)
        {
            if (!await _poolLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogError("Device pool lock timeout after 30s");
                throw new TimeoutException("Device pool lock timeout");
            }
            try
            {
                // Check if account already has a device assigned
                if (_accountDeviceMapping.TryGetValue(accountName, out var existingDeviceSerial))
                {
                    if (_devicePool.TryGetValue(existingDeviceSerial, out var existingDevice) && existingDevice.IsAvailable)
                    {
                        existingDevice.Status = DeviceStatus.Busy;
                        existingDevice.AssignedAccountName = accountName;
                        _logger.LogInfo($"[DevicePool] Reusing existing device assignment: {accountName} -> {existingDeviceSerial}");
                        return existingDevice;
                    }
                    else
                    {
                        // Remove stale mapping
                        _accountDeviceMapping.TryRemove(accountName, out _);
                    }
                }
                
                // Find available device, preferring the specified instance number
                var availableDevices = _devicePool.Values
                    .Where(d => d.IsAvailable)
                    .OrderByDescending(d => d.HealthScore)
                    .ThenBy(d => d.AverageResponseTime)
                    .ToList();
                
                DeviceState? selectedDevice = null;
                
                if (preferredInstanceNumber.HasValue)
                {
                    selectedDevice = availableDevices.FirstOrDefault(d => d.InstanceNumber == preferredInstanceNumber.Value);
                }
                
                selectedDevice ??= availableDevices.FirstOrDefault();
                
                if (selectedDevice == null)
                {
                    _logger.LogWarning($"[DevicePool] No available devices for account {accountName}");
                    return null;
                }
                
                // Assign device to account
                selectedDevice.Status = DeviceStatus.Busy;
                selectedDevice.AssignedAccountName = accountName;
                _accountDeviceMapping[accountName] = selectedDevice.DeviceSerial;
                
                _logger.LogInfo($"[DevicePool] Assigned device to account: {accountName} -> {selectedDevice.DeviceSerial} (Health: {selectedDevice.HealthScore:F1}%)");
                
                return selectedDevice;
            }
            finally
            {
                _poolLock.Release();
            }
        }
        
        /// <summary>
        /// Releases a device from account assignment
        /// </summary>
        public async Task ReleaseDeviceAsync(string accountName)
        {
            if (!await _poolLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                _logger.LogError("Device pool lock timeout after 30s");
                throw new TimeoutException("Device pool lock timeout");
            }
            try
            {
                if (_accountDeviceMapping.TryRemove(accountName, out var deviceSerial))
                {
                    if (_devicePool.TryGetValue(deviceSerial, out var device))
                    {
                        device.Status = DeviceStatus.Available;
                        device.AssignedAccountName = null;
                        _logger.LogInfo($"[DevicePool] Released device from account: {accountName} -> {deviceSerial}");
                    }
                }
            }
            finally
            {
                _poolLock.Release();
            }
        }
        
        /// <summary>
        /// Gets the current device assigned to an account
        /// </summary>
        public DeviceState? GetDeviceForAccount(string accountName)
        {
            if (_accountDeviceMapping.TryGetValue(accountName, out var deviceSerial))
            {
                _devicePool.TryGetValue(deviceSerial, out var device);
                return device;
            }
            return null;
        }
        
        /// <summary>
        /// Gets device state by serial
        /// </summary>
        public DeviceState? GetDevice(string deviceSerial)
        {
            _devicePool.TryGetValue(deviceSerial, out var device);
            return device;
        }
        
        /// <summary>
        /// Gets all devices in the pool
        /// </summary>
        public IEnumerable<DeviceState> GetAllDevices()
        {
            return _devicePool.Values.ToList();
        }
        
        /// <summary>
        /// Gets devices by status
        /// </summary>
        public IEnumerable<DeviceState> GetDevicesByStatus(DeviceStatus status)
        {
            return _devicePool.Values.Where(d => d.Status == status).ToList();
        }
        
        /// <summary>
        /// Records successful command execution on a device
        /// </summary>
        public void RecordDeviceSuccess(string deviceSerial, double responseTime)
        {
            if (_devicePool.TryGetValue(deviceSerial, out var device))
            {
                device.RecordSuccess(responseTime);
            }
        }
        
        /// <summary>
        /// Records failed command execution on a device
        /// </summary>
        public void RecordDeviceFailure(string deviceSerial, string errorMessage)
        {
            if (_devicePool.TryGetValue(deviceSerial, out var device))
            {
                device.RecordFailure(errorMessage);
                
                // If device has too many failures, mark it for recovery
                if (device.ConsecutiveFailures >= 3)
                {
                    device.Status = DeviceStatus.Error;
                    _logger.LogWarning($"[DevicePool] Device {deviceSerial} marked for recovery due to consecutive failures");
                }
            }
        }
        
        /// <summary>
        /// Starts recovery process for a device
        /// </summary>
        public async Task<bool> RecoverDeviceAsync(string deviceSerial)
        {
            if (!_devicePool.TryGetValue(deviceSerial, out var device))
            {
                return false;
            }
            
            if (device.IsRecovering)
            {
                _logger.LogInfo($"[DevicePool] Device {deviceSerial} already in recovery");
                return false;
            }
            
            if (device.RecoveryAttempts >= MAX_RECOVERY_ATTEMPTS)
            {
                _logger.LogError($"[DevicePool] Device {deviceSerial} exceeded maximum recovery attempts");
                return false;
            }
            
            device.IsRecovering = true;
            device.Status = DeviceStatus.Recovering;
            device.RecoveryAttempts++;
            
            _logger.LogInfo($"[DevicePool] Starting recovery for device {deviceSerial} (attempt {device.RecoveryAttempts}/{MAX_RECOVERY_ATTEMPTS})");
            
            try
            {
                // Perform device-specific recovery
                var recoveryService = new DeviceRecoveryService(_logger);
                var success = await recoveryService.RecoverDeviceAsync(device);
                
                if (success)
                {
                    device.Status = DeviceStatus.Available;
                    device.ResetFailures();
                    _logger.LogInfo($"[DevicePool] Device {deviceSerial} successfully recovered");
                }
                else
                {
                    device.Status = DeviceStatus.Error;
                    _logger.LogError($"[DevicePool] Failed to recover device {deviceSerial}");
                }
                
                return success;
            }
            catch (Exception ex)
            {
                device.Status = DeviceStatus.Error;
                _logger.LogError($"[DevicePool] Exception during device recovery for {deviceSerial}: {ex.Message}");
                return false;
            }
            finally
            {
                device.IsRecovering = false;
            }
        }
        
        /// <summary>
        /// Performs health check on a single device using fast detailed status check
        /// </summary>
        private async Task CheckDeviceHealthAsync(DeviceState device)
        {
            try
            {
                // First get quick status from list2 via a temporary InstanceManager
                // This is a temporary approach - in a full implementation, we'd inject InstanceManager
                var ldConsolePath = Bot.Core.Utils.LDPlayerHelper.GetLDPlayerConsolePath();
                var dnConsolePath = Bot.Core.Utils.LDPlayerHelper.GetDNPlayerConsolePath();
                var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, _logger);
                
                var detailedStatus = await instanceManager.GetInstanceDetailedStatusAsync(device.InstanceNumber);
                
                if (detailedStatus == null || !detailedStatus.IsFullyBooted)
                {
                    var reason = detailedStatus == null 
                        ? "Instance not found in list2 output"
                        : $"Instance not fully booted: {detailedStatus.StatusDescription}";
                    
                    device.Status = DeviceStatus.Offline;
                    device.RecordFailure(reason);
                    device.LastHealthCheck = DateTime.UtcNow;
                    
                    _logger.LogWarning($"[DevicePool] Device {device.DeviceSerial} health check failed: {reason}");
                    return;
                }
                
                // Update device metadata with fresh information
                device.ProcessId = detailedStatus.ProcessId;
                device.WindowHandle = detailedStatus.BindWindowHandle;
                
                // Only if instance is fully booted, check ADB responsiveness
                var controller = await ADBConnectionManager.GetConnectionAsync(device.InstanceNumber, _logger);
                if (controller != null)
                {
                    var startTime = DateTime.UtcNow;
                    var isResponsive = await controller.IsConnectedAndResponsive();
                    var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    
                    if (isResponsive)
                    {
                        device.RecordSuccess(responseTime);
                        device.Status = device.Status == DeviceStatus.Unknown ? DeviceStatus.Available : device.Status;
                        
                        _logger.LogInfo($"[DevicePool] Device {device.DeviceSerial} healthy: PID={device.ProcessId}, " +
                                       $"Handle={device.WindowHandle}, Response={responseTime:F0}ms");
                    }
                    else
                    {
                        device.RecordFailure("ADB not responsive despite instance being fully booted");
                        device.Status = DeviceStatus.Error;
                        
                        _logger.LogWarning($"[DevicePool] Device {device.DeviceSerial} ADB not responsive despite being fully booted");
                    }
                }
                else
                {
                    device.RecordFailure("Could not get ADB connection despite instance being fully booted");
                    device.Status = DeviceStatus.Error;
                    
                    _logger.LogWarning($"[DevicePool] Device {device.DeviceSerial} ADB connection failed despite being fully booted");
                }
                
                device.LastHealthCheck = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                device.RecordFailure($"Health check exception: {ex.Message}");
                device.Status = DeviceStatus.Error;
                device.LastHealthCheck = DateTime.UtcNow;
                
                _logger.LogError($"[DevicePool] Exception during health check for {device.DeviceSerial}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Performs health check on all devices
        /// </summary>
        private async Task PerformHealthCheckAsync()
        {
            if (_disposed) return;
            
            var devices = _devicePool.Values.ToList();
            var healthyDevices = 0;
            var unhealthyDevices = 0;
            
            foreach (var device in devices)
            {
                // Skip devices that are currently being recovered
                if (device.IsRecovering) continue;
                
                // Skip health check if device was recently checked
                if (DateTime.UtcNow - device.LastHealthCheck < TimeSpan.FromSeconds(HEALTH_CHECK_INTERVAL_MS / 1000 / 2))
                {
                    continue;
                }
                
                await CheckDeviceHealthAsync(device);
                
                if (device.HealthScore >= MIN_HEALTHY_SCORE)
                {
                    healthyDevices++;
                }
                else
                {
                    unhealthyDevices++;
                    
                    // Auto-recover devices with poor health
                    if (device.NeedsAttention && device.RecoveryAttempts < MAX_RECOVERY_ATTEMPTS)
                    {
                        _ = Task.Run(async () => await RecoverDeviceAsync(device.DeviceSerial));
                    }
                }
            }
            
            if (devices.Count > 0)
            {
                _logger.LogInfo($"[DevicePool] Health check completed: {healthyDevices} healthy, {unhealthyDevices} unhealthy out of {devices.Count} devices");
            }
        }
        
        /// <summary>
        /// Collects and logs device metrics
        /// </summary>
        private async Task CollectMetricsAsync()
        {
            if (_disposed) return;
            
            var devices = _devicePool.Values.ToList();
            if (devices.Count == 0) return;
            
            var totalCommands = devices.Sum(d => d.TotalCommands);
            var totalSuccessful = devices.Sum(d => d.SuccessfulCommands);
            var avgHealthScore = devices.Any() ? devices.Average(d => d.HealthScore) : 0.0;
            var devicesWithResponseTime = devices.Where(d => d.AverageResponseTime > 0).ToList();
            var avgResponseTime = devicesWithResponseTime.Any() ? devicesWithResponseTime.Average(d => d.AverageResponseTime) : 0.0;
            
            var statusCounts = devices.GroupBy(d => d.Status).ToDictionary(g => g.Key, g => g.Count());
            
            _logger.LogInfo($"[DevicePool] Metrics - Devices: {devices.Count}, Commands: {totalCommands}, Success Rate: {(totalCommands > 0 ? (double)totalSuccessful / totalCommands * 100 : 0):F1}%, Avg Health: {avgHealthScore:F1}%, Avg Response: {avgResponseTime:F0}ms");
            
            foreach (var status in statusCounts)
            {
                _logger.LogInfo($"[DevicePool] Status {status.Key}: {status.Value} devices");
            }
            
            // Log devices that need attention
            var problemDevices = devices.Where(d => d.NeedsAttention).ToList();
            if (problemDevices.Any())
            {
                _logger.LogWarning($"[DevicePool] {problemDevices.Count} devices need attention:");
                foreach (var device in problemDevices)
                {
                    _logger.LogWarning($"  - {device}");
                }
            }
        }
        
        /// <summary>
        /// Pre-warms devices for faster startup by establishing connections in advance
        /// </summary>
        public async Task PrewarmDevicesAsync(IEnumerable<int> instanceNumbers)
        {
            _logger.LogInfo($"[DevicePool] Starting device pre-warming for instances: {string.Join(", ", instanceNumbers)}");
            
            var prewarmTasks = instanceNumbers.Select(async instanceNumber =>
            {
                try
                {
                    var deviceSerial = $"emulator-{5554 + instanceNumber * 2}";
                    var device = await RegisterDeviceAsync(instanceNumber, deviceSerial);
                    
                    // Attempt to establish ADB connection
                    var controller = await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger);
                    if (controller != null)
                    {
                        // Perform a simple connectivity test
                        var startTime = DateTime.UtcNow;
                        var isResponsive = await controller.IsConnectedAndResponsive();
                        var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                        
                        if (isResponsive)
                        {
                            device.RecordSuccess(responseTime);
                            device.Status = DeviceStatus.Available;
                            _logger.LogInfo($"[DevicePool] Device {deviceSerial} pre-warmed successfully ({responseTime:F0}ms)");
                        }
                        else
                        {
                            device.RecordFailure("Pre-warm failed - device not responsive");
                            _logger.LogWarning($"[DevicePool] Device {deviceSerial} pre-warm failed - not responsive");
                        }
                    }
                    else
                    {
                        device.RecordFailure("Pre-warm failed - could not get ADB connection");
                        _logger.LogWarning($"[DevicePool] Device {deviceSerial} pre-warm failed - no ADB connection");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[DevicePool] Error pre-warming instance {instanceNumber}: {ex.Message}");
                }
            });
            
            await Task.WhenAll(prewarmTasks);
            _logger.LogInfo($"[DevicePool] Device pre-warming completed for {instanceNumbers.Count()} instances");
        }
        
        /// <summary>
        /// Gets pool statistics
        /// </summary>
        public Dictionary<string, object> GetPoolStatistics()
        {
            var devices = _devicePool.Values.ToList();
            var stats = new Dictionary<string, object>
            {
                ["TotalDevices"] = devices.Count,
                ["AvailableDevices"] = devices.Count(d => d.IsAvailable),
                ["BusyDevices"] = devices.Count(d => d.Status == DeviceStatus.Busy),
                ["OfflineDevices"] = devices.Count(d => d.Status == DeviceStatus.Offline),
                ["ErrorDevices"] = devices.Count(d => d.Status == DeviceStatus.Error),
                ["RecoveringDevices"] = devices.Count(d => d.IsRecovering),
                ["AverageHealthScore"] = devices.Any() ? devices.Average(d => d.HealthScore) : 0.0,
                ["TotalCommands"] = devices.Sum(d => d.TotalCommands),
                ["SuccessfulCommands"] = devices.Sum(d => d.SuccessfulCommands),
                ["OverallSuccessRate"] = devices.Sum(d => d.TotalCommands) > 0 ? 
                    (double)devices.Sum(d => d.SuccessfulCommands) / devices.Sum(d => d.TotalCommands) * 100.0 : 0.0
            };
            
            return stats;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _healthCheckTimer?.Dispose();
            _metricsTimer?.Dispose();
            _poolLock?.Dispose();
            
            _logger.LogInfo("[DevicePool] Device pool manager disposed");
        }
    }
}