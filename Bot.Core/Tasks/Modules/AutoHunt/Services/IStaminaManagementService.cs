using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using System.Drawing;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Service responsible for stamina detection and handling
    /// </summary>
    public interface IStaminaManagementService
    {
        /// <summary>
        /// Checks for low or depleted stamina conditions
        /// </summary>
        Task<StaminaCheckResult> CheckForStaminaLowAsync(
            byte[]? screenshot,
            Rectangle staminaLowArea,
            AccountSettings account,
            LogService logger);

        /// <summary>
        /// Handles stamina depletion by attempting to claim or return
        /// </summary>
        Task<bool> HandleStaminaDepletionAsync(
            AccountSettings account,
            LogService logger);

        /// <summary>
        /// Checks if stamina is available in the designated area
        /// </summary>
        Task<bool> IsStaminaAvailableAsync(
            AccountSettings account,
            LogService logger);
    }
}