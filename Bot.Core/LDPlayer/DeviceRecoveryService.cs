using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Utils;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Handles individual device recovery without affecting other devices
    /// </summary>
    public class DeviceRecoveryService
    {
        private readonly LogService _logger;
        private readonly SemaphoreSlim _recoveryLock = new(1, 1);
        
        public DeviceRecoveryService(LogService logger)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// Performs targeted recovery for a specific device
        /// </summary>
        public async Task<bool> RecoverDeviceAsync(DeviceState device, CancellationToken cancellationToken = default)
        {
            await _recoveryLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInfo($"[Recovery] Starting recovery for device {device.DeviceSerial} (Instance {device.InstanceNumber})");
                
                // Step 1: Try simple reconnection first
                if (await TrySimpleReconnectionAsync(device, cancellationToken))
                {
                    _logger.LogInfo($"[Recovery] Simple reconnection successful for {device.DeviceSerial}");
                    return true;
                }
                
                // Step 2: Try ADB reconnect command
                if (await TryADBReconnectAsync(device, cancellationToken))
                {
                    _logger.LogInfo($"[Recovery] ADB reconnect successful for {device.DeviceSerial}");
                    return true;
                }
                
                // Step 3: Try restarting the specific emulator instance
                if (await TryEmulatorRestartAsync(device, cancellationToken))
                {
                    _logger.LogInfo($"[Recovery] Emulator restart successful for {device.DeviceSerial}");
                    return true;
                }
                
                // Step 4: Last resort - targeted ADB server restart (only if no other devices are active)
                if (await TryTargetedADBRestartAsync(device, cancellationToken))
                {
                    _logger.LogInfo($"[Recovery] Targeted ADB restart successful for {device.DeviceSerial}");
                    return true;
                }
                
                _logger.LogError($"[Recovery] All recovery attempts failed for {device.DeviceSerial}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recovery] Exception during recovery for {device.DeviceSerial}: {ex.Message}");
                return false;
            }
            finally
            {
                _recoveryLock.Release();
            }
        }
        
        /// <summary>
        /// Tries a simple reconnection by testing the existing connection
        /// </summary>
        private async Task<bool> TrySimpleReconnectionAsync(DeviceState device, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInfo($"[Recovery] Trying simple reconnection for {device.DeviceSerial}");
                
                var controller = await ADBConnectionManager.GetConnectionAsync(device.InstanceNumber, _logger, cancellationToken);
                if (controller != null)
                {
                    var startTime = DateTime.UtcNow;
                    var isResponsive = await controller.IsConnectedAndResponsive(cancellationToken);
                    var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    
                    if (isResponsive)
                    {
                        device.RecordSuccess(responseTime);
                        return true;
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recovery] Simple reconnection failed for {device.DeviceSerial}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Tries ADB reconnect command for the specific device
        /// </summary>
        private async Task<bool> TryADBReconnectAsync(DeviceState device, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInfo($"[Recovery] Trying ADB reconnect for {device.DeviceSerial}");
                
                var adbPath = LDPlayerHelper.GetADBPath();
                if (string.IsNullOrEmpty(adbPath))
                {
                    _logger.LogError("[Recovery] ADB path not found");
                    return false;
                }
                
                // Try to reconnect to this specific device
                var reconnectProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"connect {device.DeviceSerial}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                
                if (reconnectProcess != null)
                {
                    using var cts = new CancellationTokenSource(10000); // 10 second timeout
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                    
                    string output = await reconnectProcess.StandardOutput.ReadToEndAsync();
                    await reconnectProcess.WaitForExitAsync(linkedCts.Token);
                    
                    _logger.LogInfo($"[Recovery] ADB reconnect output for {device.DeviceSerial}: {output.Trim()}");
                    
                    // Wait a moment for connection to stabilize
                    await Task.Delay(2000, cancellationToken);
                    
                    // Test if reconnection worked
                    return await TrySimpleReconnectionAsync(device, cancellationToken);
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recovery] ADB reconnect failed for {device.DeviceSerial}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Tries restarting the specific emulator instance
        /// </summary>
        private async Task<bool> TryEmulatorRestartAsync(DeviceState device, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInfo($"[Recovery] Trying emulator restart for instance {device.InstanceNumber}");
                
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                
                if (string.IsNullOrEmpty(ldConsolePath) || string.IsNullOrEmpty(dnConsolePath))
                {
                    _logger.LogError("[Recovery] LDPlayer paths not found");
                    return false;
                }
                
                var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, _logger);
                
                // Stop the instance
                _logger.LogInfo($"[Recovery] Stopping emulator instance {device.InstanceNumber}");
                await instanceManager.StopInstanceAsync(device.InstanceNumber, cancellationToken);
                
                // Wait a moment
                await Task.Delay(3000, cancellationToken);
                
                // Start the instance
                _logger.LogInfo($"[Recovery] Starting emulator instance {device.InstanceNumber}");
                var startResult = await instanceManager.StartInstanceAsync(device.InstanceNumber, cancellationToken);
                
                if (startResult)
                {
                    // Wait for emulator to boot up and enable ADB
                    await Task.Delay(10000, cancellationToken);
                    
                    // Test if restart worked
                    return await TrySimpleReconnectionAsync(device, cancellationToken);
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recovery] Emulator restart failed for instance {device.InstanceNumber}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Tries targeted ADB server restart only if no other devices are active
        /// </summary>
        private async Task<bool> TryTargetedADBRestartAsync(DeviceState device, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInfo($"[Recovery] Checking if targeted ADB restart is safe for {device.DeviceSerial}");
                
                var adbPath = LDPlayerHelper.GetADBPath();
                if (string.IsNullOrEmpty(adbPath))
                {
                    _logger.LogError("[Recovery] ADB path not found");
                    return false;
                }
                
                // Get list of all connected devices
                var devicesProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "devices",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                
                if (devicesProcess != null)
                {
                    using var cts = new CancellationTokenSource(5000); // 5 second timeout
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                    
                    string output = await devicesProcess.StandardOutput.ReadToEndAsync();
                    await devicesProcess.WaitForExitAsync(linkedCts.Token);
                    
                    // Check if there are other active devices
                    var lines = output.Split('\n');
                    var activeDevices = 0;
                    
                    foreach (var line in lines)
                    {
                        if (line.Contains("\tdevice") && !line.Contains(device.DeviceSerial))
                        {
                            activeDevices++;
                        }
                    }
                    
                    if (activeDevices > 0)
                    {
                        _logger.LogWarning($"[Recovery] Skipping ADB restart for {device.DeviceSerial} - {activeDevices} other devices are active");
                        return false;
                    }
                    
                    _logger.LogInfo($"[Recovery] No other devices active, proceeding with ADB restart for {device.DeviceSerial}");
                    
                    // Safe to restart ADB server
                    await ADBConnectionManager.AttemptServerRecoveryAsync(device.InstanceNumber, _logger, cancellationToken);
                    
                    // Wait for server to restart
                    await Task.Delay(5000, cancellationToken);
                    
                    // Test if restart worked
                    return await TrySimpleReconnectionAsync(device, cancellationToken);
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recovery] Targeted ADB restart failed for {device.DeviceSerial}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Performs comprehensive device diagnostics
        /// </summary>
        public async Task<Dictionary<string, object>> DiagnoseDeviceAsync(DeviceState device, CancellationToken cancellationToken = default)
        {
            var diagnostics = new Dictionary<string, object>();
            
            try
            {
                _logger.LogInfo($"[Recovery] Running diagnostics for {device.DeviceSerial}");
                
                // Test ADB connection
                var adbPath = LDPlayerHelper.GetADBPath();
                diagnostics["ADBPathFound"] = !string.IsNullOrEmpty(adbPath);
                
                if (!string.IsNullOrEmpty(adbPath))
                {
                    // Test device listing
                    var devicesProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = "devices",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    
                    if (devicesProcess != null)
                    {
                        using var cts = new CancellationTokenSource(5000);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                        
                        string output = await devicesProcess.StandardOutput.ReadToEndAsync();
                        await devicesProcess.WaitForExitAsync(linkedCts.Token);
                        
                        diagnostics["DeviceListOutput"] = output;
                        diagnostics["DeviceInList"] = output.Contains(device.DeviceSerial);
                        diagnostics["DeviceOnline"] = output.Contains($"{device.DeviceSerial}\tdevice");
                        diagnostics["DeviceOffline"] = output.Contains($"{device.DeviceSerial}\toffline");
                        diagnostics["DeviceUnauthorized"] = output.Contains($"{device.DeviceSerial}\tunauthorized");
                    }
                }
                
                // Test emulator status
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                diagnostics["DNConsolePathFound"] = !string.IsNullOrEmpty(dnConsolePath);
                
                if (!string.IsNullOrEmpty(dnConsolePath))
                {
                    var instanceProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = dnConsolePath,
                        Arguments = "list2",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    
                    if (instanceProcess != null)
                    {
                        using var cts = new CancellationTokenSource(5000);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                        
                        string output = await instanceProcess.StandardOutput.ReadToEndAsync();
                        await instanceProcess.WaitForExitAsync(linkedCts.Token);
                        
                        diagnostics["InstanceListOutput"] = output;
                        diagnostics["InstanceRunning"] = output.Contains($"{device.InstanceNumber},");
                    }
                }
                
                // Add device state information
                diagnostics["DeviceState"] = device.Status.ToString();
                diagnostics["HealthScore"] = device.HealthScore;
                diagnostics["ConsecutiveFailures"] = device.ConsecutiveFailures;
                diagnostics["AverageResponseTime"] = device.AverageResponseTime;
                diagnostics["LastSuccessfulCommand"] = device.LastSuccessfulCommand;
                diagnostics["LastError"] = device.LastError;
                diagnostics["LastErrorMessage"] = device.LastErrorMessage ?? string.Empty;
                
                _logger.LogInfo($"[Recovery] Diagnostics completed for {device.DeviceSerial}");
                
                return diagnostics;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Recovery] Diagnostics failed for {device.DeviceSerial}: {ex.Message}");
                diagnostics["DiagnosticsError"] = ex.Message;
                return diagnostics;
            }
        }
    }
}