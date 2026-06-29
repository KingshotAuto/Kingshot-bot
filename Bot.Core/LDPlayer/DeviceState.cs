using System;
using System.Collections.Generic;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Represents the possible states of an ADB device
    /// </summary>
    public enum DeviceStatus
    {
        /// <summary>
        /// Device is unknown or not initialized
        /// </summary>
        Unknown,
        
        /// <summary>
        /// Device is online and ready for commands
        /// </summary>
        Online,
        
        /// <summary>
        /// Device is offline or disconnected
        /// </summary>
        Offline,
        
        /// <summary>
        /// Device is currently in use by a bot task
        /// </summary>
        Busy,
        
        /// <summary>
        /// Device is online but available for assignment
        /// </summary>
        Available,
        
        /// <summary>
        /// Device is in an error state requiring recovery
        /// </summary>
        Error,
        
        /// <summary>
        /// Device is unauthorized (needs USB debugging permission)
        /// </summary>
        Unauthorized,
        
        /// <summary>
        /// Device is being recovered or restarted
        /// </summary>
        Recovering
    }

    /// <summary>
    /// Comprehensive device state tracking for ADB connections
    /// </summary>
    public class DeviceState
    {
        /// <summary>
        /// The device serial identifier (e.g., "emulator-5554")
        /// </summary>
        public string DeviceSerial { get; set; } = string.Empty;
        
        /// <summary>
        /// The LDPlayer instance number (0, 1, 2, etc.)
        /// </summary>
        public int InstanceNumber { get; set; }
        
        /// <summary>
        /// Current status of the device
        /// </summary>
        public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
        
        /// <summary>
        /// Account name currently assigned to this device (if any)
        /// </summary>
        public string? AssignedAccountName { get; set; }
        
        /// <summary>
        /// Last successful command timestamp
        /// </summary>
        public DateTime LastSuccessfulCommand { get; set; } = DateTime.MinValue;
        
        /// <summary>
        /// Last error timestamp
        /// </summary>
        public DateTime LastError { get; set; } = DateTime.MinValue;
        
        /// <summary>
        /// Last error message
        /// </summary>
        public string? LastErrorMessage { get; set; }
        
        /// <summary>
        /// Number of consecutive failures
        /// </summary>
        public int ConsecutiveFailures { get; set; } = 0;
        
        /// <summary>
        /// Average response time for commands (in milliseconds)
        /// </summary>
        public double AverageResponseTime { get; set; } = 0.0;
        
        /// <summary>
        /// Total commands executed on this device
        /// </summary>
        public long TotalCommands { get; set; } = 0;
        
        /// <summary>
        /// Total successful commands executed on this device
        /// </summary>
        public long SuccessfulCommands { get; set; } = 0;
        
        /// <summary>
        /// Device health score (0-100)
        /// </summary>
        public double HealthScore { get; set; } = 100.0;
        
        /// <summary>
        /// When the device was last checked for availability
        /// </summary>
        public DateTime LastHealthCheck { get; set; } = DateTime.MinValue;
        
        /// <summary>
        /// Whether the device is currently being recovered
        /// </summary>
        public bool IsRecovering { get; set; } = false;
        
        /// <summary>
        /// Number of recovery attempts performed
        /// </summary>
        public int RecoveryAttempts { get; set; } = 0;
        
        /// <summary>
        /// When the device was first discovered
        /// </summary>
        public DateTime FirstDiscovered { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Process ID of the emulator instance (if known)
        /// </summary>
        public int ProcessId { get; set; } = 0;
        
        /// <summary>
        /// Window handle for UI interaction (if known)
        /// </summary>
        public int WindowHandle { get; set; } = 0;
        
        /// <summary>
        /// Additional metadata about the device
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>
        /// Calculates the success rate of commands on this device
        /// </summary>
        public double SuccessRate => TotalCommands > 0 ? (double)SuccessfulCommands / TotalCommands * 100.0 : 0.0;
        
        /// <summary>
        /// Indicates if the device is currently available for assignment
        /// </summary>
        public bool IsAvailable => Status == DeviceStatus.Available && !IsRecovering;
        
        /// <summary>
        /// Indicates if the device needs attention (errors, poor health, etc.)
        /// </summary>
        public bool NeedsAttention => Status == DeviceStatus.Error || HealthScore < 50.0 || ConsecutiveFailures > 3;
        
        /// <summary>
        /// Updates the device health score based on recent performance
        /// </summary>
        public void UpdateHealthScore()
        {
            var baseScore = SuccessRate;
            var timeSinceLastSuccess = DateTime.UtcNow - LastSuccessfulCommand;
            var timeSinceLastError = DateTime.UtcNow - LastError;
            
            // Reduce score for old successful commands
            if (timeSinceLastSuccess.TotalMinutes > 30)
            {
                baseScore -= Math.Min(30, timeSinceLastSuccess.TotalMinutes - 30);
            }
            
            // Reduce score for recent errors
            if (timeSinceLastError.TotalMinutes < 10)
            {
                baseScore -= (10 - timeSinceLastError.TotalMinutes) * 5;
            }
            
            // Reduce score for consecutive failures
            baseScore -= ConsecutiveFailures * 10;
            
            // Reduce score for slow response times
            if (AverageResponseTime > 2000) // 2 seconds
            {
                baseScore -= (AverageResponseTime - 2000) / 100;
            }
            
            HealthScore = Math.Max(0, Math.Min(100, baseScore));
        }
        
        /// <summary>
        /// Records a successful command execution
        /// </summary>
        public void RecordSuccess(double responseTime)
        {
            TotalCommands++;
            SuccessfulCommands++;
            LastSuccessfulCommand = DateTime.UtcNow;
            ConsecutiveFailures = 0;
            
            // Update average response time
            if (TotalCommands == 1)
            {
                AverageResponseTime = responseTime;
            }
            else
            {
                AverageResponseTime = (AverageResponseTime * (TotalCommands - 1) + responseTime) / TotalCommands;
            }
            
            UpdateHealthScore();
        }
        
        /// <summary>
        /// Records a failed command execution
        /// </summary>
        public void RecordFailure(string errorMessage)
        {
            TotalCommands++;
            ConsecutiveFailures++;
            LastError = DateTime.UtcNow;
            LastErrorMessage = errorMessage;
            
            UpdateHealthScore();
        }
        
        /// <summary>
        /// Resets failure counters (typically called after successful recovery)
        /// </summary>
        public void ResetFailures()
        {
            ConsecutiveFailures = 0;
            RecoveryAttempts = 0;
            LastErrorMessage = null;
            UpdateHealthScore();
        }
        
        /// <summary>
        /// Returns a string representation of the device state
        /// </summary>
        public override string ToString()
        {
            var account = string.IsNullOrEmpty(AssignedAccountName) ? "Unassigned" : AssignedAccountName;
            return $"Device {DeviceSerial} (Instance {InstanceNumber}): {Status} | {account} | Health: {HealthScore:F1}% | Success: {SuccessRate:F1}%";
        }
    }
}