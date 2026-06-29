using System;

namespace Bot.Core.Models
{
    /// <summary>
    /// Comprehensive instance status information obtained from LDPlayer's list2 command.
    /// Provides detailed state information for better diagnosis and faster status checks.
    /// </summary>
    public class InstanceDetailedStatus
    {
        /// <summary>
        /// The instance index number (0, 1, 2, etc.)
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// The instance title/name as shown in LDPlayer
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Top window handle for the instance
        /// </summary>
        public int TopWindowHandle { get; set; }

        /// <summary>
        /// Bind window handle for the instance
        /// </summary>
        public int BindWindowHandle { get; set; }

        /// <summary>
        /// Whether Android OS has fully started (1 = started, 0 = not started)
        /// </summary>
        public bool AndroidStarted { get; set; }

        /// <summary>
        /// Process ID of the emulator instance
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// VirtualBox process ID
        /// </summary>
        public int VBoxProcessId { get; set; }

        /// <summary>
        /// When this status information was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Indicates if the emulator process is running (ProcessId > 0)
        /// </summary>
        public bool IsRunning => ProcessId > 0;

        /// <summary>
        /// Indicates if the instance is fully booted and ready for ADB commands
        /// </summary>
        public bool IsFullyBooted => IsRunning && AndroidStarted;

        /// <summary>
        /// Indicates if the instance has valid window handles for UI interaction
        /// </summary>
        public bool HasValidHandles => TopWindowHandle > 0 && BindWindowHandle > 0;

        /// <summary>
        /// Gets a descriptive status string for logging and diagnostics
        /// </summary>
        public string StatusDescription
        {
            get
            {
                if (!IsRunning)
                    return "Not Running";
                if (!AndroidStarted)
                    return "Emulator Running, Android Starting";
                if (!HasValidHandles)
                    return "Android Started, UI Not Ready";
                return "Fully Ready";
            }
        }

        /// <summary>
        /// Returns a comprehensive string representation of the instance status
        /// </summary>
        public override string ToString()
        {
            return $"Instance {Index} ({Title}): {StatusDescription} | PID: {ProcessId} | VBox: {VBoxProcessId} | " +
                   $"Windows: {TopWindowHandle}/{BindWindowHandle} | Updated: {LastUpdated:HH:mm:ss}";
        }

        /// <summary>
        /// Creates an InstanceDetailedStatus from parsed list2 output line
        /// </summary>
        /// <param name="parts">Split CSV parts from list2 output</param>
        /// <returns>InstanceDetailedStatus or null if parsing fails</returns>
        public static InstanceDetailedStatus? FromList2Parts(string[] parts)
        {
            if (parts.Length < 7)
                return null;

            try
            {
                return new InstanceDetailedStatus
                {
                    Index = int.Parse(parts[0].Trim()),
                    Title = parts[1].Trim(),
                    TopWindowHandle = int.Parse(parts[2].Trim()),
                    BindWindowHandle = int.Parse(parts[3].Trim()),
                    AndroidStarted = parts[4].Trim() == "1",
                    ProcessId = int.Parse(parts[5].Trim()),
                    VBoxProcessId = int.Parse(parts[6].Trim()),
                    LastUpdated = DateTime.UtcNow
                };
            }
            catch
            {
                // Return null if any parsing fails
                return null;
            }
        }

        /// <summary>
        /// Determines if this status indicates the instance needs recovery
        /// </summary>
        public bool NeedsRecovery
        {
            get
            {
                // Instance is running but Android hasn't started for too long
                if (IsRunning && !AndroidStarted)
                {
                    var timeSinceUpdate = DateTime.UtcNow - LastUpdated;
                    return timeSinceUpdate > TimeSpan.FromMinutes(5);
                }

                // Instance has handles but process is dead
                return HasValidHandles && !IsRunning;
            }
        }

        /// <summary>
        /// Gets the readiness level of the instance for task execution
        /// </summary>
        public InstanceReadiness ReadinessLevel
        {
            get
            {
                if (!IsRunning)
                    return InstanceReadiness.NotRunning;
                if (!AndroidStarted)
                    return InstanceReadiness.Booting;
                if (!HasValidHandles)
                    return InstanceReadiness.Starting;
                return InstanceReadiness.Ready;
            }
        }
    }

    /// <summary>
    /// Enumeration of instance readiness levels
    /// </summary>
    public enum InstanceReadiness
    {
        /// <summary>
        /// Instance is not running
        /// </summary>
        NotRunning,

        /// <summary>
        /// Instance is running but Android OS is still booting
        /// </summary>
        Booting,

        /// <summary>
        /// Android is started but UI components are still initializing
        /// </summary>
        Starting,

        /// <summary>
        /// Instance is fully ready for task execution
        /// </summary>
        Ready
    }
}