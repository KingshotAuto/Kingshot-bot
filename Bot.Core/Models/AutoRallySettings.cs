using System;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public class AutoRallySettings
    {
        [JsonPropertyName("autoJoin")]
        public bool AutoJoin { get; set; } = false;

        [JsonPropertyName("autoJoinMinHours")]
        public double AutoJoinMinHours { get; set; } = 3.0; // Default 3 hours minimum

        [JsonPropertyName("autoJoinCheckIntervalHours")]
        public double AutoJoinCheckIntervalHours { get; set; } = 1.0; // Default 1 hour between checks

        [JsonPropertyName("lastAutoJoinCheck")]
        public DateTime? LastAutoJoinCheck { get; set; } = null; // Track when auto join was last checked
    }
}