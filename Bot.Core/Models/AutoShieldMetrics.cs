using System;

namespace Bot.Core.Models
{
    public class AutoShieldMetrics
    {
        public int TotalShieldsApplied { get; set; }
        public int RechargeableShieldsUsed { get; set; }
        public int BackupShieldsUsed { get; set; }
        public TimeSpan AverageRemainingTime { get; set; }
        public int FailedAttempts { get; set; }
        public DateTime LastUpdated { get; set; }

        // Error counters
        public int OcrTimeouts { get; set; }
        public int TemplateNotFoundErrors { get; set; }
        public int ShieldUnavailableErrors { get; set; }
        public int ConfirmationFailedErrors { get; set; }

        public double SuccessRate => 
            TotalShieldsApplied == 0 ? 0 : 
            (double)TotalShieldsApplied / (TotalShieldsApplied + FailedAttempts) * 100;

        public double RechargeableUsageRatio =>
            TotalShieldsApplied == 0 ? 0 :
            (double)RechargeableShieldsUsed / TotalShieldsApplied * 100;
    }
} 