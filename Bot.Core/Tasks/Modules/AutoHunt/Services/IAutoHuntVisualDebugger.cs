using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Services
{
    /// <summary>
    /// Service responsible for creating visual debugging screenshots
    /// </summary>
    public interface IAutoHuntVisualDebugger
    {
        /// <summary>
        /// Creates visual debug screenshots showing blocked and available target areas
        /// </summary>
        Task CreateVisualBlockingLogAsync(
            byte[] screenshotBytes,
            AccountSettings account,
            LogService logger,
            List<TargetInfo>? foundTargets = null,
            List<Rectangle>? blockedAreas = null,
            string action = "");

        /// <summary>
        /// Creates debug screenshots for target detection results
        /// </summary>
        Task CreateTargetDetectionLogAsync(
            byte[] screenshotBytes,
            AccountSettings account,
            LogService logger,
            List<HuntTarget> detectedTargets,
            string action = "");

        /// <summary>
        /// Creates debug screenshots for stamina check results
        /// </summary>
        Task CreateStaminaDebugLogAsync(
            byte[] screenshotBytes,
            AccountSettings account,
            LogService logger,
            StaminaCheckResult result,
            Rectangle staminaArea);
    }
}