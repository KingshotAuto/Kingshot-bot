using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using Bot.Core.Logging;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Collections.Concurrent;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Enhanced ADB controller with persistent connections and performance optimizations.
    /// Inspired by wosbot-master's ddmlib approach with C# AdvancedSharpAdbClient.
    /// </summary>
    public class ADBControllerV2 : IDisposable
    {
        private readonly DeviceData _device;
        private readonly AdbClient _adbClient;
        private readonly LogService _logger;
        private readonly int _instanceNumber;
        private bool _disposed = false;
        
        // Performance optimization: Reusable screenshot buffer (similar to wosbot-master's ThreadLocal<BufferedImage>)
        private static readonly ThreadLocal<MemoryStream> _reusableScreenshotBuffer = new(() => new MemoryStream());
        
        // Command batching for performance
        private readonly List<string> _batchedCommands = new();
        private readonly SemaphoreSlim _commandSemaphore = new(1, 1);
        
        // Connection health tracking
        private DateTime _lastSuccessfulOperation = DateTime.UtcNow;
        private int _consecutiveFailures = 0;
        private const int MAX_CONSECUTIVE_FAILURES = 3;
        
        // Performance metrics
        private static readonly ConcurrentDictionary<string, (long TotalTime, int Count)> _performanceMetrics = new();

        public ADBControllerV2(DeviceData device, AdbClient adbClient, LogService logger, int instanceNumber)
        {
            _device = device;
            _adbClient = adbClient ?? throw new ArgumentNullException(nameof(adbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _instanceNumber = instanceNumber;
            
            _logger.LogInfo($"[ADBv2] Controller created for instance {instanceNumber}: {device.Serial}");
        }

        /// <summary>
        /// Enhanced screenshot capture with buffer reuse (inspired by wosbot-master's approach)
        /// </summary>
        public async Task<byte[]> TakeScreenshotAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!await IsConnectedAndResponsiveAsync(cancellationToken))
                {
                    throw new InvalidOperationException($"Device {_device.Serial} is not responsive");
                }

                // Take screenshot using temporary file approach (more reliable)
                var tempPath = $"/sdcard/temp_screenshot_{_instanceNumber}.png";
                
                // Execute screencap command to save to file
                var receiver = new ConsoleOutputReceiver();
                await _adbClient.ExecuteRemoteCommandAsync($"screencap -p {tempPath}", _device, receiver, cancellationToken);
                
                // Use reusable buffer to pull the screenshot file
                var buffer = _reusableScreenshotBuffer.Value;
                buffer!.SetLength(0);
                buffer.Seek(0, SeekOrigin.Begin);
                
                // Pull the file using SyncService
                using (var syncService = new SyncService(_device))
                {
                    syncService.Pull(tempPath, buffer, null);
                }
                
                // Clean up temp file
                await _adbClient.ExecuteRemoteCommandAsync($"rm {tempPath}", _device, new ConsoleOutputReceiver(), cancellationToken);
                
                // Convert to byte array
                var screenshotBytes = buffer.ToArray();
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("TakeScreenshot", DateTime.UtcNow - startTime);

                // Performance: removed Image.FromStream validation - just log byte count
                _logger.LogInfo($"[ADBv2] Screenshot captured for instance {_instanceNumber} ({screenshotBytes.Length} bytes)");
                return screenshotBytes;
            }
            catch (Exception ex)
            {
                RecordFailedOperation();
                _logger.LogError($"[ADBv2] Screenshot failed for instance {_instanceNumber}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Enhanced tap functionality with random coordinates (similar to wosbot-master's tapAtRandomPoint)
        /// </summary>
        public async Task<bool> TapAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            return await TapRandomInRectAsync(x, y, x, y, cancellationToken);
        }

        /// <summary>
        /// Random tap within rectangle area (directly inspired by wosbot-master)
        /// </summary>
        public async Task<bool> TapRandomInRectAsync(int x1, int y1, int x2, int y2, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!await IsConnectedAndResponsiveAsync(cancellationToken))
                {
                    return false;
                }

                var random = new Random();
                var minX = Math.Min(x1, x2);
                var maxX = Math.Max(x1, x2);
                var minY = Math.Min(y1, y2);
                var maxY = Math.Max(y1, y2);
                
                var randomX = minX + random.Next(maxX - minX + 1);
                var randomY = minY + random.Next(maxY - minY + 1);
                
                var command = $"input tap {randomX} {randomY}";
                await ExecuteShellCommandAsync(command, cancellationToken);
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("TapRandom", DateTime.UtcNow - startTime);
                
                _logger.LogInfo($"[ADBv2] Tap sent to ({randomX},{randomY}) on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                RecordFailedOperation();
                _logger.LogError($"[ADBv2] Tap failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Multiple taps with delay (inspired by wosbot-master's tapCount functionality)
        /// </summary>
        public async Task<bool> TapRandomInRectMultipleAsync(int x1, int y1, int x2, int y2, int tapCount, int delayMs, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!await IsConnectedAndResponsiveAsync(cancellationToken))
                {
                    return false;
                }

                var random = new Random();
                var minX = Math.Min(x1, x2);
                var maxX = Math.Max(x1, x2);
                var minY = Math.Min(y1, y2);
                var maxY = Math.Max(y1, y2);
                
                for (int i = 1; i <= tapCount; i++)
                {
                    var randomX = minX + random.Next(maxX - minX + 1);
                    var randomY = minY + random.Next(maxY - minY + 1);
                    
                    var command = $"input tap {randomX} {randomY}";
                    await ExecuteShellCommandAsync(command, cancellationToken);
                    
                    _logger.LogInfo($"[ADBv2] Tap {i}/{tapCount} sent to ({randomX},{randomY}) on instance {_instanceNumber}");
                    
                    if (i < tapCount && delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("TapMultiple", DateTime.UtcNow - startTime);
                
                return true;
            }
            catch (Exception ex)
            {
                RecordFailedOperation();
                _logger.LogError($"[ADBv2] Multiple tap failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Swipe gesture (inspired by wosbot-master's swipe functionality)
        /// </summary>
        public async Task<bool> SwipeAsync(int startX, int startY, int endX, int endY, int durationMs = 500, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!await IsConnectedAndResponsiveAsync(cancellationToken))
                {
                    return false;
                }

                var command = $"input swipe {startX} {startY} {endX} {endY} {durationMs}";
                await ExecuteShellCommandAsync(command, cancellationToken);
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("Swipe", DateTime.UtcNow - startTime);
                
                _logger.LogInfo($"[ADBv2] Swipe executed from ({startX},{startY}) to ({endX},{endY}) on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                RecordFailedOperation();
                _logger.LogError($"[ADBv2] Swipe failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Press back button (similar to wosbot-master's pressBackButton)
        /// </summary>
        public async Task<bool> PressBackButtonAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await ExecuteShellCommandAsync("input keyevent KEYCODE_BACK", cancellationToken);
                _logger.LogInfo($"[ADBv2] Back button pressed on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Back button failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if app is installed (similar to wosbot-master's isAppInstalled)
        /// </summary>
        public async Task<bool> IsAppInstalledAsync(string packageName, CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await ExecuteShellCommandAsync($"pm list packages | grep {packageName}", cancellationToken);
                return !string.IsNullOrWhiteSpace(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Package check failed for {packageName} on instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if app is in foreground (similar to wosbot-master's isPackageRunning)
        /// </summary>
        public async Task<bool> IsAppInForegroundAsync(string packageName, CancellationToken cancellationToken = default)
        {
            try
            {
                // Try multiple dumpsys commands like wosbot-master does
                var commands = new[]
                {
                    "dumpsys window windows",
                    "dumpsys window displays", 
                    "dumpsys activity activities"
                };

                foreach (var command in commands)
                {
                    var result = await ExecuteShellCommandAsync(command, cancellationToken);
                    
                    if (command.Contains("window"))
                    {
                        if ((result.Contains("mCurrentFocus") || result.Contains("mFocusedApp")) && 
                            result.Contains($"{packageName}/"))
                        {
                            _logger.LogInfo($"[ADBv2] App {packageName} detected in foreground on instance {_instanceNumber}");
                            return true;
                        }
                    }
                    else if (command.Contains("activity"))
                    {
                        if (result.Contains("mResumedActivity") && result.Contains($"{packageName}/"))
                        {
                            _logger.LogInfo($"[ADBv2] App {packageName} detected in foreground on instance {_instanceNumber}");
                            return true;
                        }
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Foreground check failed for {packageName} on instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Launch app (similar to wosbot-master's launchApp)
        /// </summary>
        public async Task<bool> LaunchAppAsync(string packageName, CancellationToken cancellationToken = default)
        {
            try
            {
                await ExecuteShellCommandAsync($"monkey -p {packageName} -c android.intent.category.LAUNCHER 1", cancellationToken);
                _logger.LogInfo($"[ADBv2] App {packageName} launched on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] App launch failed for {packageName} on instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Command batching for performance (batch multiple commands together)
        /// </summary>
        public void BatchCommand(string command)
        {
            lock (_batchedCommands)
            {
                _batchedCommands.Add(command);
            }
        }

        /// <summary>
        /// Execute all batched commands at once
        /// </summary>
        public async Task<bool> ExecuteBatchedCommandsAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            List<string> commandsToExecute;
            
            lock (_batchedCommands)
            {
                commandsToExecute = new List<string>(_batchedCommands);
                _batchedCommands.Clear();
            }

            if (!commandsToExecute.Any())
            {
                return true;
            }

            try
            {
                // Execute commands as a single shell script
                var combinedCommand = string.Join(" && ", commandsToExecute);
                await ExecuteShellCommandAsync(combinedCommand, cancellationToken);
                
                RecordPerformanceMetric("BatchedCommands", DateTime.UtcNow - startTime);
                _logger.LogInfo($"[ADBv2] Executed {commandsToExecute.Count} batched commands on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Batched commands failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Enhanced connection health check (similar to wosbot-master's device.isOnline())
        /// </summary>
        public async Task<bool> IsConnectedAndResponsiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    return false;
                }

                // Quick responsiveness test
                await ExecuteShellCommandAsync("echo ping", cancellationToken, timeoutMs: 5000);
                return true;
            }
            catch
            {
                ADBConnectionManagerV2.MarkConnectionUnhealthy(_instanceNumber, _logger);
                return false;
            }
        }

        /// <summary>
        /// Core shell command execution
        /// </summary>
        private async Task<string> ExecuteShellCommandAsync(string command, CancellationToken cancellationToken = default, int timeoutMs = 15000)
        {
            await _commandSemaphore.WaitAsync(cancellationToken);
            
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                
                var receiver = new ConsoleOutputReceiver();
                await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, combinedCts.Token);
                
                return receiver.ToString();
            }
            finally
            {
                _commandSemaphore.Release();
            }
        }

        /// <summary>
        /// Performance and health tracking
        /// </summary>
        private void RecordSuccessfulOperation()
        {
            _lastSuccessfulOperation = DateTime.UtcNow;
            _consecutiveFailures = 0;
        }

        private void RecordFailedOperation()
        {
            _consecutiveFailures++;
        }

        private static void RecordPerformanceMetric(string operation, TimeSpan duration)
        {
            _performanceMetrics.AddOrUpdate(operation, 
                (duration.Ticks, 1),
                (key, existing) => (existing.TotalTime + duration.Ticks, existing.Count + 1));
        }

        /// <summary>
        /// Fix screencap line ending issues (common problem with screencap -p)
        /// </summary>
        private static byte[] FixScreencapLineEndings(byte[] data)
        {
            // Convert \r\n to \n for PNG compatibility
            var result = new List<byte>();
            
            for (int i = 0; i < data.Length; i++)
            {
                if (i < data.Length - 1 && data[i] == 0x0D && data[i + 1] == 0x0A)
                {
                    result.Add(0x0A); // Add only \n
                    i++; // Skip the \r
                }
                else
                {
                    result.Add(data[i]);
                }
            }
            
            return result.ToArray();
        }

        /// <summary>
        /// Get performance statistics
        /// </summary>
        public static Dictionary<string, (double AvgMs, int Count)> GetPerformanceStats()
        {
            var stats = new Dictionary<string, (double AvgMs, int Count)>();
            
            foreach (var kvp in _performanceMetrics)
            {
                var avgTicks = kvp.Value.TotalTime / kvp.Value.Count;
                var avgMs = new TimeSpan(avgTicks).TotalMilliseconds;
                stats[kvp.Key] = (avgMs, kvp.Value.Count);
            }
            
            return stats;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _commandSemaphore?.Dispose();
                _disposed = true;
                _logger.LogInfo($"[ADBv2] Controller disposed for instance {_instanceNumber}");
            }
        }
    }
}