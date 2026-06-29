using System;
using System.Text.Json.Serialization;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.Models
{
    public class BlockedArea
    {
        public Rectangle Area { get; set; }
        public DateTime BlockedTime { get; set; }

        public BlockedArea(Rectangle area)
        {
            Area = area;
            BlockedTime = DateTime.UtcNow;
        }
    }

    public class AutoHuntSettings
    {
        [JsonPropertyName("useEqualize")]
        public bool UseEqualize { get; set; } = true;  // Default enabled

        [JsonPropertyName("lastMaxMarchTime")]
        public DateTime? LastMaxMarchTime { get; set; }

        [JsonPropertyName("lastRunTime")]
        public DateTime? LastRunTime { get; set; }

        [JsonPropertyName("lastMarchSentTime")]
        public DateTime? LastMarchSentTime { get; set; }

        private const int BLOCK_EXPIRY_MINUTES = 4;  // 4 minutes to ensure blocks persist through typical hunt cycles
        private const int OVERLAP_PADDING = 15;  // Updated to match the TARGET_AREA_PADDING from AutoHuntTask

        [JsonInclude]
        [JsonPropertyName("usedTargetAreasByAccountId")]
        private Dictionary<string, List<BlockedArea>> UsedTargetAreasByAccountId { get; set; } = new Dictionary<string, List<BlockedArea>>();

        // Backward compatibility with old instance-based storage
        [JsonInclude]
        [JsonPropertyName("usedTargetAreasByInstance")]
        private Dictionary<int, List<BlockedArea>>? UsedTargetAreasByInstance { get; set; }

        public List<Rectangle> GetUsedTargetAreas(string accountId)
        {
            // Migrate old instance-based data if present
            MigrateInstanceDataToAccountId(accountId);

            if (!UsedTargetAreasByAccountId.ContainsKey(accountId))
            {
                UsedTargetAreasByAccountId[accountId] = new List<BlockedArea>();
            }

            // Clean expired blocks before returning
            CleanExpiredBlocks(accountId);

            return UsedTargetAreasByAccountId[accountId].Select(b => b.Area).ToList();
        }

        private void MigrateInstanceDataToAccountId(string accountId)
        {
            // Only migrate if we have old data and no new data
            if (UsedTargetAreasByInstance != null && !UsedTargetAreasByAccountId.ContainsKey(accountId))
            {
                // Find instance data that might belong to this account (we can't perfectly map, so we'll migrate all)
                foreach (var instanceData in UsedTargetAreasByInstance)
                {
                    if (instanceData.Value.Any())
                    {
                        UsedTargetAreasByAccountId[accountId] = instanceData.Value;
                        break; // Take the first non-empty instance data
                    }
                }
            }
        }

        private void CleanExpiredBlocks(string accountId)
        {
            if (UsedTargetAreasByAccountId.ContainsKey(accountId))
            {
                var now = DateTime.UtcNow;
                UsedTargetAreasByAccountId[accountId] = UsedTargetAreasByAccountId[accountId]
                    .Where(block => (now - block.BlockedTime).TotalMinutes < BLOCK_EXPIRY_MINUTES)
                    .ToList();
            }
        }

        public void ClearUsedTargetAreas(string accountId)
        {
            if (UsedTargetAreasByAccountId.ContainsKey(accountId))
            {
                UsedTargetAreasByAccountId[accountId].Clear();
            }
            else
            {
                UsedTargetAreasByAccountId[accountId] = new List<BlockedArea>();
            }
        }

        public void AddUsedTargetArea(string accountId, Rectangle area)
        {
            if (!UsedTargetAreasByAccountId.ContainsKey(accountId))
            {
                UsedTargetAreasByAccountId[accountId] = new List<BlockedArea>();
            }

            // Clean expired blocks before adding new one
            CleanExpiredBlocks(accountId);

            // Check if area overlaps with any existing blocked area
            var existingBlock = UsedTargetAreasByAccountId[accountId]
                .FirstOrDefault(b => 
                    // Check if rectangles overlap with padding
                    !(b.Area.X + b.Area.Width + OVERLAP_PADDING < area.X ||
                      b.Area.X > area.X + area.Width + OVERLAP_PADDING ||
                      b.Area.Y + b.Area.Height + OVERLAP_PADDING < area.Y ||
                      b.Area.Y > area.Y + area.Height + OVERLAP_PADDING));

            if (existingBlock == null)
            {
                UsedTargetAreasByAccountId[accountId].Add(new BlockedArea(area));
            }
        }

        public void RemoveUsedTargetArea(string accountId, Rectangle area)
        {
            if (UsedTargetAreasByAccountId.ContainsKey(accountId))
            {
                UsedTargetAreasByAccountId[accountId] = UsedTargetAreasByAccountId[accountId]
                    .Where(block => 
                        // Keep blocks that don't overlap with the area to remove
                        block.Area.X + block.Area.Width + OVERLAP_PADDING < area.X ||
                        block.Area.X > area.X + area.Width + OVERLAP_PADDING ||
                        block.Area.Y + block.Area.Height + OVERLAP_PADDING < area.Y ||
                        block.Area.Y > area.Y + area.Height + OVERLAP_PADDING)
                    .ToList();
            }
        }

        // Legacy methods for backward compatibility - delegate to account-based methods
        public List<Rectangle> GetUsedTargetAreas(int instanceNumber) => GetUsedTargetAreas($"Instance_{instanceNumber}");
        public void ClearUsedTargetAreas(int instanceNumber) => ClearUsedTargetAreas($"Instance_{instanceNumber}");
        public void AddUsedTargetArea(int instanceNumber, Rectangle area) => AddUsedTargetArea($"Instance_{instanceNumber}", area);
        public void RemoveUsedTargetArea(int instanceNumber, Rectangle area) => RemoveUsedTargetArea($"Instance_{instanceNumber}", area);
    }

    public class AutoHuntSessionState
    {
        public bool CanAttackKing { get; set; } = true;
        public bool CanAttackBear { get; set; } = true;
        public bool CanAttackAttack { get; set; } = true;
        public int InstanceNumber { get; set; }
        public DateTime SessionStartTime { get; set; } = DateTime.UtcNow;
    }
} 