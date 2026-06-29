using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Service responsible for managing march availability and deployment
    /// </summary>
    public interface IMarchManagementService
    {
        /// <summary>
        /// Gets the current number of available marches
        /// </summary>
        Task<int> GetAvailableMarchesAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken);

        /// <summary>
        /// Waits for marches to become available, with timeout and stamina checking
        /// </summary>
        Task<MarchAvailabilityResult> WaitForAvailableMarchesAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken);

        /// <summary>
        /// Checks if max march limit has been reached
        /// </summary>
        Task<bool> CheckMaxMarchAsync(
            AccountSettings account,
            LogService logger);

        /// <summary>
        /// Finds and clicks the quick-deploy button if available
        /// </summary>
        Task<bool> FindAndClickQuickDeployAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken);
    }
}