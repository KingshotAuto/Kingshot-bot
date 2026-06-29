using System;
using System.Drawing;
using Bot.Core.Models;

namespace Bot.Core.Tasks.Modules.AutoHunt.Models
{
    /// <summary>
    /// Represents a target found during the hunt process
    /// </summary>
    public class HuntTarget
    {
        public string Type { get; set; } = string.Empty;
        public bool RequiresMarch { get; set; }
        public Rectangle MatchLocation { get; set; }
        public Rectangle TargetArea { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Information about a detected target during scanning
    /// </summary>
    public class TargetInfo
    {
        public string Type { get; set; } = string.Empty;
        public Rectangle TargetArea { get; set; }
        public double Confidence { get; set; }
    }


    /// <summary>
    /// Result of stamina checking operations
    /// </summary>
    public enum StaminaCheckResult
    {
        NoStaminaIssue,
        StaminaDepleted,
        StaminaClaimed_Retry
    }

    /// <summary>
    /// Target processing result
    /// </summary>
    public class TargetProcessResult
    {
        public bool Success { get; set; }
        public bool StaminaDepleted { get; set; }
        public string Message { get; set; } = string.Empty;

        public TargetProcessResult(bool success, bool staminaDepleted, string message = "")
        {
            Success = success;
            StaminaDepleted = staminaDepleted;
            Message = message;
        }

        public static TargetProcessResult Successful(string message = "") => new(true, false, message);
        public static TargetProcessResult Failed(string message = "") => new(false, false, message);
        public static TargetProcessResult StaminaEmpty(string message = "") => new(false, true, message);
    }

    /// <summary>
    /// March availability result
    /// </summary>
    public class MarchAvailabilityResult
    {
        public int AvailableMarches { get; set; }
        public bool WaitRequired { get; set; }
        public bool StaminaDepleted { get; set; }
        public string Message { get; set; } = string.Empty;

        public MarchAvailabilityResult(int availableMarches, bool waitRequired = false, bool staminaDepleted = false, string message = "")
        {
            AvailableMarches = availableMarches;
            WaitRequired = waitRequired;
            StaminaDepleted = staminaDepleted;
            Message = message;
        }
    }

    /// <summary>
    /// Target prioritization context
    /// </summary>
    public class TargetPrioritizationContext
    {
        public AutoHuntSessionState SessionState { get; set; }
        public int AvailableMarches { get; set; }
        public string AccountId { get; set; }

        public TargetPrioritizationContext(AutoHuntSessionState sessionState, int availableMarches, string accountId)
        {
            SessionState = sessionState;
            AvailableMarches = availableMarches;
            AccountId = accountId;
        }
    }

}