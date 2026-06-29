using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Utils;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Service to automatically detect and fix common ADB connection issues
    /// </summary>
    public class ADBRecoveryService
    {
        private readonly LogService _logger;
        private readonly string _adbPath;
        private readonly string _ldConsolePath;
        
        public ADBRecoveryService(LogService logger)
        {
            _logger = logger;
            _adbPath = LDPlayerHelper.GetADBPath();
            _ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
        }
        
        /// <summary>
        /// Attempts multiple recovery strategies to fix ADB connection issues
        /// </summary>
        public async Task<bool> AttemptFullRecoveryAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"[ADBRecovery] Starting comprehensive recovery for instance {instanceNumber}", category: LogCategories.ADB);
            
            // Strategy 1: Kill all adb processes and restart
            if (await RestartADBServerAsync(cancellationToken))
            {
                _logger.LogInfo("[ADBRecovery] ADB server restarted successfully", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // Strategy 2: Restart the LDPlayer instance
            if (await RestartInstanceAsync(instanceNumber, cancellationToken))
            {
                _logger.LogInfo($"[ADBRecovery] Instance {instanceNumber} restarted", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // Strategy 3: Fix port conflicts
            if (await FixPortConflictsAsync(instanceNumber, cancellationToken))
            {
                _logger.LogInfo("[ADBRecovery] Port conflicts resolved", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // Strategy 4: Reset ADB keys
            if (await ResetADBKeysAsync(cancellationToken))
            {
                _logger.LogInfo("[ADBRecovery] ADB keys reset", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // Strategy 5: Enable ADB in LDPlayer settings
            if (await EnableADBInSettingsAsync(instanceNumber, cancellationToken))
            {
                _logger.LogInfo("[ADBRecovery] ADB enabled in LDPlayer settings", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            _logger.LogError($"[ADBRecovery] All recovery strategies failed for instance {instanceNumber}", category: LogCategories.ADB);
            return false;
        }
        
        private async Task<bool> RestartADBServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Kill all adb.exe processes
                foreach (var adbProcess in Process.GetProcessesByName("adb"))
                {
                    try
                    {
                        adbProcess.Kill();
                        await adbProcess.WaitForExitAsync(cancellationToken);
                    }
                    catch { }
                }
                
                await Task.Delay(1000, cancellationToken);
                
                // Start ADB server
                var startInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to restart ADB server: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> RestartInstanceAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Quit the instance
                var quitInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"quit --index {instanceNumber}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var quitProcess = Process.Start(quitInfo))
                {
                    if (quitProcess != null)
                    {
                        await quitProcess.WaitForExitAsync(cancellationToken);
                    }
                }
                
                await Task.Delay(3000, cancellationToken);
                
                // Launch the instance again
                var launchInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"launch --index {instanceNumber}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var launchProcess = Process.Start(launchInfo))
                {
                    if (launchProcess != null)
                    {
                        await launchProcess.WaitForExitAsync(cancellationToken);
                    }
                }
                
                // Wait for boot
                await Task.Delay(15000, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to restart instance: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> FixPortConflictsAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                var port = 5554 + (instanceNumber * 2);
                
                // Kill processes using the port
                var netstatInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netstat -ano | findstr :{port}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(netstatInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync(cancellationToken);
                    
                    // Parse PIDs and kill conflicting processes
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 4)
                        {
                            if (int.TryParse(parts[^1], out int pid))
                            {
                                try
                                {
                                    var conflictingProcess = Process.GetProcessById(pid);
                                    if (!conflictingProcess.ProcessName.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase))
                                    {
                                        conflictingProcess.Kill();
                                        _logger.LogInfo($"[ADBRecovery] Killed process {conflictingProcess.ProcessName} using port {port}", category: LogCategories.ADB);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to fix port conflicts: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> ResetADBKeysAsync(CancellationToken cancellationToken)
        {
            try
            {
                var adbKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android");
                if (Directory.Exists(adbKeyPath))
                {
                    var keyFiles = new[] { "adbkey", "adbkey.pub" };
                    foreach (var keyFile in keyFiles)
                    {
                        var fullPath = Path.Combine(adbKeyPath, keyFile);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            _logger.LogInfo($"[ADBRecovery] Deleted {keyFile}", category: LogCategories.ADB);
                        }
                    }
                }
                
                // Restart ADB to regenerate keys
                return await RestartADBServerAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to reset ADB keys: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> EnableADBInSettingsAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Use ldconsole to enable ADB debugging
                var enableInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"globalsetting --index {instanceNumber} --enable_adb 1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(enableInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    
                    // Restart instance for settings to take effect
                    return await RestartInstanceAsync(instanceNumber, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to enable ADB in settings: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> TestConnectionAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                var deviceSerial = $"emulator-{5554 + (instanceNumber * 2)}";
                
                // Test ADB connection
                var testInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = $"-s {deviceSerial} shell echo test",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(testInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(cancellationToken);
                    
                    return process.ExitCode == 0 && output.Contains("test") && !error.Contains("error");
                }
            }
            catch { }
            
            return false;
        }
    }
}