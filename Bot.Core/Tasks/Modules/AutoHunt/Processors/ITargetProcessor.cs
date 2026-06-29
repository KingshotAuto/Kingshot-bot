using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Processors
{
    /// <summary>
    /// Interface for processing specific types of hunt targets
    /// </summary>
    public interface ITargetProcessor
    {
        /// <summary>
        /// The type of target this processor handles (e.g., "king", "bear", "scout", "attack")
        /// </summary>
        string TargetType { get; }

        /// <summary>
        /// Whether this target type requires a march to be deployed
        /// </summary>
        bool RequiresMarch { get; }

        /// <summary>
        /// Processes the specified target
        /// </summary>
        Task<TargetProcessResult> ProcessAsync(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId);

        /// <summary>
        /// Determines if this processor can handle the target based on session state
        /// </summary>
        bool CanProcess(HuntTarget target, AutoHuntSessionState sessionState);

        /// <summary>
        /// Gets the priority level for this target type (lower numbers = higher priority)
        /// </summary>
        int GetPriority();
    }
}