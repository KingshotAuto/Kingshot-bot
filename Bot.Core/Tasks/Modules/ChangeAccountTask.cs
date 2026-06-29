using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Concurrent;

namespace Bot.Core.Tasks.Modules
{
    public class ChangeAccountTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ChangeAccount;
        public override string Name => "Change Account";

        // Track which instances have already had their accounts changed
        private static readonly ConcurrentDictionary<int, bool> _instancesProcessed = new();

        // UI element coordinates
        private static readonly Point ProfileButtonPoint = new Point(40, 41);
        private static readonly Rectangle SettingsButtonArea = new Rectangle(560, 1221, 117, 38);  // 560,1221 to 677,1259
        private static readonly Rectangle CharactersButtonArea = new Rectangle(53, 293, 73, 72);   // 53,293 to 126,365
        private static readonly Rectangle TickBox1Area = new Rectangle(500, 464, 113, 100);        // 500,464 to 613,564
        private static readonly Rectangle TickBox2Area = new Rectangle(544, 645, 64, 59);          // 544,645 to 608,704

        protected override string GetImageFolderName() => "ChangeAccount";

        public override async Task<bool> CanExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default)
        {
            // This task only runs once per instance per application session
            if (_instancesProcessed.TryGetValue(account.InstanceNumber, out bool processed) && processed)
            {
                logger.LogInfo($"[{account.AccountName}] Change Account task already executed for instance {account.InstanceNumber}. Skipping.");
                return false;
            }
            
            // Add a small delay to make it truly async
            await Task.Delay(1, cancellationToken);
            
            return true;
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
                // Double-check we haven't processed this instance (thread safety)
                if (_instancesProcessed.TryGetValue(account.InstanceNumber, out bool processed) && processed)
                {
                    return new TaskExecutionDetails(true, message: "Account already changed for this instance");
                }

                logger.LogInfo($"[{account.AccountName}] Starting account change sequence...");

                // Step 1: Ensure we're in a known view
                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber);

                // Step 2: Click profile button
                logger.LogInfo($"[{account.AccountName}] Clicking profile button...");
                if (!await ClickAsync(account.InstanceNumber, logger, ProfileButtonPoint))
                {
                    return TaskExecutionDetails.Failed("Failed to click profile button");
                }
                await Task.Delay(1000, cancellationToken);

                // Step 3: Find and click settings button
                logger.LogInfo($"[{account.AccountName}] Looking for settings button...");
                if (!await FindAndClickImageAsync("settings.png", account.InstanceNumber, logger,
                    threshold: 0.7, searchArea: SettingsButtonArea))
                {
                    logger.LogError($"[{account.AccountName}] Error: Failed to find settings.png, calling locator module for recovery");
                    var settingsLocator = new LocatorService(logger, account);
                    try
                    {
                        await settingsLocator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying settings button detection");
                        
                        // Retry after locator service
                        if (!await FindAndClickImageAsync("settings.png", account.InstanceNumber, logger,
                            threshold: 0.7, searchArea: SettingsButtonArea))
                        {
                            logger.LogError($"[{account.AccountName}] Could not find settings button even after locator recovery");
                            return TaskExecutionDetails.Failed("Could not find settings button after locator recovery");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                        return TaskExecutionDetails.Failed("Could not find settings button");
                    }
                }
                await Task.Delay(1000, cancellationToken);

                // Step 4: Find and click characters button
                logger.LogInfo($"[{account.AccountName}] Looking for characters button...");
                if (!await FindAndClickImageAsync("characters.png", account.InstanceNumber, logger,
                    threshold: 0.7, searchArea: CharactersButtonArea))
                {
                    logger.LogError($"[{account.AccountName}] Error: Failed to find characters.png, calling locator module for recovery");
                    var charactersLocator = new LocatorService(logger, account);
                    try
                    {
                        await charactersLocator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying characters button detection");
                        
                        // Retry after locator service
                        if (!await FindAndClickImageAsync("characters.png", account.InstanceNumber, logger,
                            threshold: 0.7, searchArea: CharactersButtonArea))
                        {
                            logger.LogError($"[{account.AccountName}] Could not find characters button even after locator recovery");
                            return TaskExecutionDetails.Failed("Could not find characters button after locator recovery");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                        return TaskExecutionDetails.Failed("Could not find characters button");
                    }
                }
                await Task.Delay(1000, cancellationToken);

                // Step 5: Find tick location and select other account
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    return TaskExecutionDetails.Failed("Failed to capture screenshot for tick detection");
                }

                var (tickFound1, tickRect1, confidence1) = _templateMatcher.MatchTemplate(
                    screenshot,
                    Path.Combine(ImageTemplateFolder, "tick.png"),
                    account.InstanceNumber,
                    threshold: 0.7,
                    searchArea: TickBox1Area
                );

                var (tickFound2, tickRect2, confidence2) = _templateMatcher.MatchTemplate(
                    screenshot,
                    Path.Combine(ImageTemplateFolder, "tick.png"),
                    account.InstanceNumber,
                    threshold: 0.7,
                    searchArea: TickBox2Area
                );

                if (!tickFound1 && !tickFound2)
                {
                    return TaskExecutionDetails.Failed("Could not find tick indicator on either account");
                }

                // Click the box that doesn't have the tick
                Rectangle boxToClick = tickFound1 ? TickBox2Area : TickBox1Area;
                logger.LogInfo($"[{account.AccountName}] Found tick in box {(tickFound1 ? "1" : "2")} (confidence: {(tickFound1 ? confidence1 : confidence2):F3}), clicking box {(tickFound1 ? "2" : "1")}...");
                
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, boxToClick))
                {
                    return TaskExecutionDetails.Failed("Failed to click account selection box");
                }
                await Task.Delay(1000, cancellationToken);

                // Step 6: Click confirm button
                logger.LogInfo($"[{account.AccountName}] Looking for confirm button...");
                if (!await FindAndClickImageAsync("confirm.png", account.InstanceNumber, logger, threshold: 0.7))
                {
                    return TaskExecutionDetails.Failed("Could not find confirm button");
                }

                // Wait for loading screen
                logger.LogInfo($"[{account.AccountName}] Waiting for loading screen...");
                await Task.Delay(3000, cancellationToken);  // Initial delay to ensure loading screen appears

                // Mark this instance as processed to prevent running again this session
                _instancesProcessed.TryAdd(account.InstanceNumber, true);
                logger.LogInfo($"[{account.AccountName}] Marked instance {account.InstanceNumber} as processed for this session");

                // Clear cached account ID for this instance since we've switched accounts
                AccountDetectionTask.ClearCacheForInstance(account.InstanceNumber);
                logger.LogInfo($"[{account.AccountName}] Cleared cached account ID for instance {account.InstanceNumber}");

                // Signal that tasks need to be restarted from loading screen
                return new TaskExecutionDetails(true, message: "Account changed successfully, restarting tasks...", requiresTaskRestart: true);
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during account change: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during account change: {ex.Message}");
            }
        }

        // Add method to reset instance tracking (useful for testing or manual resets)
        public static void ResetInstanceTracking()
        {
            _instancesProcessed.Clear();
        }

        // Clear tracking for a specific instance (called when instance shuts down)
        public static void ClearInstanceTracking(int instanceNumber)
        {
            _instancesProcessed.TryRemove(instanceNumber, out _);
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            // Ensure image templates directory exists
            if (!Directory.Exists(ImageTemplateFolder))
            {
                Directory.CreateDirectory(ImageTemplateFolder);
            }
            
            // Create a README with instructions for required images
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath, @"
Required Images for Change Account Task:

1. settings.png - The settings button icon
2. characters.png - The characters menu option
3. confirm.png - The confirm button for account switching
4. tick.png - The tick/checkmark icon

Place these images in this folder.
");
            }
            
            return Task.CompletedTask;
        }
    }
} 