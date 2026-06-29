using System;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Linq;
using Serilog;
using Serilog.Context;

namespace Bot.Core.Logging
{
    public class LogService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly int? _instanceNumber;
        private readonly string? _accountName;
        private volatile bool _disposed = false;

        // Static tracking to prevent excessive initialization messages
        private static readonly ConcurrentDictionary<string, bool> _initializedLoggers = new();
        private static volatile bool _globalLoggerInitialized = false;

        // Default constructor for global logs
        public LogService()
        {
            _logger = SerilogConfiguration.GetLogger();
            if (!_globalLoggerInitialized)
            {
                _globalLoggerInitialized = true;
                Console.WriteLine("[LogService] Global logger initialized with Serilog");
            }
        }

        // Constructor for per-instance logs
        public LogService(int instanceNumber)
        {
            _logger = SerilogConfiguration.GetLogger();
            _instanceNumber = instanceNumber;
            
            var key = $"instance_{instanceNumber}";
            if (_initializedLoggers.TryAdd(key, true))
            {
                Console.WriteLine($"[LogService] Instance {instanceNumber} logger initialized with Serilog");
            }
        }

        // Constructor for custom log file path (maintained for backward compatibility)
        public LogService(string customLogPath)
        {
            _logger = SerilogConfiguration.GetLogger();
            
            var key = $"custom_{customLogPath}";
            if (_initializedLoggers.TryAdd(key, true))
            {
                Console.WriteLine($"[LogService] Custom path logger initialized with Serilog: {customLogPath}");
            }
        }

        // Static factory method for creating instance-specific logger with account name
        public static LogService ForInstance(int instanceNumber, string? accountName = null)
        {
            return new LogService(instanceNumber, accountName);
        }

        // Private constructor for instance with account name
        private LogService(int instanceNumber, string? accountName)
        {
            _logger = SerilogConfiguration.GetLogger();
            _instanceNumber = instanceNumber;
            _accountName = accountName;
            
            var key = $"instance_{instanceNumber}_{accountName}";
            if (_initializedLoggers.TryAdd(key, true))
            {
                Console.WriteLine($"[LogService] Instance {instanceNumber} logger initialized for account '{accountName}' with Serilog");
            }
        }

        public virtual void LogInfo(string message, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            if (_disposed)
            {
                Console.WriteLine("[LogService] Attempted to log after disposal");
                return;
            }

            using (LogContext.PushProperty("Category", category ?? "General"))
            using (LogContext.PushProperty("Method", method))
            using (LogContext.PushProperty("InstanceId", _instanceNumber))
            using (LogContext.PushProperty("AccountName", _accountName))
            {
                _logger.Information(message);
            }
        }

        public virtual void LogError(string message, Exception? exception = null, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            if (_disposed)
            {
                Console.WriteLine("[LogService] Attempted to log error after disposal");
                return;
            }

            using (LogContext.PushProperty("Category", category ?? "General"))
            using (LogContext.PushProperty("Method", method))
            using (LogContext.PushProperty("InstanceId", _instanceNumber))
            using (LogContext.PushProperty("AccountName", _accountName))
            {
                if (exception != null)
                    _logger.Error(exception, "❌ {Message}", message);
                else
                    _logger.Error("❌ {Message}", message);
            }
        }

        public virtual void LogWarning(string message, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            if (_disposed) return;

            using (LogContext.PushProperty("Category", category ?? "General"))
            using (LogContext.PushProperty("Method", method))
            using (LogContext.PushProperty("InstanceId", _instanceNumber))
            using (LogContext.PushProperty("AccountName", _accountName))
            {
                _logger.Warning(message);
            }
        }

        // PERFORMANCE MONITORING helper
        public IDisposable StartPerformanceTimer(string operationName, object? context = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LogService));
            return new PerformanceTimer(this, operationName);
        }

        // TASK EXECUTION METRICS
        public void LogTaskExecution(string taskName, string accountName, int instanceNumber, bool success, TimeSpan duration)
        {
            if (_disposed) return;
            LogInfo($"Task {taskName} completed for {accountName} (Instance {instanceNumber}): {(success ? "SUCCESS" : "FAILED")} in {duration.TotalMilliseconds:F0}ms",
                   category: "TaskExecution");
        }

        // ADB COMMAND METRICS
        public void LogAdbCommand(string command, string deviceAddress, bool success, TimeSpan duration, string? output = null, string? error = null)
        {
            if (_disposed) return;
            var message = $"ADB: {command} -> {(success ? "SUCCESS" : "FAILED")} ({duration.TotalMilliseconds:F0}ms)";
            if (!string.IsNullOrEmpty(error))
            {
                message += $"\nError: {error}";
            }
            LogInfo(message, category: "ADB");
        }

        public virtual void LogTaskEnd(string taskName, bool success, string reason = "")
        {
            if (_disposed) return;
            var message = $"Task '{taskName}' {(success ? "completed successfully" : "failed")}";
            if (!string.IsNullOrEmpty(reason))
                message += $" - {reason}";
            
            if (success)
                LogInfo(message, category: "TaskExecution");
            else
                LogError(message, category: "TaskExecution");
        }

        public virtual void PurgeLogs()
        {
            if (_disposed) return;
            
            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var logsDirectory = Path.Combine(baseDirectory, "logs");
                
                if (Directory.Exists(logsDirectory))
                {
                    var logFiles = Directory.GetFiles(logsDirectory, "*.txt")
                        .Where(file => {
                            var fileName = Path.GetFileName(file);
                            return fileName.StartsWith("logs", StringComparison.OrdinalIgnoreCase) ||
                                   fileName.StartsWith("instance_", StringComparison.OrdinalIgnoreCase) ||
                                   fileName.StartsWith("log-", StringComparison.OrdinalIgnoreCase);
                        });
                    
                    foreach (var file in logFiles)
                    {
                        try
                        {
                            File.Delete(file);
                            Console.WriteLine($"[LOG PURGE] Deleted: {file}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[LOG PURGE ERROR] Failed to delete {file}: {ex.Message}");
                        }
                    }
                }
                
                // Log the purge event to the new clean log file
                LogInfo("🧹 Log file purged - Starting fresh session");
                LogInfo("=====================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOG PURGE ERROR] Failed to purge log file: {ex.Message}");
                LogError($"Failed to purge log file: {ex.Message}");
            }
        }

        public static void PurgeAllLogs()
        {
            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var logsDirectory = Path.Combine(baseDirectory, "logs");
                
                if (Directory.Exists(logsDirectory))
                {
                    // More safely delete only known log file patterns, to avoid deleting user files.
                    var logFiles = Directory.GetFiles(logsDirectory, "*.txt")
                        .Where(file => {
                            var fileName = Path.GetFileName(file);
                            return fileName.StartsWith("logs", StringComparison.OrdinalIgnoreCase) ||
                                   fileName.StartsWith("instance_", StringComparison.OrdinalIgnoreCase) ||
                                   fileName.StartsWith("log-", StringComparison.OrdinalIgnoreCase);
                        });
                    
                    foreach (var file in logFiles)
                    {
                        try
                        {
                            File.Delete(file);
                            Console.WriteLine($"[LOG PURGE] Deleted: {file}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[LOG PURGE ERROR] Failed to delete {file}: {ex.Message}");
                        }
                    }
                }
                
                Console.WriteLine($"[LOG PURGE] ✅ All log files purged - Starting fresh session");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOG PURGE ERROR] Failed to purge all logs: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Console.WriteLine("[LogService] Disposed");
            }
        }
    }

    public class PerformanceTimer : IDisposable
    {
        private readonly LogService _logger;
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;

        public PerformanceTimer(LogService logger, string operationName)
        {
            _logger = logger;
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            try
            {
                _logger.LogInfo($"Performance: {_operationName} completed in {_stopwatch.ElapsedMilliseconds}ms",
                              category: "Performance");
            }
            catch (ObjectDisposedException)
            {
                // Logger was disposed, just ignore the performance log
            }
        }
    }
} 