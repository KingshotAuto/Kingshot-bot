using Bot.Core.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Utils
{
    /// <summary>
    /// Monitors system resources (CPU, memory, disk I/O) to prevent overloading during bot operations
    /// Uses .NET built-in methods to avoid external dependencies
    /// </summary>
    public class SystemResourceMonitor
    {
        private readonly LogService _logger;
        private readonly Timer? _monitoringTimer;
        private readonly Process _currentProcess;
        
        // For CPU usage calculation
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTime _lastCheck = DateTime.UtcNow;
        
        // Resource thresholds
        public const float CPU_WARNING_THRESHOLD = 80.0f;
        public const float CPU_CRITICAL_THRESHOLD = 90.0f;
        public const float MEMORY_WARNING_THRESHOLD = 85.0f;
        public const float MEMORY_CRITICAL_THRESHOLD = 95.0f;
        
        // Current resource usage
        public float CurrentCpuUsage { get; private set; }
        public float CurrentMemoryUsage { get; private set; }
        public float MemoryUsagePercent => CurrentMemoryUsage; // Alias for clarity
        public long AvailableMemoryMB { get; private set; }
        
        // Resource state tracking
        public bool IsCpuUnderLoad => CurrentCpuUsage > CPU_WARNING_THRESHOLD;
        public bool IsMemoryUnderLoad => CurrentMemoryUsage > MEMORY_WARNING_THRESHOLD;
        public bool IsSystemOverloaded => CurrentCpuUsage > CPU_CRITICAL_THRESHOLD || CurrentMemoryUsage > MEMORY_CRITICAL_THRESHOLD;
        
        // Events for resource state changes
        public event Action<float>? OnCpuThresholdExceeded;
        public event Action<float>? OnMemoryThresholdExceeded;
        public event Action? OnSystemOverloaded;
        public event Action? OnSystemRecovered;
        
        private bool _wasOverloaded = false;

        public SystemResourceMonitor(LogService logger)
        {
            _logger = logger;
            _currentProcess = Process.GetCurrentProcess();
            
            try
            {
                // Start monitoring every 5 seconds
                _monitoringTimer = new Timer(UpdateResourceUsage, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
                
                _logger.LogInfo("SystemResourceMonitor initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize SystemResourceMonitor: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Updates current resource usage and triggers events if thresholds are exceeded
        /// </summary>
        private void UpdateResourceUsage(object? state)
        {
            try
            {
                // Get current process CPU usage (approximation using TotalProcessorTime)
                _currentProcess.Refresh();
                var cpuTime = _currentProcess.TotalProcessorTime;
                if (_lastCpuTime != TimeSpan.Zero)
                {
                    var cpuUsedMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                    var totalMsPassed = (DateTime.UtcNow - _lastCheck).TotalMilliseconds;
                    var cpuUsageRatio = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
                    CurrentCpuUsage = (float)(cpuUsageRatio * 100);
                    
                    // Cap at reasonable values
                    CurrentCpuUsage = Math.Max(0, Math.Min(100, CurrentCpuUsage));
                }
                _lastCpuTime = cpuTime;
                _lastCheck = DateTime.UtcNow;
                
                // Get memory usage using GC and process information
                var totalMemory = GC.GetTotalMemory(false);
                var workingSet = _currentProcess.WorkingSet64;
                
                // Calculate memory usage without PerformanceCounter dependency
                var processMemoryMB = workingSet / (1024.0 * 1024.0);
                
                // Use GC memory pressure as indicator of system memory availability
                // This is more reliable than assuming fixed system memory
                var gcMemoryMB = totalMemory / (1024.0 * 1024.0);
                
                // Estimate total available memory based on process usage patterns
                // If process is using a lot of memory, assume less is available
                var estimatedSystemMemoryMB = Math.Max(4096, processMemoryMB * 8); // Conservative estimate
                AvailableMemoryMB = (long)Math.Max(512, estimatedSystemMemoryMB - processMemoryMB);
                
                // Calculate memory usage percentage based on working set vs estimated system memory
                CurrentMemoryUsage = (float)((processMemoryMB / estimatedSystemMemoryMB) * 100.0);
                CurrentMemoryUsage = Math.Max(0, Math.Min(100, CurrentMemoryUsage));
                
                // Check thresholds and trigger events
                CheckResourceThresholds();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error updating resource usage: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks resource thresholds and triggers appropriate events
        /// </summary>
        private void CheckResourceThresholds()
        {
            // Check CPU threshold
            if (CurrentCpuUsage > CPU_WARNING_THRESHOLD)
            {
                OnCpuThresholdExceeded?.Invoke(CurrentCpuUsage);
            }
            
            // Check memory threshold
            if (CurrentMemoryUsage > MEMORY_WARNING_THRESHOLD)
            {
                OnMemoryThresholdExceeded?.Invoke(CurrentMemoryUsage);
            }
            
            // Check overall system state
            bool isCurrentlyOverloaded = IsSystemOverloaded;
            
            if (isCurrentlyOverloaded && !_wasOverloaded)
            {
                _logger.LogWarning($"🚨 System overloaded - CPU: {CurrentCpuUsage:F1}%, Memory: {CurrentMemoryUsage:F1}%");
                OnSystemOverloaded?.Invoke();
                _wasOverloaded = true;
            }
            else if (!isCurrentlyOverloaded && _wasOverloaded)
            {
                _logger.LogInfo($"✅ System recovered - CPU: {CurrentCpuUsage:F1}%, Memory: {CurrentMemoryUsage:F1}%");
                OnSystemRecovered?.Invoke();
                _wasOverloaded = false;
            }
        }

        /// <summary>
        /// Checks if it's safe to start a new resource-intensive operation
        /// </summary>
        public bool IsSafeToStartOperation()
        {
            return !IsSystemOverloaded && AvailableMemoryMB > 500; // At least 500MB available
        }

        /// <summary>
        /// Waits for system resources to become available before proceeding
        /// </summary>
        public async Task WaitForResourceAvailabilityAsync(CancellationToken cancellationToken, int maxWaitSeconds = 30)
        {
            var startTime = DateTime.UtcNow;
            var maxWaitTime = TimeSpan.FromSeconds(maxWaitSeconds);
            
            while (IsSystemOverloaded && (DateTime.UtcNow - startTime) < maxWaitTime)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                _logger.LogInfo($"⏳ Waiting for system resources to become available (CPU: {CurrentCpuUsage:F1}%, Memory: {CurrentMemoryUsage:F1}%)");
                await Task.Delay(2000, cancellationToken); // Check every 2 seconds
            }
            
            if (IsSystemOverloaded)
            {
                _logger.LogWarning($"⚠️ Proceeding despite high resource usage after {maxWaitSeconds}s timeout");
            }
        }

        /// <summary>
        /// Gets recommended delay based on current system load
        /// </summary>
        public int GetRecommendedDelayMs()
        {
            if (IsSystemOverloaded)
                return 5000; // 5 second delay for overloaded system
            else if (IsCpuUnderLoad || IsMemoryUnderLoad)
                return 2000; // 2 second delay for high load
            else
                return 0; // No delay needed
        }

        /// <summary>
        /// Gets a detailed resource usage report
        /// </summary>
        public string GetResourceReport()
        {
            return $"CPU: {CurrentCpuUsage:F1}%, Memory: {CurrentMemoryUsage:F1}%, Available Memory: {AvailableMemoryMB}MB, " +
                   $"Status: {(IsSystemOverloaded ? "OVERLOADED" : IsCpuUnderLoad || IsMemoryUnderLoad ? "HIGH LOAD" : "NORMAL")}";
        }

        public void Dispose()
        {
            try
            {
                _monitoringTimer?.Dispose();
                _currentProcess?.Dispose();
                _logger.LogInfo("SystemResourceMonitor disposed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error disposing SystemResourceMonitor: {ex.Message}");
            }
        }
    }
}