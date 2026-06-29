using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Service responsible for hunt mode navigation and UI management
    /// </summary>
    public interface IHuntModeNavigationService
    {
        /// <summary>
        /// Enters hunt mode by clicking the hunt button
        /// </summary>
        Task<bool> EnterHuntModeAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken);

        /// <summary>
        /// Checks if currently in hunt mode
        /// </summary>
        Task<bool> IsInHuntModeAsync(
            AccountSettings account,
            LogService logger);

        /// <summary>
        /// Returns to map view from hunt mode
        /// </summary>
        Task ReturnToMapViewAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken);

        /// <summary>
        /// Handles level-up popups that may appear during hunting
        /// </summary>
        Task<bool> HandleLevelUpPopupAsync(
            AccountSettings account,
            LogService logger);

        /// <summary>
        /// Checks if a target is currently marching (being processed)
        /// </summary>
        Task<bool> IsTargetMarchingAsync(
            AccountSettings account,
            LogService logger);
    }
}