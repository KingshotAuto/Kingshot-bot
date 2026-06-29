using System;
using System.IO;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Diagnostics;
using Bot.Core.Logging;

namespace Bot.Core.Utils
{
    public static class LDPlayerHelper
    {
        private static string? _ldConsolePath;
        private static string? _dnConsolePath;
        private static string? _adbPath;
        private static string? _cachedInstallPath;
        private static string? _manualInstallPath;
        private static LogService? _logger;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetShortPathName(
            string lpszLongPath,
            StringBuilder lpszShortPath,
            int cchBuffer);

        private static string GetSafePathString(string path)
        {
            // Convert the path to its full form to handle any Unicode characters
            path = Path.GetFullPath(path);
            
            // If the path contains non-ASCII characters, get the 8.3 short path name
            if (path.Any(c => c > 127))
            {
                var shortPath = GetShortPath(path);
                if (!string.IsNullOrEmpty(shortPath))
                {
                    return shortPath;
                }
            }
            
            return path;
        }

        private static string GetShortPath(string longPath)
        {
            var shortPathBuilder = new StringBuilder(260); // MAX_PATH
            int result = GetShortPathName(longPath, shortPathBuilder, shortPathBuilder.Capacity);
            
            if (result == 0)
            {
                // If failed, return original path
                return longPath;
            }
            
            return shortPathBuilder.ToString();
        }

        public static void SetLogger(LogService? logger)
        {
            _logger = logger;
        }

        public static void SetManualInstallPath(string path)
        {
            path = GetSafePathString(path);
            
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"The specified LDPlayer directory does not exist: {path}");

            var ldConsole = Path.Combine(path, "ldconsole.exe");
            var dnConsole = Path.Combine(path, "dnconsole.exe");

            if (!File.Exists(ldConsole) || !File.Exists(dnConsole))
                throw new FileNotFoundException($"Required LDPlayer executables not found in directory: {path}");

            _manualInstallPath = path;
            _logger?.LogInfo($"LDPlayer path set manually: {path}");
            ClearPathCache();
        }

        private static string GetLDPlayerInstallPath()
        {
            // If manual path is set and valid, use it
            if (!string.IsNullOrEmpty(_manualInstallPath) && 
                Directory.Exists(_manualInstallPath) && 
                File.Exists(Path.Combine(_manualInstallPath, "ldconsole.exe")))
            {
                _logger?.LogInfo($"LDPlayer detected via manual path: {_manualInstallPath}");
                return GetSafePathString(_manualInstallPath);
            }

            // If we have a cached path that's still valid, use it
            if (!string.IsNullOrEmpty(_cachedInstallPath) && 
                Directory.Exists(_cachedInstallPath) && 
                File.Exists(Path.Combine(_cachedInstallPath, "ldconsole.exe")))
            {
                _logger?.LogInfo($"LDPlayer detected via cached path: {_cachedInstallPath}");
                return GetSafePathString(_cachedInstallPath);
            }

            var errors = new List<string>();

            try
            {
                // Common registry keys for different LDPlayer versions and localizations
                string[] registryKeys = {
                    @"SOFTWARE\XuanZhi\LDPlayer9",
                    @"SOFTWARE\LDPlayer9",
                    @"SOFTWARE\ChangZhi\LDPlayer9",
                    @"SOFTWARE\雷电模拟器9",  // Chinese
                    @"SOFTWARE\雷電模擬器9",  // Traditional Chinese
                    @"SOFTWARE\LDプレーヤー9", // Japanese
                    @"SOFTWARE\LDエミュレータ9" // Japanese alternative
                };

                foreach (var key in registryKeys)
                {
                    var path = GetPathFromRegistry(key);
                    if (!string.IsNullOrEmpty(path) && IsValidLDPlayerPath(path))
                    {
                        _cachedInstallPath = path;
                        _logger?.LogInfo($"LDPlayer 9 detected via registry: {key} -> {path}");
                        return GetSafePathString(path);
                    }
                }

                // Get all possible program files paths
                var programPaths = new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"C:\Program Files",
                    @"C:\Program Files (x86)"
                }.Distinct().Where(p => Directory.Exists(p));

                // Only look for LDPlayer 9 subfolders
                var subfolders = new[] {
                    @"LDPlayer\LDPlayer9",
                    "LDPlayer9",
                    @"雷电模拟器\LDPlayer9",    // Chinese
                    @"雷電模擬器\LDPlayer9",    // Traditional Chinese
                    @"LDプレーヤー9",          // Japanese
                    @"LDエミュレータ9"         // Japanese alternative
                };

                // Check all program files locations
                foreach (var programPath in programPaths)
                {
                    foreach (var subfolder in subfolders)
                    {
                        var fullPath = GetSafePathString(Path.Combine(programPath, subfolder));
                        if (IsValidLDPlayerPath(fullPath))
                        {
                            _cachedInstallPath = fullPath;
                            _logger?.LogInfo($"LDPlayer 9 detected via Program Files search: {fullPath}");
                            return fullPath;
                        }
                    }
                }

                // Check direct drive paths with parallel processing
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed)
                    .Select(d => d.Name.TrimEnd('\\'))
                    .ToList();

                string? foundPath = null;
                var lockObj = new object();

                Parallel.ForEach(drives, (drive, loopState) =>
                {
                    if (loopState.ShouldExitCurrentIteration) return;

                    foreach (var subfolder in subfolders)
                    {
                        if (loopState.ShouldExitCurrentIteration) return;

                        try
                        {
                            var fullPath = GetSafePathString(Path.Combine(drive, subfolder));
                            if (IsValidLDPlayerPath(fullPath))
                            {
                                lock (lockObj)
                                {
                                    if (foundPath == null) // First thread to find a valid path
                                    {
                                        foundPath = fullPath;
                                        _cachedInstallPath = fullPath;
                                        _logger?.LogInfo($"LDPlayer 9 detected via parallel drive search: {fullPath}");
                                    }
                                }
                                loopState.Stop(); // Signal other threads to stop
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Silently continue if we can't access a path during parallel search
                            _logger?.LogWarning($"Error checking path {Path.Combine(drive, subfolder)}: {ex.Message}");
                        }
                    }
                });

                if (foundPath != null)
                {
                    return foundPath;
                }

                // If we get here, we couldn't find LDPlayer 9
                var searchedPaths = new List<string>();
                searchedPaths.AddRange(registryKeys.Select(k => $"Registry: {k}"));
                searchedPaths.AddRange(programPaths.SelectMany(p => subfolders.Select(s => Path.Combine(p, s))));
                searchedPaths.AddRange(drives.SelectMany(d => subfolders.Select(s => Path.Combine(d, s))));

                var errorMsg = "LDPlayer 9 installation not found. Searched in:\n" +
                             string.Join("\n", searchedPaths.Distinct().Take(5)) +
                             (searchedPaths.Count > 5 ? "\n... and more locations" : "");

                _logger?.LogError($"LDPlayer 9 detection failed - searched {searchedPaths.Count} locations");
                throw new FileNotFoundException(errorMsg);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                throw new FileNotFoundException(
                    "Failed to find LDPlayer 9 installation. Please ensure LDPlayer 9 is installed correctly, " +
                    "or use SetManualInstallPath() to specify the location manually.\n\n" +
                    "Errors encountered:\n" + string.Join("\n", errors), ex);
            }
        }

        private static bool IsValidLDPlayerPath(string path)
        {
            if (!Directory.Exists(path)) return false;
            
            var ldConsole = Path.Combine(path, "ldconsole.exe");
            var dnConsole = Path.Combine(path, "dnconsole.exe");
            
            return File.Exists(ldConsole) && File.Exists(dnConsole);
        }

        private static string? GetPathFromRegistry(string keyPath)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (key?.GetValue("InstallDir") is string path && !string.IsNullOrEmpty(path))
                        return GetSafePathString(path);
                }

                using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    if (key?.GetValue("InstallDir") is string path && !string.IsNullOrEmpty(path))
                        return GetSafePathString(path);
                }
            }
            catch
            {
                // Silently fail and return null - we'll try other methods
            }
            return null;
        }

        public static string GetLDPlayerConsolePath()
        {
            if (_ldConsolePath == null || !File.Exists(_ldConsolePath))
            {
                var installPath = GetLDPlayerInstallPath();
                _ldConsolePath = GetSafePathString(Path.Combine(installPath, "ldconsole.exe"));
                
                if (!File.Exists(_ldConsolePath))
                    throw new FileNotFoundException($"ldconsole.exe not found in LDPlayer directory: {installPath}");
            }
            return _ldConsolePath;
        }

        public static string GetDNPlayerConsolePath()
        {
            if (_dnConsolePath == null || !File.Exists(_dnConsolePath))
            {
                var installPath = GetLDPlayerInstallPath();
                _dnConsolePath = GetSafePathString(Path.Combine(installPath, "dnconsole.exe"));
                
                if (!File.Exists(_dnConsolePath))
                    throw new FileNotFoundException($"dnconsole.exe not found in LDPlayer directory: {installPath}");
            }
            return _dnConsolePath;
        }

        public static string GetADBPath()
        {
            if (_adbPath == null || !File.Exists(_adbPath))
            {
                var installPath = GetLDPlayerInstallPath();
                _adbPath = GetSafePathString(Path.Combine(installPath, "adb.exe"));
                
                if (!File.Exists(_adbPath))
                    throw new FileNotFoundException($"adb.exe not found in LDPlayer directory: {installPath}");
            }
            return _adbPath;
        }

        /// <summary>
        /// Gets the instance name/title for a given instance number using dnconsole list2
        /// </summary>
        /// <param name="instanceNumber">The instance number</param>
        /// <returns>The instance name/title, or "Instance{instanceNumber}" if not found</returns>
        public static async Task<string> GetInstanceNameAsync(int instanceNumber)
        {
            try
            {
                var dnConsolePath = GetDNPlayerConsolePath();
                var processInfo = new ProcessStartInfo
                {
                    FileName = dnConsolePath,
                    Arguments = "list2",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            var parts = line.Split(',');
                            if (parts.Length >= 2 && 
                                int.TryParse(parts[0].Trim(), out var index) && 
                                index == instanceNumber)
                            {
                                var title = parts[1].Trim();
                                if (!string.IsNullOrWhiteSpace(title))
                                {
                                    _logger?.LogInfo($"Found instance name: {instanceNumber} -> {title}");
                                    return title;
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _logger?.LogWarning($"dnconsole list2 stderr: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting instance name for {instanceNumber}: {ex.Message}");
            }

            // Fallback to default naming
            var fallbackName = $"Instance{instanceNumber}";
            _logger?.LogWarning($"Could not get instance name for {instanceNumber}, using fallback: {fallbackName}");
            return fallbackName;
        }

        public static void ClearPathCache()
        {
            _ldConsolePath = null;
            _dnConsolePath = null;
            _adbPath = null;
            _cachedInstallPath = null;
        }
    }
} 