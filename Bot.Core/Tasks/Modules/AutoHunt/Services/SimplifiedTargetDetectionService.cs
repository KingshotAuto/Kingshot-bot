using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Simplified implementation of target detection service
    /// The actual detection logic is handled by the coordinator
    /// </summary>
    public class SimplifiedTargetDetectionService : ITargetDetectionService
    {
        public async Task<List<HuntTarget>> DetectAllTargetsAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken)
        {
            // This will be implemented by the coordinator
            return await Task.FromResult(new List<HuntTarget>());
        }

        public HuntTarget? PrioritizeTarget(
            List<HuntTarget> targets,
            TargetPrioritizationContext context,
            out bool noMarchesAvailable)
        {
            noMarchesAvailable = false;
            bool foundTargetButNoMarches = false;

            // Select targets by highest confidence instead of fixed priority order
            var targetsOrderedByConfidence = targets.OrderByDescending(t => t.Confidence).ToList();

            foreach (var target in targetsOrderedByConfidence)
            {
                // Check if target is blocked by session state
                if (IsTargetBlockedBySessionState(target, context.SessionState))
                {
                    continue;
                }

                // Check march requirements
                if (target.RequiresMarch && context.AvailableMarches <= 0)
                {
                    foundTargetButNoMarches = true;
                    continue;
                }

                return target;
            }

            noMarchesAvailable = foundTargetButNoMarches;
            return null;
        }

        public bool IsTargetAreaBlocked(HuntTarget target, string accountId)
        {
            // This will be handled by the coordinator
            return false;
        }

        private bool IsTargetBlockedBySessionState(HuntTarget target, AutoHuntSessionState sessionState)
        {
            return (target.Type == "king" && !sessionState.CanAttackKing) ||
                   (target.Type == "bear" && !sessionState.CanAttackBear) ||
                   (target.Type == "attack" && !sessionState.CanAttackAttack);
        }
    }
}