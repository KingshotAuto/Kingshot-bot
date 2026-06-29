using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    public class AutoHealTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoHeal;
        public override string Name => "Auto Heal";
        protected override string GetImageFolderName() => "autoheal";
        
        private new readonly UnifiedTemplateMatchingService _templateMatcher = new UnifiedTemplateMatchingService(new LogService());
        private readonly string _templateFolder;
        private const double TEMPLATE_MATCH_THRESHOLD = 0.65;

        public AutoHealTask(LogService logger)
        {
            _templateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "autoheal");
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
                logger.LogInfo($"[AutoHeal] Starting auto heal process for account {account.AccountName}");

                // Validate template directory exists
                if (!Directory.Exists(_templateFolder))
                {
                    return FailConfiguration("Step 1: Setup", $"Template directory not found: {_templateFolder}");
                }

                // Ensure we're in map view
                NotifyProgress(userNotifications, "Navigating to map", 10);
                var locator = new LocatorService(logger, account);
                if (!await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken))
                {
                    return FailNavigation("Step 2: Navigating to map view", "Could not reach map view");
                }

                var adbController = await ADBMigrationHelper.GetConnectionAsync(account.InstanceNumber, logger, cancellationToken);
                if (adbController == null)
                {
                    return FailConnection("Step 3: Connecting to emulator", "Failed to establish ADB connection");
                }

                // Define healing sequence with specific search areas
                var healSequence = new[]
                {
                    (Path.Combine(_templateFolder, "heal.png"), new Rectangle(523, 1009, 70, 67), "heal icon"),
                    (Path.Combine(_templateFolder, "heal-button.png"), new Rectangle(414, 786, 208, 202), "heal button"),
                    (Path.Combine(_templateFolder, "help-button.png"), new Rectangle(523, 1009, 70, 67), "help button")
                };

                foreach (var (templatePath, searchArea, description) in healSequence)
                {
                    if (!File.Exists(templatePath))
                    {
                        logger.LogError($"[AutoHeal] Template not found: {templatePath}");
                        return TaskExecutionDetails.Failed($"Template not found: {Path.GetFileName(templatePath)}");
                    }

                    // Give each template up to 5 seconds to appear
                    var startTime = DateTime.UtcNow;
                    bool found = false;
                    Rectangle matchRect = Rectangle.Empty;

                    while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !found && !cancellationToken.IsCancellationRequested)
                    {
                        var screenshot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                        var (isFound, rect, confidence) = _templateMatcher.MatchTemplate(
                            screenshot,
                            templatePath,
                            account.InstanceNumber,
                            TEMPLATE_MATCH_THRESHOLD,
                            searchArea: searchArea);

                        if (isFound)
                        {
                            found = true;
                            matchRect = rect;
                            var clickPoint = matchRect.GetCenter();
                            logger.LogInfo($"[AutoHeal] Found {description} at ({clickPoint.X}, {clickPoint.Y}) with confidence {confidence:F3}");
                            await ADBMigrationHelper.TapAsync(adbController, clickPoint.X, clickPoint.Y, logger, cancellationToken);
                            await Task.Delay(500, cancellationToken); // Short delay after click
                            break;
                        }

                        // If it's the first template (heal.png) and we can't find it, exit early
                        if (description == "heal icon" && (DateTime.UtcNow - startTime).TotalSeconds >= 4.5)
                        {
                            logger.LogInfo("[AutoHeal] Heal icon not found, skipping healing process");
                            return TaskExecutionDetails.Succeeded("No healing needed");
                        }

                        await Task.Delay(200, cancellationToken);
                    }

                    if (!found && description != "heal icon")
                    {
                        logger.LogError($"[{account.AccountName}] Error: Failed to find {Path.GetFileName(templatePath)}, calling locator module for recovery");
                        var healLocator = new LocatorService(logger, account);
                        try
                        {
                            await healLocator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
                            logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying {description} detection");
                            
                            // Retry the template matching after locator service
                            var retryStartTime = DateTime.UtcNow;
                            while ((DateTime.UtcNow - retryStartTime).TotalSeconds < 5 && !found && !cancellationToken.IsCancellationRequested)
                            {
                                var retryScreenshot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                                var (retryFound, retryRect, retryConfidence) = _templateMatcher.MatchTemplate(
                                    retryScreenshot,
                                    templatePath,
                                    account.InstanceNumber,
                                    TEMPLATE_MATCH_THRESHOLD,
                                    searchArea: searchArea);

                                if (retryFound)
                                {
                                    found = true;
                                    matchRect = retryRect;
                                    var clickPoint = matchRect.GetCenter();
                                    logger.LogInfo($"[AutoHeal] Found {description} at ({clickPoint.X}, {clickPoint.Y}) with confidence {retryConfidence:F3} after locator recovery");
                                    await ADBMigrationHelper.TapAsync(adbController, clickPoint.X, clickPoint.Y, logger, cancellationToken);
                                    await Task.Delay(500, cancellationToken);
                                    break;
                                }
                                await Task.Delay(200, cancellationToken);
                            }
                            
                            if (!found)
                            {
                                logger.LogError($"[{account.AccountName}] Could not find {description} even after locator recovery");
                                return FailDetection($"Step: Finding {description}", description, recoveryNeeded: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                            return FailDetection($"Step: Finding {description}", description, recoveryNeeded: true);
                        }
                    }
                }

                logger.LogInfo("[AutoHeal] Successfully completed healing sequence");
                return TaskExecutionDetails.Succeeded("Healing completed successfully");
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[AutoHeal] Task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[AutoHeal] Error during healing process: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }
    }
} 