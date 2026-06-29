using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public enum ResourceType
    {
        Bread,
        Wood,
        Stone,
        Iron
        // Add other resource types if needed
    }

    public class FarmingTarget
    {
        [JsonPropertyName("resourceType")]
        public ResourceType ResourceType { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; } = 1; // Default to level 1
    }
} 