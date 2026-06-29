using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Core;
using Serilog.Sinks.File;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Bot.Core.Utils;

namespace Bot.Core.Logging
{
    public static class SerilogConfiguration
    {
        private static ILogger? _logger;
        private static readonly object _lockObject = new object();
        
        // Cache for instance names to avoid repeated dnconsole calls
        internal static readonly ConcurrentDictionary<int, string> _instanceNameCache = new();
        internal static DateTime _cacheLastUpdated = DateTime.MinValue;
        internal static readonly TimeSpan CacheTimeout = TimeSpan.FromMinutes(30);

        // Destructuring policies to mask sensitive data
        private class SensitiveDataDestructuringPolicy : IDestructuringPolicy
        {
            public bool TryDestructure(object? value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue? result)
            {
                if (value is string stringValue)
                {
                    // Mask hardware IDs, device IDs, and similar patterns
                    stringValue = MaskSensitiveData(stringValue);
                    result = new ScalarValue(stringValue);
                    return true;
                }

                result = null;
                return false;
            }
        }

        private static string MaskSensitiveData(string input)
        {
            // Hardware ID patterns (typical formats)
            input = Regex.Replace(input, @"Hardware\s*ID:?\s*[A-F0-9\-]{8,}", "Hardware ID: [REDACTED]", RegexOptions.IgnoreCase);
            
            // Device ID patterns
            input = Regex.Replace(input, @"Device\s*ID:?\s*[A-F0-9\-]{8,}", "Device ID: [REDACTED]", RegexOptions.IgnoreCase);
            
            // MAC Address patterns
            input = Regex.Replace(input, @"([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})", "XX:XX:XX:XX:XX:XX");
            
            // IP Address patterns
            input = Regex.Replace(input, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "XXX.XXX.XXX.XXX");
            
            // UUID/GUID patterns
            input = Regex.Replace(input, @"[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX");
            
            // Windows username in paths
            input = Regex.Replace(input, @"C:\\Users\\[^\\]+\\", @"C:\Users\[REDACTED]\");
            
            // License keys or similar patterns
            input = Regex.Replace(input, @"License\s*Key:?\s*[A-Z0-9\-]{10,}", "License Key: [REDACTED]", RegexOptions.IgnoreCase);
            
            return input;
        }

        /// <summary>
        /// Gets the instance name with caching to avoid repeated dnconsole calls
        /// </summary>
        public static async Task<string> GetInstanceNameAsync(int instanceNumber)
        {
            // Check cache first
            if (DateTime.UtcNow - _cacheLastUpdated < CacheTimeout && 
                _instanceNameCache.TryGetValue(instanceNumber, out var cachedName))
            {
                return cachedName;
            }

            try
            {
                // Get fresh name from dnconsole
                var instanceName = await LDPlayerHelper.GetInstanceNameAsync(instanceNumber);
                
                // Update cache
                _instanceNameCache[instanceNumber] = instanceName;
                _cacheLastUpdated = DateTime.UtcNow;
                
                return instanceName;
            }
            catch (Exception)
            {
                // Fallback to default naming if anything goes wrong
                var fallbackName = $"Instance{instanceNumber}";
                _instanceNameCache[instanceNumber] = fallbackName;
                return fallbackName;
            }
        }

        /// <summary>
        /// Creates a sanitized filename from instance name
        /// </summary>
        public static string SanitizeInstanceName(string instanceName)
        {
            // Remove or replace invalid filename characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = instanceName;
            
            foreach (var invalidChar in invalidChars)
            {
                sanitized = sanitized.Replace(invalidChar, '_');
            }
            
            // Also replace spaces and other potentially problematic characters
            sanitized = sanitized.Replace(' ', '_')
                                .Replace(':', '_')
                                .Replace('/', '_')
                                .Replace('\\', '_');
            
            return sanitized;
        }

        private static void SetLogFilePermissions(string logDirectory)
        {
            try
            {
                // Get the directory info
                var dirInfo = new DirectoryInfo(logDirectory);

                // Get the access control list
                var dirSecurity = dirInfo.GetAccessControl();

                // Set inheritance rules
                dirSecurity.SetAccessRuleProtection(false, true);

                // Add full control for Users group
                var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var usersAccessRule = new FileSystemAccessRule(
                    usersIdentity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);

                dirSecurity.AddAccessRule(usersAccessRule);

                // Apply the new access control settings
                dirInfo.SetAccessControl(dirSecurity);

                // Also set permissions for any existing log files
                foreach (var file in dirInfo.GetFiles("*.txt"))
                {
                    var fileSecurity = file.GetAccessControl();
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(
                        usersIdentity,
                        FileSystemRights.FullControl,
                        AccessControlType.Allow));
                    file.SetAccessControl(fileSecurity);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not set log file permissions: {ex.Message}");
                // Continue anyway - logging should still work, just might require admin access
            }
        }

        public static ILogger GetLogger()
        {
            if (_logger != null)
                return _logger;

            lock (_lockObject)
            {
                if (_logger != null)
                    return _logger;

                _logger = CreateLogger();
                return _logger;
            }
        }

        private static ILogger CreateLogger()
        {
            // Try to find the project root directory by looking for key files
            var logDirectory = FindProjectRootLogDirectory() ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            
            Directory.CreateDirectory(logDirectory);

            // Set permissions for the log directory and files
            SetLogFilePermissions(logDirectory);
            
            // Clean up old log files before starting new logger
            CleanupOldLogFiles(logDirectory);

            var loggerConfig = new LoggerConfiguration()
                // Set minimum level
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                
                // Enrich with context properties  
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "KingshotAuto")
                
                // Add destructuring policies for sensitive data
                .Destructure.With<SensitiveDataDestructuringPolicy>()
                
                // Filter out sensitive messages entirely
                .Filter.ByExcluding(evt => 
                    evt.MessageTemplate.Text.Contains("Hardware ID", StringComparison.OrdinalIgnoreCase) ||
                    evt.MessageTemplate.Text.Contains("Device ID", StringComparison.OrdinalIgnoreCase) ||
                    evt.MessageTemplate.Text.Contains("License Key", StringComparison.OrdinalIgnoreCase) ||
                    evt.MessageTemplate.Text.Contains("MAC Address", StringComparison.OrdinalIgnoreCase) ||
                    evt.MessageTemplate.Text.Contains("UUID", StringComparison.OrdinalIgnoreCase) ||
                    evt.MessageTemplate.Text.Contains("GUID", StringComparison.OrdinalIgnoreCase) ||
                    evt.MessageTemplate.Text.Contains("Serial Number", StringComparison.OrdinalIgnoreCase))
                
                // Main log file - captures everything
                .WriteTo.File(
                    path: Path.Combine(logDirectory, "logs.txt"),
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] [{Category}] {Message:lj}{NewLine}{Exception}",
                    shared: true,
                    fileSizeLimitBytes: 10_000_000,
                    rollOnFileSizeLimit: true)
                
                // Console output for debugging (with minimal sensitive data)
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Information);

            // Add dynamic instance-specific sinks using a custom sink
            loggerConfig = loggerConfig.WriteTo.Sink(new DynamicInstanceLogSink(logDirectory));
                
            var logger = loggerConfig.CreateLogger();
                
            // Write a startup log entry to confirm logging is working
            logger.Information("🚀 KingshotAuto logging system initialized - logs will be saved to: {LogDirectory}", logDirectory);
            logger.Information("📝 Instance-specific logs will use format: log-{InstanceName}-dd-MM-yy.txt");
            
            return logger;
        }

        private static void CleanupOldLogFiles(string logDirectory)
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    return;

                var cutoffDate = DateTime.Now.AddDays(-3); // Keep logs for 3 days
                var logFiles = Directory.GetFiles(logDirectory, "*.txt");
                var deletedCount = 0;
                var totalSize = 0L;

                foreach (var file in logFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var fileName = Path.GetFileName(file);
                        
                        // Delete files older than 3 days
                        // Include both old format (instance_X_yyyyMMdd.txt) and new format (log-InstanceName-dd-MM-yy.txt)
                        if (fileInfo.LastWriteTime < cutoffDate && (
                            fileName.StartsWith("logs", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("instance_", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("log-", StringComparison.OrdinalIgnoreCase)))
                        {
                            totalSize += fileInfo.Length;
                            File.Delete(file);
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LogCleanup] Could not delete {file}: {ex.Message}");
                    }
                }

                if (deletedCount > 0)
                {
                    var sizeMB = totalSize / (1024.0 * 1024.0);
                    Console.WriteLine($"[LogCleanup] Cleaned up {deletedCount} old log files, freed {sizeMB:F2} MB");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LogCleanup] Error during log cleanup: {ex.Message}");
            }
        }

        private static string? FindProjectRootLogDirectory()
        {
            try
            {
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var directory = new DirectoryInfo(currentDir);
                
                // Walk up the directory tree looking for project root indicators
                while (directory != null && directory.Parent != null)
                {
                    // Look for solution file or key project files that indicate this is the project root
                    if (File.Exists(Path.Combine(directory.FullName, "BotApp.sln")) ||
                        File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")) ||
                        Directory.Exists(Path.Combine(directory.FullName, "Bot.Core")))
                    {
                        var logsPath = Path.Combine(directory.FullName, "logs");
                        return logsPath;
                    }
                    directory = directory.Parent;
                }
                
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Dispose()
        {
            if (_logger is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _logger = null;
        }
    }

    /// <summary>
    /// Custom Serilog sink that creates dynamic instance-specific log files with proper naming
    /// </summary>
    public class DynamicInstanceLogSink : ILogEventSink, IDisposable
    {
        private readonly string _logDirectory;
        private readonly ConcurrentDictionary<string, ILogger> _instanceLoggers = new();
        private readonly object _lockObject = new object();

        public DynamicInstanceLogSink(string logDirectory)
        {
            _logDirectory = logDirectory;
        }

        public void Emit(LogEvent logEvent)
        {
            // Only handle events that have InstanceId property
            if (!logEvent.Properties.TryGetValue("InstanceId", out var instanceIdProperty) || 
                !(instanceIdProperty is ScalarValue scalarValue) ||
                !int.TryParse(scalarValue.Value?.ToString(), out var instanceNumber))
            {
                return;
            }

            try
            {
                // Get instance name asynchronously (this will use the cache)
                var instanceName = GetInstanceNameSync(instanceNumber);
                var sanitizedName = SerilogConfiguration.SanitizeInstanceName(instanceName);
                
                // Create filename: log-{instanceName}-dd-MM-yy.txt
                var fileName = $"log-{sanitizedName}-{DateTime.Now:dd-MM-yy}.txt";
                // Use instance number as primary key to prevent duplicate loggers
                var loggerKey = $"instance_{instanceNumber}_{DateTime.Now:yyyyMMdd}";

                // Get or create logger for this instance
                if (!_instanceLoggers.TryGetValue(loggerKey, out var instanceLogger))
                {
                    lock (_lockObject)
                    {
                        if (!_instanceLoggers.TryGetValue(loggerKey, out instanceLogger))
                        {
                            var filePath = Path.Combine(_logDirectory, fileName);
                            instanceLogger = new LoggerConfiguration()
                                .WriteTo.File(
                                    path: filePath,
                                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] [{Category}] {Message:lj}{NewLine}{Exception}",
                                    shared: true,
                                    fileSizeLimitBytes: 10_000_000,
                                    rollOnFileSizeLimit: true)
                                .CreateLogger();

                            _instanceLoggers[loggerKey] = instanceLogger;
                        }
                    }
                }

                // Write the log event to the instance-specific logger
                instanceLogger.Write(logEvent);
            }
            catch (Exception ex)
            {
                // If anything goes wrong, just write to console to avoid breaking logging
                Console.WriteLine($"[DynamicInstanceLogSink] Error writing log for instance {instanceNumber}: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronous wrapper for getting instance name (blocks once to get real name, then uses cache)
        /// </summary>
        private static string GetInstanceNameSync(int instanceNumber)
        {
            // Check if we have it in cache and it's recent
            if (SerilogConfiguration._instanceNameCache.TryGetValue(instanceNumber, out var cachedName) &&
                DateTime.UtcNow - SerilogConfiguration._cacheLastUpdated < SerilogConfiguration.CacheTimeout)
            {
                return cachedName;
            }

            // If not in cache, we need to get the real name to avoid duplicate logs
            // This will block briefly on first access but prevents fallback log creation
            try
            {
                // Use GetAwaiter().GetResult() to make async call synchronous
                var realName = SerilogConfiguration.GetInstanceNameAsync(instanceNumber).GetAwaiter().GetResult();
                return realName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DynamicInstanceLogSink] Error getting real instance name for {instanceNumber}: {ex.Message}");
                // Only use fallback if real name retrieval fails
                var fallbackName = $"Instance{instanceNumber}";
                SerilogConfiguration._instanceNameCache.TryAdd(instanceNumber, fallbackName);
                return fallbackName;
            }
        }

        public void Dispose()
        {
            foreach (var logger in _instanceLoggers.Values)
            {
                if (logger is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _instanceLoggers.Clear();
        }
    }
} 