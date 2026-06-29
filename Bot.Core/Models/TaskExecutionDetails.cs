using System;
using System.Collections.Generic;

namespace Bot.Core.Models
{
    /// <summary>
    /// Categorizes the type of failure for better user guidance
    /// </summary>
    public enum FailureCategory
    {
        /// <summary>ADB, emulator, or network connection issues</summary>
        Connection,
        /// <summary>Template matching or OCR detection failures</summary>
        Detection,
        /// <summary>Cannot reach expected game view/screen</summary>
        Navigation,
        /// <summary>Operation exceeded time limit</summary>
        Timeout,
        /// <summary>Missing or invalid configuration settings</summary>
        Configuration,
        /// <summary>Game is in an unexpected state</summary>
        GameState,
        /// <summary>Unclassified failure</summary>
        Unknown
    }

    public class TaskExecutionDetails
    {
        public bool Success { get; set; }
        public DateTime? NextActionUtc { get; set; } // Used by TroopTrainingTask for pending finish time
        public string? Message { get; set; }
        public bool RecoveryNeeded { get; set; } = false;
        public bool RequiresTaskRestart { get; set; }

        /// <summary>Category of failure for troubleshooting guidance</summary>
        public FailureCategory? FailureCategory { get; set; }

        /// <summary>Which step the task failed at (e.g., "Opening alliance menu")</summary>
        public string? FailedAtStep { get; set; }

        /// <summary>User-facing hint for how to resolve the issue</summary>
        public string? TroubleshootingHint { get; set; }

        /// <summary>
        /// Common troubleshooting hints by failure category
        /// </summary>
        public static readonly Dictionary<FailureCategory, string> CommonHints = new()
        {
            { Models.FailureCategory.Connection, "Restart LDPlayer or check ADB connection" },
            { Models.FailureCategory.Detection, "Ensure game is fully loaded and resolution is 960x540" },
            { Models.FailureCategory.Navigation, "Game may be stuck - recovery will be attempted" },
            { Models.FailureCategory.Timeout, "Game may be slow - check system resources" },
            { Models.FailureCategory.Configuration, "Check task settings for this account" },
            { Models.FailureCategory.GameState, "Close any open dialogs in game and retry" },
            { Models.FailureCategory.Unknown, "Check logs for details or restart the bot" }
        };

        public TaskExecutionDetails(bool success, DateTime? nextActionUtc = null, string? message = null, bool recoveryNeeded = false, bool requiresTaskRestart = false)
        {
            Success = success;
            NextActionUtc = nextActionUtc;
            Message = message;
            RecoveryNeeded = recoveryNeeded;
            RequiresTaskRestart = requiresTaskRestart;
        }

        // Helper for simple success/failure
        public static TaskExecutionDetails Succeeded(string message = "") => new TaskExecutionDetails(true, message: message);
        public static TaskExecutionDetails Failed(string message) => new TaskExecutionDetails(false, message: message);
        public static TaskExecutionDetails FailedAndNeedsRecovery(string message) => new TaskExecutionDetails(false, message: message, recoveryNeeded: true);

        /// <summary>
        /// Create a categorized failure with automatic troubleshooting hint
        /// </summary>
        public static TaskExecutionDetails FailedWith(
            FailureCategory category,
            string step,
            string message,
            string? customHint = null,
            bool recoveryNeeded = false)
        {
            var hint = customHint ?? (CommonHints.TryGetValue(category, out var defaultHint) ? defaultHint : null);

            return new TaskExecutionDetails(false, message: message, recoveryNeeded: recoveryNeeded)
            {
                FailureCategory = category,
                FailedAtStep = step,
                TroubleshootingHint = hint
            };
        }

        /// <summary>
        /// Get a formatted user-friendly error message
        /// </summary>
        public string GetUserMessage()
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(FailedAtStep))
                parts.Add(FailedAtStep);

            if (!string.IsNullOrEmpty(Message))
                parts.Add(Message);

            if (!string.IsNullOrEmpty(TroubleshootingHint))
                parts.Add(TroubleshootingHint);

            return string.Join(" - ", parts);
        }
    }
} 