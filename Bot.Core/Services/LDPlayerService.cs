using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;

namespace Bot.Core.Services
{
    public class LDPlayerService
    {
        private readonly LogService _logger;

        public LDPlayerService(LogService logger)
        {
            _logger = logger;
            // Set the logger in LDPlayerHelper for telemetry
            LDPlayerHelper.SetLogger(logger);
        }

        public async Task<List<LDPlayerInstance>> GetInstancesAsync()
        {
            var instances = new List<LDPlayerInstance>();

            try
            {
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                
                if (!File.Exists(dnConsolePath))
                {
                    _logger.LogError($"dnconsole.exe not found at path: {dnConsolePath}");
                    return instances;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = dnConsolePath,
                    Arguments = "list2",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    _logger.LogError($"dnconsole.exe exited with code: {process.ExitCode}");
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogError($"Error output: {error}");
                    }
                    return instances;
                }

                foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(',');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int index))
                    {
                        var instance = new LDPlayerInstance
                        {
                            Index = index,
                            Name = parts[1],
                            IsRunning = parts.Length >= 5 && parts[4] == "1"
                        };

                        // Add additional properties if available
                        if (parts.Length >= 3)
                        {
                            instance.TopWindowHandle = parts[2];
                        }
                        if (parts.Length >= 4)
                        {
                            instance.BindWindowHandle = parts[3];
                        }

                        instances.Add(instance);
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError($"LDPlayer executables not found: {ex.Message}");
                // Clear the path cache in case LDPlayer was moved/reinstalled
                LDPlayerHelper.ClearPathCache();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting LDPlayer instances: {ex.Message}");
            }

            return instances;
        }
    }
} 