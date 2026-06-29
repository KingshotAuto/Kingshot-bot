using System;

namespace Bot.Core.Models
{
    public enum ShieldType
    {
        None,
        TwoHour,
        EightHour,
        TwentyFourHour,
        SeventyTwoHour
    }

    public class AutoShieldSettings
    {
        public bool UseRechargeableShield { get; set; } = true;
        public ShieldType SelectedBackupShield { get; set; } = ShieldType.None;
        public int ShieldOverlapMinutes { get; set; } = 30;
        public DateTime? LastShieldActivatedTime { get; set; }
        public TimeSpan? LastShieldDuration { get; set; }
        public bool VerboseLogging { get; set; } = false;
    }
} 