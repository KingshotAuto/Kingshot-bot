using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Service responsible for detecting and prioritizing hunt targets
    /// </summary>
    public interface ITargetDetectionService
    {
        /// <summary>
        /// Detects all available hunt targets on the current screen
        /// </summary>
        Task<List<HuntTarget>> DetectAllTargetsAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken);

        /// <summary>
        /// Prioritizes detected targets based on session state and available marches
        /// </summary>
        HuntTarget? PrioritizeTarget(
            List<HuntTarget> targets,
            TargetPrioritizationContext context,
            out bool noMarchesAvailable);

        /// <summary>
        /// Checks if a target area is blocked (already used recently)
        /// </summary>
        bool IsTargetAreaBlocked(HuntTarget target, string accountId);
    }
}