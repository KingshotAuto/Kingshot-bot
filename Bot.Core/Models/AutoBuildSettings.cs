using System;

namespace Bot.Core.Models
{
    public class AutoBuildSettings
    {
        public int MaxSpeedupMinutes { get; set; } = 30; // Maximum timer length to use speedups on (in minutes)
        public bool EnableAutoSpeedup { get; set; } = true;
        public DateTime? LastBuildTime { get; set; }
    }
} 