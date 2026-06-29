using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Bot.Core.Models
{
    public class FarmingSettings
    {
        /// <summary>
        /// Maximum number of marches to send farming. 0 means unlimited (as many as possible).
        /// </summary>
        [JsonPropertyName("maxFarmingMarches")]
        public int MaxFarmingMarches { get; set; } = 0;

        /// <summary>
        /// Static cache to track marches sent per instance to persist across task runs.
        /// Key: InstanceNumber, Value: Number of marches sent in current session
        /// </summary>
        private static readonly ConcurrentDictionary<int, int> _marchesSentCache = new();

        /// <summary>
        /// Get the number of marches already sent for this instance in the current session.
        /// </summary>
        public static int GetMarchesSent(int instanceNumber)
        {
            return _marchesSentCache.GetOrAdd(instanceNumber, 0);
        }

        /// <summary>
        /// Increment the march count for the specified instance.
        /// </summary>
        public static void IncrementMarchesSent(int instanceNumber)
        {
            _marchesSentCache.AddOrUpdate(instanceNumber, 1, (key, value) => value + 1);
        }

        /// <summary>
        /// Reset the march count for the specified instance (called when instance shuts down).
        /// </summary>
        public static void ResetMarchesSent(int instanceNumber)
        {
            _marchesSentCache.TryRemove(instanceNumber, out _);
        }

        /// <summary>
        /// Check if the maximum march limit has been reached for this instance.
        /// Returns false if MaxFarmingMarches is 0 (unlimited).
        /// </summary>
        public bool HasReachedMarchLimit(int instanceNumber)
        {
            if (MaxFarmingMarches == 0) return false; // 0 means unlimited
            
            var marchesSent = GetMarchesSent(instanceNumber);
            return marchesSent >= MaxFarmingMarches;
        }

        /// <summary>
        /// Get the number of remaining marches for this instance.
        /// Returns -1 if unlimited (MaxFarmingMarches is 0).
        /// </summary>
        public int GetRemainingMarches(int instanceNumber)
        {
            if (MaxFarmingMarches == 0) return -1; // Unlimited
            
            var marchesSent = GetMarchesSent(instanceNumber);
            return Math.Max(0, MaxFarmingMarches - marchesSent);
        }
    }
}