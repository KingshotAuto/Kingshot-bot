using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bot.Core.Logging;
using System.Threading;
using System.Text.RegularExpressions;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Provides methods to interact with an LDPlayer emulator instance via ADB.
    /// Handles command execution, device input, screenshot capture, and connection checks.
    /// </summary>
    public class ADBController : IDisposable
    {
        private readonly string _adbPath;
        private readonly string _deviceSerial;
        private readonly LogService _logger;
        private bool _disposed = false;
        // Semaphore to ensure only one command runs at a time per controller
        // This is correct - each instance should have its own command queue
        private readonly SemaphoreSlim _commandSemaphore = new(1, 1);

        /// <summary>
        /// Constructs an ADBController for a specific emulator device.
        /// </summary>
        public ADBController(string adbPath, string deviceSerial, LogService logger)
        {
            _adbPath = adbPath;
            _deviceSerial = deviceSerial;
            
            // Attempt to extract instance number from device serial for logging
            var instanceNumber = 0;
            var match = Regex.Match(deviceSerial, @"emulator-(\d+)");
            if (match.Success)
            {
                var serialNum = int.Parse(match.Groups[1].Value);
                instanceNumber = (serialNum - 5554) / 2;
            }

            _logger = new LogService(instanceNumber);
            _logger.LogInfo($"ADBController created: {_adbPath} -> {_deviceSerial}");
        }

        /// <summary>
        /// Runs an ADB command without specifying a device (used for global commands like 'devices').
        /// </summary>
        private async Task<string> RunAdbCommandWithoutDeviceAsync(string arguments, int timeoutMs = 10000, CancellationToken cancellationToken = default)
        {
            var psi = new ProcessStartInfo(_adbPath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return "Failed to start process.";
            
            // Create timeout cancellation token
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var waitTask = process.WaitForExitAsync(linkedCts.Token);
                
                await Task.WhenAll(outputTask, errorTask, waitTask);
                
                string output = await outputTask;
                string error = await errorTask;
                return string.IsNullOrEmpty(output) ? error : output;
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogError($"ADB command timed out after {timeoutMs}ms: {arguments}");
                try { process.Kill(); } catch { /* Ignore kill errors */ }
                return "Command timed out";
            }
        }

        /// <summary>
        /// Runs an ADB command for the specific device serial.
        /// </summary>
        private async Task<string> RunAdbCommandAsync(string arguments, int timeoutMs = 10000, CancellationToken cancellationToken = default)
        {
            // Add timeout for semaphore acquisition
            if (!await _commandSemaphore.WaitAsync(timeoutMs / 2, cancellationToken))
            {
                _logger.LogError($"Failed to acquire command semaphore within {timeoutMs / 2}ms for: {arguments}");
                throw new TimeoutException($"Command semaphore timeout: {arguments}");
            }
            
            try
            {
                return await RunAdbCommandWithoutDeviceAsync($"-s {_deviceSerial} {arguments}", timeoutMs, cancellationToken);
            }
            finally
            {
                _commandSemaphore.Release();
            }
        }

        /// <summary>
        /// Simulates a tap at the specified coordinates on the device.
        /// </summary>
        public async Task TapAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            _logger.LogInfo($"Tapping at coordinates ({x}, {y})");
            await RunAdbCommandAsync($"shell input tap {x} {y}", 5000, cancellationToken);
        }
        /// <summary>
        /// Simulates a swipe gesture between two points on the device.
        /// </summary>
        public async Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs, CancellationToken cancellationToken = default)
        {
            _logger.LogInfo($"Swiping from ({x1}, {y1}) to ({x2}, {y2}) over {durationMs}ms");
            await RunAdbCommandAsync($"shell input swipe {x1} {y1} {x2} {y2} {durationMs}", 5000, cancellationToken);
        }
        /// <summary>
        /// Inputs text on the device.
        /// </summary>
        public Task InputTextAsync(string text, CancellationToken cancellationToken = default) => RunAdbCommandAsync($"shell input text \"{text.Replace(" ", "%s")}\"", 5000, cancellationToken);
        /// <summary>
        /// Sends a key event to the device.
        /// </summary>
        public Task InputKeyEventAsync(int keyCode, CancellationToken cancellationToken = default) => RunAdbCommandAsync($"shell input keyevent {keyCode}", 5000, cancellationToken);


        /// <summary>
        /// Lists all devices currently connected to ADB.
        /// </summary>
        public async Task<string> ListDevicesAsync(CancellationToken cancellationToken = default)
        {
            return await RunAdbCommandWithoutDeviceAsync("devices", 5000, cancellationToken);
        }

        /// <summary>
        /// Checks if the device is connected and responsive to ADB commands.
        /// </summary>
        public async Task<bool> IsConnectedAndResponsive(CancellationToken cancellationToken = default)
        {
            try
            {
                // First check if device is listed (quick check)
                var devices = await RunAdbCommandWithoutDeviceAsync("devices", 3000, cancellationToken);
                if (!devices.Contains($"{_deviceSerial}\tdevice"))
                {
                    return false;
                }

                // Then try a simple shell command to verify responsiveness
                var result = await RunAdbCommandAsync("shell echo test", 5000, cancellationToken);
                return result.Trim() == "test";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Takes a screenshot of the device's current screen and returns it as a byte array.
        /// </summary>
        public async Task<byte[]> TakeScreenshotAsync(CancellationToken cancellationToken = default)
        {
            // Use adb exec-out to get raw framebuffer data (much faster than PNG)
            var psi = new ProcessStartInfo(_adbPath, $"-s {_deviceSerial} exec-out screencap")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("Failed to start adb screencap process.");

            // Create timeout for screenshot operation (15 seconds)
            using var timeoutCts = new CancellationTokenSource(15000);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var ms = new MemoryStream();
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(ms, linkedCts.Token);
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogError("Screenshot operation timed out after 15 seconds");
                try { process.Kill(); } catch { /* Ignore kill errors */ }
                throw new TimeoutException("Screenshot operation timed out");
            }
            var raw = ms.ToArray();

            // Parse header to get width, height, and format
            int width = (raw[0] & 0xFF) | ((raw[1] & 0xFF) << 8) | ((raw[2] & 0xFF) << 16) | ((raw[3] & 0xFF) << 24);
            int height = (raw[4] & 0xFF) | ((raw[5] & 0xFF) << 8) | ((raw[6] & 0xFF) << 16) | ((raw[7] & 0xFF) << 24);
            var format = (raw[8] & 0xFF) | ((raw[9] & 0xFF) << 8) | ((raw[10] & 0xFF) << 16) | ((raw[11] & 0xFF) << 24);
            
            // Format codes from Android's HAL_PIXEL_FORMAT_*
            const int HAL_PIXEL_FORMAT_RGBA_8888 = 0x1;  // RGBA
            const int HAL_PIXEL_FORMAT_RGBX_8888 = 0x2;  // RGB
            const int HAL_PIXEL_FORMAT_BGRA_8888 = 0x3;  // BGRA
            const int HAL_PIXEL_FORMAT_BGRX_8888 = 0x4;  // BGR

            string formatName = format switch
            {
                HAL_PIXEL_FORMAT_RGBA_8888 => "RGBA",
                HAL_PIXEL_FORMAT_BGRA_8888 => "BGRA",
                HAL_PIXEL_FORMAT_RGBX_8888 => "RGB",
                HAL_PIXEL_FORMAT_BGRX_8888 => "BGR",
                _ => $"Unknown (0x{format:X8})"
            };

            // Determine pixel size based on format
            int pixelSize = format switch
            {
                HAL_PIXEL_FORMAT_RGBA_8888 => 4, // RGBA
                HAL_PIXEL_FORMAT_BGRA_8888 => 4, // BGRA
                HAL_PIXEL_FORMAT_RGBX_8888 => 3, // RGB
                HAL_PIXEL_FORMAT_BGRX_8888 => 3, // BGR
                _ => 4 // Default to RGBA
            };

            int expectedLen = width * height * pixelSize;
            if (raw.Length < 12 + expectedLen)
                throw new Exception($"Raw screencap data too short: expected {expectedLen} bytes, got {raw.Length - 12}");

            // Convert raw framebuffer to Bitmap
            using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bmpData = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* dst = (byte*)bmpData.Scan0;
                int srcIdx = 12;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte r, g, b, a;
                        switch (format)
                        {
                            case HAL_PIXEL_FORMAT_RGBA_8888: // RGBA
                                r = raw[srcIdx];
                                g = raw[srcIdx + 1];
                                b = raw[srcIdx + 2];
                                a = raw[srcIdx + 3];
                                break;
                            case HAL_PIXEL_FORMAT_BGRA_8888: // BGRA
                                b = raw[srcIdx];
                                g = raw[srcIdx + 1];
                                r = raw[srcIdx + 2];
                                a = raw[srcIdx + 3];
                                break;
                            case HAL_PIXEL_FORMAT_RGBX_8888: // RGB
                                r = raw[srcIdx];
                                g = raw[srcIdx + 1];
                                b = raw[srcIdx + 2];
                                a = 255;
                                break;
                            case HAL_PIXEL_FORMAT_BGRX_8888: // BGR
                                b = raw[srcIdx];
                                g = raw[srcIdx + 1];
                                r = raw[srcIdx + 2];
                                a = 255;
                                break;
                            default: // Default to RGBA
                                r = raw[srcIdx];
                                g = raw[srcIdx + 1];
                                b = raw[srcIdx + 2];
                                a = raw[srcIdx + 3];
                                break;
                        }
                        // ARGB in memory: [b, g, r, a]
                        dst[0] = b;
                        dst[1] = g;
                        dst[2] = r;
                        dst[3] = a;
                        dst += 4;
                        srcIdx += pixelSize;
                    }
                }
            }
            bmp.UnlockBits(bmpData);
            // Convert Bitmap to PNG byte array for compatibility
            using var outStream = new MemoryStream();
            bmp.Save(outStream, System.Drawing.Imaging.ImageFormat.Png);
            return outStream.ToArray();
        }

        /// <summary>
        /// Disposes resources used by the controller.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _commandSemaphore.Dispose();
            }
            _disposed = true;
        }

        /// <summary>
        /// Executes an LDPlayer macro file (.record) on the emulator instance.
        /// </summary>
        /// <param name="macroFilePath">Path to the .record macro file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        public async Task<bool> ExecuteMacroAsync(string macroFilePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(macroFilePath))
            {
                _logger.LogError($"Macro file not found: {macroFilePath}");
                return false;
            }

            try
            {
                // LDPlayer macro execution via ADB - send the macro file to the device and execute it
                // First, push the macro file to the device
                var pushResult = await RunAdbCommandAsync($"push \"{macroFilePath}\" /sdcard/macro.record", 30000, cancellationToken);
                
                if (!string.IsNullOrEmpty(pushResult) && pushResult.Contains("error"))
                {
                    _logger.LogError($"Failed to push macro file: {pushResult}");
                    return false;
                }

                // Execute the macro via broadcast intent (LDPlayer specific)
                var executeResult = await RunAdbCommandAsync("shell am broadcast -a android.intent.action.MACRO_EXECUTE -e path /sdcard/macro.record", 30000, cancellationToken);
                
                _logger.LogInfo($"Macro execution result: {executeResult}");
                return !executeResult.Contains("error");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing macro {macroFilePath}: {ex.Message}");
                return false;
            }
        }

        ~ADBController()
        {
            Dispose(false);
        }
    }
} 