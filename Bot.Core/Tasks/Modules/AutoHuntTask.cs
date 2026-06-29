using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Tasks;
using Bot.Core.Tasks.Modules.AutoHunt;
using Bot.Core.Tasks.Modules.AutoHunt.Services;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Refactored AutoHunt task using the new modular architecture
    /// </summary>
    public class AutoHuntTask : BaseTaskWithCommonPatterns
    {
        private readonly AutoHuntCoordinator _coordinator;

        public override TaskType TaskType => TaskType.AutoHunt;
        public override string Name => "Auto Hunt";

        public AutoHuntTask()
        {
            // Wire up simplified service implementations
            // The actual logic is handled by the coordinator
            var targetDetectionService = new SimplifiedTargetDetectionService();
            var marchManagementService = new SimplifiedMarchManagementService();
            var staminaManagementService = new SimplifiedStaminaManagementService();
            var huntModeNavigationService = new SimplifiedHuntModeNavigationService();
            var visualDebugger = new SimplifiedAutoHuntVisualDebugger();

            // Create the coordinator with all dependencies
            _coordinator = new AutoHuntCoordinator(
                targetDetectionService,
                marchManagementService,
                staminaManagementService,
                huntModeNavigationService,
                visualDebugger);
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            return await _coordinator.ExecuteAutoHuntAsync(
                account, 
                logger, 
                cancellationToken, 
                isReRun, 
                userNotifications);
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override string GetImageFolderName() => "autohunt";

    }
}