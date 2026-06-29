using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace Bot.Core.Tasks.Modules
{
    public class AllianceHelpTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoAllianceHelp;
        public override string Name => "Alliance Help";

        protected override string GetImageFolderName() => "alliance";

        // Property to control number of attempts (can be set before execution)
        public int MaxAttempts { get; set; } = 4; // Default 4 attempts for manual execution

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for alliance help button...");

                // Retry every 0.5 seconds with configurable max attempts
                const int retryIntervalMs = 500;
                var maxAttempts = MaxAttempts;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    // Check for pause at the beginning of each attempt
                    await WaitIfPausedAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");

                    logger.LogInfo($"[{account.AccountName}] Attempt {attempt}/{maxAttempts} - Looking for alliance help button...");

                    // Take screenshot for this attempt
                    var screenshotBytes = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshotBytes == null)
                    {
                        logger.LogWarning($"[{account.AccountName}] Failed to get screenshot on attempt {attempt}");
                        if (attempt < maxAttempts)
                        {
                            await WaitIfPausedAsync(cancellationToken);
                            if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                            await Task.Delay(retryIntervalMs, cancellationToken);
                            continue;
                        }
                        else
                        {
                            logger.LogError($"[{account.AccountName}] Failed to get screenshot after all attempts");
                            return TaskExecutionDetails.Failed("Failed to get screenshot after all attempts");
                        }
                    }

                    // Use enhanced matching with multiple scales for better detection
                    var helpButtonFound = await FindAndClickImageAsync(
                        "alliance-help.png",
                        account.InstanceNumber,
                        logger,
                        threshold: 0.6, // Lower threshold for more lenient matching
                        useEnhancedMatching: true // This will use multiple scales
                    );

                    if (helpButtonFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked alliance help button on attempt {attempt}");
                        await Task.Delay(2000, cancellationToken); // Wait for help to be sent
                        return TaskExecutionDetails.Succeeded();
                    }

                    // If not found and not the last attempt, wait before retrying
                    if (attempt < maxAttempts)
                    {
                        logger.LogInfo($"[{account.AccountName}] Alliance help button not found on attempt {attempt}, retrying in {retryIntervalMs}ms...");
                        await Task.Delay(retryIntervalMs, cancellationToken);
                    }
                }

                logger.LogInfo($"[{account.AccountName}] ℹ️ No alliance help button found after {maxAttempts} attempts");
                return TaskExecutionDetails.Succeeded(); // Still return success as no help button is a valid state
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during alliance help: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during alliance help: {ex.Message}");
            }
        }

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            // Create README if it doesn't exist
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath,
                    "Alliance Help Task - Image Templates\n" +
                    "===============================\n\n" +
                    "Required images:\n\n" +
                    "1. alliance-help.png - The alliance help button that appears when help is available\n\n" +
                    "Image Requirements:\n" +
                    "- Must be clear, high-contrast screenshots\n" +
                    "- Should include some surrounding context\n" +
                    "- Minimum size: 50x50 pixels\n" +
                    "- Maximum size: 200x200 pixels\n" +
                    "- Format: PNG with transparency where applicable\n\n" +
                    "Notes:\n" +
                    "- This task runs after every other task\n" +
                    "- Uses enhanced matching with multiple scales for better detection\n" +
                    "- Quick and lightweight to not impact other tasks\n",
                    cancellationToken);
            }
        }
    }
} 