using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.IO;

namespace Bot.Core.Tasks.Modules
{
    public class ResidentWelcomeTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ResidentWelcome;
        public override string Name => "Resident Welcome";

        private LocatorService? _locatorService;
        
        // Tooltip for the UI
        public string ToolTip => "Welcomes in new residents";

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            _instanceLogger = logger;
            await base.OnInitializeAsync(logger, cancellationToken);
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            bool isReRun = false, 
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting Resident Welcome task");

                // Ensure we're in base view
                _locatorService = new LocatorService(logger, account, TaskType.ResidentWelcome);
                await _locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber);
                logger.LogInfo($"[{account.AccountName}] Ensured base view for resident welcome");

                // Execute zoom out to ensure proper visibility of resident icons
                logger.LogInfo($"[{account.AccountName}] Executing zoom out for better resident visibility");
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);
                var zoomSuccess = await instanceManager.ZoomOutAsync(account.InstanceNumber, cancellationToken);
                if (zoomSuccess)
                {
                    logger.LogInfo($"[{account.AccountName}] Zoom out executed successfully");
                    // Wait a moment for the zoom animation to complete
                    await Task.Delay(1000, cancellationToken);
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] Zoom out execution failed, continuing without zoom");
                }

                // Look for resident icon for 3 seconds
                logger.LogInfo($"[{account.AccountName}] Looking for resident icon...");
                bool residentFound = false;
                DateTime startTime = DateTime.UtcNow;
                
                while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !residentFound)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");

                    if (await FindAndClickImageAsync("resident.png", account.InstanceNumber, logger, threshold: 0.6))
                    {
                        residentFound = true;
                        logger.LogInfo($"[{account.AccountName}] Found and clicked resident icon");
                        break;
                    }

                    await Task.Delay(500, cancellationToken); // Check every 0.5 seconds
                }

                if (!residentFound)
                {
                    logger.LogInfo($"[{account.AccountName}] No resident icon found, moving to next module");
                    return new TaskExecutionDetails(true, message: "No residents to welcome");
                }

                // Look for welcome-in button for 3 seconds
                logger.LogInfo($"[{account.AccountName}] Looking for welcome-in button...");
                bool welcomeInFound = false;
                startTime = DateTime.UtcNow;
                
                while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !welcomeInFound)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");

                    if (await FindAndClickImageAsync("welcome-in.png", account.InstanceNumber, logger, threshold: 0.6))
                    {
                        welcomeInFound = true;
                        logger.LogInfo($"[{account.AccountName}] Found and clicked welcome-in button");
                        break;
                    }

                    await Task.Delay(500, cancellationToken); // Check every 0.5 seconds
                }

                if (!welcomeInFound)
                {
                    logger.LogWarning($"[{account.AccountName}] Welcome-in button not found after clicking resident icon");
                    return TaskExecutionDetails.Failed("Welcome-in button not found");
                }

                logger.LogInfo($"[{account.AccountName}] Resident Welcome task completed successfully");
                return new TaskExecutionDetails(true, message: "Residents welcomed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Resident Welcome task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Task failed: {ex.Message}");
            }
        }




        protected override string GetImageFolderName()
        {
            return "residentcollect";
        }
    }
}