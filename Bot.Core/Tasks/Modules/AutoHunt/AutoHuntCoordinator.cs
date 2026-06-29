using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using Bot.Core.Tasks.Modules.AutoHunt.Services;
using Bot.Core.Tasks;
using Bot.Core.Models;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using Bot.Core.Utils;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules.AutoHunt
{
    /// <summary>
    /// Coordinator that orchestrates the AutoHunt process using the new modular architecture
    /// </summary>
    public class AutoHuntCoordinator : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoHunt;
        public override string Name => "Auto Hunt Coordinator";

        // Static session state storage - using account ID for better isolation
        private static readonly ConcurrentDictionary<string, AutoHuntSessionState> SessionStates = new();
        private static readonly ConcurrentDictionary<string, bool> SkipMapViewChecks = new();
        private static readonly ConcurrentDictionary<string, HashSet<Rectangle>> PredictionFailedAreas = new();

        // Services
        private readonly ITargetDetectionService _targetDetectionService;
        private readonly IMarchManagementService _marchManagementService;
        private readonly IStaminaManagementService _staminaManagementService;
        private readonly IHuntModeNavigationService _huntModeNavigationService;
        private readonly IAutoHuntVisualDebugger _visualDebugger;

        public AutoHuntCoordinator(
            ITargetDetectionService targetDetectionService,
            IMarchManagementService marchManagementService,
            IStaminaManagementService staminaManagementService,
            IHuntModeNavigationService huntModeNavigationService,
            IAutoHuntVisualDebugger visualDebugger)
        {
            _targetDetectionService = targetDetectionService;
            _marchManagementService = marchManagementService;
            _staminaManagementService = staminaManagementService;
            _huntModeNavigationService = huntModeNavigationService;
            _visualDebugger = visualDebugger;
        }

        public async Task<TaskExecutionDetails> ExecuteAutoHuntAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            bool isReRun = false, 
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Initialize the template matcher if not already done
                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }
                // Get unique account ID for this account
                var accountId = await GetAccountIdAsync(account, logger);
                logger.LogInfo($"[{account.AccountName}] Using account ID: '{accountId}' for blocked area tracking");

                // Ensure map view first
                logger.LogInfo($"[{account.AccountName}] 🧭 Ensuring bot is in MapView for initial march check...");
                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber);

                // Wait for map view to fully stabilize
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(2000, cancellationToken);

                // Check available marches before doing anything else
                int initialMarches = await GetAvailableMarchesDirectly(account, logger, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Initial march check: {initialMarches} marches available");
                if (initialMarches <= 0)
                {
                    return TaskExecutionDetails.Failed("No marches available at start, skipping AutoHunt module");
                }

                // Get settings and clear used areas for this account when starting
                var huntSettings = GetAutoHuntSettings(account);
                huntSettings.ClearUsedTargetAreas(accountId);
                SaveAutoHuntSettings(account, huntSettings);
                logger.LogInfo($"[{account.AccountName}] Cleared used areas for account ID: {accountId}");

                // Get or create session state
                var sessionState = SessionStates.GetOrAdd(accountId, 
                    new AutoHuntSessionState { InstanceNumber = account.InstanceNumber });

                // Initial max march check
                if (await CheckMaxMarchDirectly(account, logger))
                {
                    return TaskExecutionDetails.Failed("Max march detected at start, waiting 2 minutes");
                }

                // Main hunt loop
                int availableMarches = initialMarches;
                bool noMarchesAvailable;
                
                // Outer loop to handle blocked area retries
                while (!cancellationToken.IsCancellationRequested)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) break;

                    // Inner hunt loop
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await WaitIfPausedAsync(cancellationToken);
                        if (cancellationToken.IsCancellationRequested) break;

                        // Check if we are already in hunt mode from a previous action
                        bool isAlreadyInHuntMode = SkipMapViewChecks.GetOrAdd(accountId, false);

                        if (!isAlreadyInHuntMode)
                        {
                            // Check marches at the start of every iteration
                            availableMarches = await WaitForAvailableMarchesDirectly(account, logger, cancellationToken);
                            if (availableMarches == -1)
                            {
                                return new TaskExecutionDetails(true, message: "Stamina depleted while waiting for marches");
                            }
                        }
                        else
                        {
                            logger.LogInfo($"[{account.AccountName}] Already in hunt mode, skipping map view and march wait checks.");
                            availableMarches = await GetAvailableMarchesDirectly(account, logger, cancellationToken);
                            logger.LogInfo($"[{account.AccountName}] Current available marches: {availableMarches}");
                            
                            // Reset the flag for the next iteration
                            SkipMapViewChecks.AddOrUpdate(accountId, false, (_, __) => false);
                        }

                        // Enter hunt mode if not already in it
                        if (!isAlreadyInHuntMode)
                        {
                            if (!await EnterHuntModeDirectly(account, logger, cancellationToken))
                            {
                                return TaskExecutionDetails.Failed("Could not enter hunt mode");
                            }
                            await Task.Delay(2000, cancellationToken);
                        }

                        // Detect targets - directly implemented in coordinator
                        var targets = await DetectAllTargetsDirectly(account, logger, cancellationToken, accountId);
                        if (targets.Any())
                        {
                            var targetsOrderedByConfidence = targets.OrderByDescending(t => t.Confidence).ToList();
                            logger.LogInfo($"[{account.AccountName}] Target selection order by confidence: {string.Join(", ", targetsOrderedByConfidence.Select(t => $"{t.Type}({t.Confidence:F3})"))}");
                        }

                        // Prioritize target
                        var context = new TargetPrioritizationContext(sessionState, availableMarches, accountId);
                        var target = _targetDetectionService.PrioritizeTarget(targets, context, out noMarchesAvailable);

                        if (target == null)
                        {
                            if (noMarchesAvailable)
                            {
                                logger.LogInfo($"[{account.AccountName}] Found targets but no marches available, waiting for marches...");
                                continue;
                            }

                            logger.LogInfo($"[{account.AccountName}] No suitable targets available, returning to map view");
                            await ReturnToMapViewDirectly(account, logger, cancellationToken);
                            break;
                        }

                        // Process the target
                        var result = await ProcessTargetByType(target, sessionState, account, logger, cancellationToken, accountId);
                        if (result.StaminaDepleted)
                        {
                            logger.LogInfo($"[{account.AccountName}] Stamina depleted, ending hunt session");
                            await ReturnToMapViewDirectly(account, logger, cancellationToken);
                            return new TaskExecutionDetails(true, message: "Stamina depleted, moving to next module");
                        }

                        if (result.Success && target.RequiresMarch)
                        {
                            availableMarches--;
                            logger.LogInfo($"[{account.AccountName}] March used. Remaining: {availableMarches}");

                            if (availableMarches <= 0)
                            {
                                logger.LogInfo($"[{account.AccountName}] No more marches available, waiting for marches...");
                                continue;
                            }
                        }
                    }

                    // Analyze blocked areas to determine next action
                    var finalSettings = GetAutoHuntSettings(account);
                    var blockedAreas = finalSettings.GetUsedTargetAreas(accountId);
                    var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
                    
                    if (blockedAreas.Any())
                    {
                        var nonPredictionBlocks = blockedAreas.Where(area => !predictionFailedAreas.Contains(area)).ToList();
                        
                        if (nonPredictionBlocks.Any())
                        {
                            logger.LogInfo($"[{account.AccountName}] 🎯 AutoHunt completed. {nonPredictionBlocks.Count} temporary blocks, {predictionFailedAreas.Count} prediction failures.");
                            logger.LogInfo($"[{account.AccountName}] 📋 Moving to next module (blocked areas will expire individually).");
                            break;
                        }
                        else
                        {
                            logger.LogInfo($"[{account.AccountName}] 🚫 All {predictionFailedAreas.Count} blocked areas are prediction failures. Moving to next module.");
                            break;
                        }
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] 🎯 AutoHunt completed. No blocked areas.");
                        break;
                    }
                }

                return new TaskExecutionDetails(true, message: "AutoHunt completed");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during AutoHunt: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during AutoHunt: {ex.Message}");
            }
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            return await ExecuteAutoHuntAsync(account, logger, cancellationToken, isReRun, userNotifications);
        }

        protected override string GetImageFolderName()
        {
            return "autohunt";
        }

        private async Task<TargetProcessResult> ProcessTargetByType(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            // Block the area immediately when we start processing any target that requires a march
            if (target.RequiresMarch)
            {
                var settings = GetAutoHuntSettings(account);
                settings.AddUsedTargetArea(accountId, target.TargetArea);
                SaveAutoHuntSettings(account, settings);
            }

            // Route to appropriate processor
            return target.Type switch
            {
                "king" => await ProcessKingTarget(target, sessionState, account, logger, cancellationToken, accountId),
                "bear" => await ProcessBearTarget(target, sessionState, account, logger, cancellationToken, accountId),
                "scout" => await ProcessScoutTarget(target, sessionState, account, logger, cancellationToken, accountId),
                "attack" => await ProcessAttackTarget(target, sessionState, account, logger, cancellationToken, accountId),
                _ => TargetProcessResult.Failed($"Unknown target type: {target.Type}")
            };
        }

        #region Target Processors

        private async Task<TargetProcessResult> ProcessKingTarget(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click king
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click king target");
                await Task.Delay(2000, cancellationToken);

                // Click view button
                if (!await FindAndClickImageAsync("view-button.png", account.InstanceNumber, logger))
                {
                    // Check if it's because there's already a march
                    if (await IsTargetMarchingDirectly(account, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] Target already marching - area remains blocked: {target.TargetArea}");
                        // Handle marching target logic here
                        SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                        return TargetProcessResult.Failed("Target already marching");
                    }

                    // Check for stamina icon to see if we're back in hunt mode
                    logger.LogInfo($"[{account.AccountName}] No view button found for king target, checking for stamina icon...");
                    var staminaScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (staminaScreenshot != null)
                    {
                        var (staminaFound, _, _) = _templateMatcher.MatchTemplate(
                            staminaScreenshot,
                            Path.Combine(ImageTemplateFolder, "stamina.png"),
                            account.InstanceNumber,
                            threshold: 0.55,
                            searchArea: AutoHuntConstants.StaminaArea,
                            isUIElement: true
                        );

                        if (staminaFound)
                        {
                            logger.LogInfo($"[{account.AccountName}] Found stamina icon, likely back in hunt mode - will look for targets again");
                            SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                            return TargetProcessResult.Failed("Back in hunt mode");
                        }
                    }

                    return TargetProcessResult.Failed("No view button found");
                }
                await Task.Delay(1500, cancellationToken);

                // Click attack button
                if (!await FindAndClickImageAsync("attack-button.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click attack button");
                await Task.Delay(1000, cancellationToken);

                // Check prediction text before proceeding
                var prediction = await ReadPredictionText(account, logger);
                if (prediction.Contains("not likely to prevail", StringComparison.OrdinalIgnoreCase) ||
                    prediction.Contains("almost certain to fail", StringComparison.OrdinalIgnoreCase) ||
                    prediction.Contains("certain to fail", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInfo($"[{account.AccountName}] King attack predicted to fail - '{prediction}', blocking target area for session");
                    
                    // Track as prediction failure
                    var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
                    predictionFailedAreas.Add(target.TargetArea);
                    
                    // Click deploy-back to return to hunt view
                    if (await FindAndClickImageAsync("deploy-back.png", account.InstanceNumber, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] Clicked deploy-back button to return to hunt view");
                        await Task.Delay(1000, cancellationToken);
                    }
                    
                    return TargetProcessResult.Failed("Prediction indicates failure");
                }

                // Look for deploy button or max march
                if (!await HandleDeployment(account, logger, cancellationToken, accountId))
                {
                    return TargetProcessResult.Failed("Deployment failed");
                }

                // Handle confirmation and stamina checks
                var staminaResult = await HandleConfirmationAndStamina(account, logger, cancellationToken);
                if (staminaResult.StaminaDepleted)
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted during king attack");
                }

                await Task.Delay(2000, cancellationToken);

                // Final max march check
                var marchSettings = GetAutoHuntSettings(account);
                if (await CheckMaxMarchDirectly(account, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected after king deployment");
                }

                return TargetProcessResult.Successful("King target processed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing king target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        private async Task<TargetProcessResult> ProcessBearTarget(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click bear
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click bear target");
                await Task.Delay(1000, cancellationToken);

                // Click view button
                if (!await FindAndClickImageAsync("view-button.png", account.InstanceNumber, logger))
                {
                    // Check if it's because there's already a march
                    if (await IsTargetMarchingDirectly(account, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] Target already marching - area remains blocked: {target.TargetArea}");
                        SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                        return TargetProcessResult.Failed("Target already marching");
                    }

                    // Check for stamina icon to see if we're back in hunt mode
                    logger.LogInfo($"[{account.AccountName}] No view button found for bear target, checking for stamina icon...");
                    var staminaScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (staminaScreenshot != null)
                    {
                        var (staminaFound, _, _) = _templateMatcher.MatchTemplate(
                            staminaScreenshot,
                            Path.Combine(ImageTemplateFolder, "stamina.png"),
                            account.InstanceNumber,
                            threshold: 0.55,
                            searchArea: AutoHuntConstants.StaminaArea,
                            isUIElement: true
                        );

                        if (staminaFound)
                        {
                            logger.LogInfo($"[{account.AccountName}] Found stamina icon, likely back in hunt mode - will look for targets again");
                            SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                            return TargetProcessResult.Failed("Back in hunt mode");
                        }
                    }

                    return TargetProcessResult.Failed("No view button found");
                }
                await Task.Delay(1000, cancellationToken);

                // Click attack button
                if (!await FindAndClickImageAsync("attack-button.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click attack button");
                await Task.Delay(1000, cancellationToken);

                // Check prediction text before equalize
                var initialPrediction = await ReadPredictionText(account, logger);
                if (initialPrediction.Contains("not likely to prevail", StringComparison.OrdinalIgnoreCase) ||
                    initialPrediction.Contains("almost certain to fail", StringComparison.OrdinalIgnoreCase) ||
                    initialPrediction.Contains("certain to fail", StringComparison.OrdinalIgnoreCase))
                {
                    sessionState.CanAttackBear = false;
                    logger.LogInfo($"[{account.AccountName}] Bear attack predicted to fail before equalize - '{initialPrediction}', blocking target area for session");
                    
                    // Track as prediction failure
                    var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
                    predictionFailedAreas.Add(target.TargetArea);
                    
                    // Click deploy-back to return to hunt view
                    if (await FindAndClickImageAsync("deploy-back.png", account.InstanceNumber, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] Clicked deploy-back button to return to hunt view");
                        await Task.Delay(1000, cancellationToken);
                    }
                    
                    return TargetProcessResult.Failed("Prediction indicates failure before equalize");
                }

                // Click equalize (if enabled in settings)
                var huntSettings = GetAutoHuntSettings(account);
                if (huntSettings.UseEqualize)
                {
                    logger.LogInfo($"[{account.AccountName}] Equalize enabled - clicking equalize button");
                    if (!await FindAndClickImageAsync("equalize.png", account.InstanceNumber, logger))
                        return TargetProcessResult.Failed("Failed to click equalize button");
                    await Task.Delay(1000, cancellationToken);
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Equalize disabled - skipping equalize button");
                }

                // Check prediction text after equalize
                var prediction = await ReadPredictionText(account, logger);
                if (prediction.Contains("not likely to prevail", StringComparison.OrdinalIgnoreCase) ||
                    prediction.Contains("almost certain to fail", StringComparison.OrdinalIgnoreCase) ||
                    prediction.Contains("certain to fail", StringComparison.OrdinalIgnoreCase))
                {
                    sessionState.CanAttackBear = false;
                    logger.LogInfo($"[{account.AccountName}] Bear attack predicted to fail after equalize - '{prediction}', blocking target area for session");
                    
                    // Track as prediction failure
                    var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
                    predictionFailedAreas.Add(target.TargetArea);
                    
                    // Click deploy-back to return to hunt view
                    if (await FindAndClickImageAsync("deploy-back.png", account.InstanceNumber, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] Clicked deploy-back button to return to hunt view");
                        await Task.Delay(1000, cancellationToken);
                    }
                    
                    return TargetProcessResult.Failed("Prediction indicates failure after equalize");
                }

                // Click deploy
                if (!await FindAndClickImageAsync("deploy-button.png", account.InstanceNumber, logger, threshold: 0.6, searchArea: AutoHuntConstants.DeployButtonArea))
                    return TargetProcessResult.Failed("Failed to click deploy button");

                // Handle confirmation and stamina checks
                var staminaResult = await HandleConfirmationAndStamina(account, logger, cancellationToken);
                if (staminaResult.StaminaDepleted)
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted during bear attack");
                }

                await Task.Delay(2000, cancellationToken);

                // Final max march check
                var marchSettings = GetAutoHuntSettings(account);
                if (await CheckMaxMarchDirectly(account, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected after bear deployment");
                }

                return TargetProcessResult.Successful("Bear target processed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing bear target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        private async Task<TargetProcessResult> ProcessScoutTarget(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click scout
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click scout target");
                await Task.Delay(1000, cancellationToken);

                // Click view button
                if (!await FindAndClickImageAsync("view-button.png", account.InstanceNumber, logger))
                {
                    // Check for stamina icon to see if we're back in hunt mode
                    logger.LogInfo($"[{account.AccountName}] No view button found for scout target, checking for stamina icon...");
                    var staminaScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (staminaScreenshot != null)
                    {
                        var (staminaFound, _, _) = _templateMatcher.MatchTemplate(
                            staminaScreenshot,
                            Path.Combine(ImageTemplateFolder, "stamina.png"),
                            account.InstanceNumber,
                            threshold: 0.55,
                            searchArea: AutoHuntConstants.StaminaArea,
                            isUIElement: true
                        );

                        if (staminaFound)
                        {
                            logger.LogInfo($"[{account.AccountName}] Found stamina icon, likely back in hunt mode - will look for targets again");
                            SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                            return TargetProcessResult.Failed("Back in hunt mode");
                        }
                    }

                    return TargetProcessResult.Failed("No view button found");
                }
                await Task.Delay(1000, cancellationToken);

                // Click rescue
                if (!await FindAndClickImageAsync("rescue.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click rescue button");

                // Check for stamina
                await Task.Delay(1000, cancellationToken);
                var staminaCheckScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (staminaCheckScreenshot != null)
                {
                    var staminaLowArea = new Rectangle(285, 122, 155, 41);
                    var staminaResult = await CheckForStaminaLowDirectly(staminaCheckScreenshot, staminaLowArea, account, logger);
                    if (staminaResult == StaminaCheckResult.StaminaDepleted)
                    {
                        return TargetProcessResult.StaminaEmpty("Stamina depleted during scout rescue");
                    }
                    else if (staminaResult == StaminaCheckResult.StaminaClaimed_Retry)
                    {
                        logger.LogInfo($"[{account.AccountName}] Stamina claimed, retrying scout rescue");
                    }
                }

                logger.LogInfo($"[{account.AccountName}] No stamina-low detected after rescue, continuing module");
                return TargetProcessResult.Successful("Scout target processed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing scout target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        private async Task<TargetProcessResult> ProcessAttackTarget(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click attack
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click attack target");
                await Task.Delay(1000, cancellationToken);

                // Click view button
                if (!await FindAndClickImageAsync("view-button.png", account.InstanceNumber, logger))
                {
                    // Check if it's because there's already a march
                    if (await IsTargetMarchingDirectly(account, logger))
                    {
                        logger.LogInfo($"[{account.AccountName}] Target already marching - area remains blocked: {target.TargetArea}");
                        SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                        return TargetProcessResult.Failed("Target already marching");
                    }

                    // Check for stamina icon to see if we're back in hunt mode
                    logger.LogInfo($"[{account.AccountName}] No view button found for attack target, checking for stamina icon...");
                    var staminaScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (staminaScreenshot != null)
                    {
                        var (staminaFound, _, _) = _templateMatcher.MatchTemplate(
                            staminaScreenshot,
                            Path.Combine(ImageTemplateFolder, "stamina.png"),
                            account.InstanceNumber,
                            threshold: 0.55,
                            searchArea: AutoHuntConstants.StaminaArea,
                            isUIElement: true
                        );

                        if (staminaFound)
                        {
                            logger.LogInfo($"[{account.AccountName}] Found stamina icon, likely back in hunt mode - will look for targets again");
                            SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                            return TargetProcessResult.Failed("Back in hunt mode");
                        }
                    }

                    return TargetProcessResult.Failed("No view button found");
                }
                await Task.Delay(1000, cancellationToken);

                // Click conquer
                if (!await FindAndClickImageAsync("conquer.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click conquer button");
                await Task.Delay(1000, cancellationToken);

                // Check for stamina
                var staminaCheckScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (staminaCheckScreenshot != null)
                {
                    var staminaLowArea = new Rectangle(285, 122, 155, 41);
                    var staminaResult = await CheckForStaminaLowDirectly(staminaCheckScreenshot, staminaLowArea, account, logger);
                    if (staminaResult == StaminaCheckResult.StaminaDepleted)
                    {
                        return TargetProcessResult.StaminaEmpty("Stamina depleted during attack");
                    }
                    else if (staminaResult == StaminaCheckResult.StaminaClaimed_Retry)
                    {
                        logger.LogInfo($"[{account.AccountName}] Stamina claimed, retrying attack deployment");
                    }
                }

                // Try to find and click quick-deploy
                if (!await FindAndClickQuickDeployDirectly(account, logger, cancellationToken))
                    return TargetProcessResult.Failed("Failed to find quick deploy");
                await Task.Delay(1000, cancellationToken);

                // Click fight
                if (!await FindAndClickImageAsync("fight.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click fight button");

                // Wait for battle result
                await Task.Delay(3000, cancellationToken);

                DateTime startTime = DateTime.UtcNow;
                bool resultFound = false;
                bool wasVictory = false;

                logger.LogInfo($"[{account.AccountName}] Waiting 5 seconds before checking battle result...");
                await Task.Delay(5000, cancellationToken);
                
                logger.LogInfo($"[{account.AccountName}] Now checking for battle result using OCR (up to 20 seconds)...");

                // Battle result text area: 211,360 to 515,454
                var battleResultArea = new Rectangle(211, 360, 304, 94);

                int checkCount = 0;
                while ((DateTime.UtcNow - startTime).TotalSeconds < 20 && !resultFound)
                {
                    checkCount++;
                    var battleScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (battleScreenshot == null) continue;

                    try
                    {
                        var ocrService = new OCRService(logger);
                        var text = ocrService.ExtractTextFromScreenArea(battleScreenshot, battleResultArea);
                        if (!string.IsNullOrEmpty(text))
                        {
                            text = text.ToLower().Trim();
                            logger.LogInfo($"[{account.AccountName}] Battle result OCR check #{checkCount}: '{text}'");

                            // Check for victory text
                            if (text.Contains("victory"))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Victory detected via OCR: '{text}' after {checkCount} checks in {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                                resultFound = true;
                                wasVictory = true;
                                break;
                            }

                            // Check for defeat text
                            if (text.Contains("defeat"))
                            {
                                logger.LogInfo($"[{account.AccountName}] ❌ Defeat detected via OCR: '{text}' after {checkCount} checks in {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                                resultFound = true;
                                wasVictory = false;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[{account.AccountName}] OCR error on check #{checkCount}: {ex.Message}");
                    }

                    // Log progress every 3 checks or every 2 seconds
                    if (checkCount % 3 == 0 || (DateTime.UtcNow - startTime).TotalSeconds >= checkCount * 2)
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        logger.LogInfo($"[{account.AccountName}] Battle result OCR progress: {elapsed:F1}s elapsed, {checkCount} checks completed");
                    }

                    await Task.Delay(500, cancellationToken);
                }

                if (!resultFound)
                {
                    logger.LogError($"[{account.AccountName}] Could not detect battle result via OCR after 20 seconds");
                    return TargetProcessResult.Failed("Could not detect battle result");
                }

                // Click result area
                await ClickRandomInRectAsync(account.InstanceNumber, logger, AutoHuntConstants.VictoryDefeatClickArea);

                if (!wasVictory)
                {
                    sessionState.CanAttackAttack = false;
                    logger.LogInfo($"[{account.AccountName}] Attack target defeated, disabling for this session");
                }

                return wasVictory ? 
                    TargetProcessResult.Successful("Attack target victory") : 
                    TargetProcessResult.Failed("Attack target defeat");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing attack target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Target Detection

        private async Task<List<HuntTarget>> DetectAllTargetsDirectly(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            var targets = new List<HuntTarget>();
            var bestConfidences = new Dictionary<string, List<(Rectangle location, double confidence)>>();

            // Get settings for blocked areas
            var settings = GetAutoHuntSettings(account);
            var blockedAreas = settings.GetUsedTargetAreas(accountId);
            LogBlockedAreas(account, logger, accountId, blockedAreas);

            // First check if we're in hunt mode
            logger.LogInfo($"[{account.AccountName}] Waiting up to 5 seconds for hunt mode indicators...");
            bool huntModeFound = await WaitForHuntMode(account, logger, cancellationToken);

            if (!huntModeFound)
            {
                logger.LogInfo($"[{account.AccountName}] No hunt mode indicators found (stamina or compass), skipping target detection");
                return targets;
            }

            logger.LogInfo($"[{account.AccountName}] Found hunt mode indicators, processing targets...");

            // Process ticks first if we're in hunt mode
            bool foundAnyTick = await ProcessTicks(account, logger, cancellationToken, accountId, settings);

            // Scan for targets
            await ScanForTargets(account, logger, cancellationToken, bestConfidences, huntModeFound);

            // Process the best detections
            targets = ProcessBestDetections(account, logger, bestConfidences, settings, accountId);

            // Handle case where no targets found
            if (!targets.Any())
            {
                await HandleNoTargetsFound(account, logger, cancellationToken, bestConfidences, foundAnyTick);
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] Found {targets.Count} valid targets after scanning");
            }

            // Create visual blocking debug log - DISABLED FOR PERFORMANCE
            // await CreateDebugLog(account, logger, targets, settings, accountId);

            return targets;
        }

        private async Task<bool> WaitForHuntMode(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            bool huntModeFound = false;
            DateTime startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !huntModeFound && !cancellationToken.IsCancellationRequested)
            {
                huntModeFound = await IsInHuntMode(account, logger);
                if (!huntModeFound)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }

            return huntModeFound;
        }

        private async Task<bool> IsInHuntMode(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            // Check for castle-hunt icon
            var (castleHuntFound, _, castleHuntConf) = _templateMatcher.MatchTemplate(
                screenshot,
                Path.Combine(ImageTemplateFolder, "castle-hunt.png"),
                account.InstanceNumber,
                threshold: 0.8,  // High confidence threshold as requested
                searchArea: AutoHuntConstants.TargetSearchArea  // Use full target search area
            );

            logger.LogInfo($"[{account.AccountName}] Castle-hunt icon search: found={castleHuntFound}, confidence={castleHuntConf:F3}, area={AutoHuntConstants.TargetSearchArea}");

            if (castleHuntFound)
            {
                logger.LogInfo($"[{account.AccountName}] Found castle-hunt icon with confidence {castleHuntConf:F3} - confirmed in hunt mode");
                return true;
            }

            return false;
        }

        private async Task<bool> ProcessTicks(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            string accountId, 
            AutoHuntSettings settings)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for ticks to process (scanning up to 3 seconds)...");
            bool foundTick;
            bool foundAnyTick = false;
            DateTime tickScanStart = DateTime.UtcNow;
            TimeSpan timeSinceLastTick = TimeSpan.Zero;

            do
            {
                foundTick = false;
                var tickScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (tickScreenshot == null) break;

                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    tickScreenshot,
                    Path.Combine(ImageTemplateFolder, "tick.png"),
                    account.InstanceNumber,
                    threshold: 0.8,
                    searchArea: AutoHuntConstants.TargetSearchArea
                );

                if (found)
                {
                    foundTick = true;
                    foundAnyTick = true;
                    await ProcessTickFound(account, logger, matchRect, confidence, accountId, settings, cancellationToken);
                    await Task.Delay(500, cancellationToken);
                    tickScanStart = DateTime.UtcNow;
                }
                else
                {
                    await Task.Delay(200, cancellationToken);
                }

                timeSinceLastTick = DateTime.UtcNow - tickScanStart;
            } while ((foundTick || timeSinceLastTick.TotalSeconds < 1.5) && !cancellationToken.IsCancellationRequested);

            if (!foundTick)
            {
                logger.LogInfo($"[{account.AccountName}] No ticks found after scanning for {timeSinceLastTick.TotalSeconds:F1} seconds");
            }

            return foundAnyTick;
        }

        private async Task ProcessTickFound(
            AccountSettings account, 
            LogService logger, 
            Rectangle matchRect, 
            double confidence, 
            string accountId, 
            AutoHuntSettings settings, 
            CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Found tick at {matchRect} with confidence {confidence:F3}");

            if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
            {
                logger.LogInfo($"[{account.AccountName}] Successfully clicked tick");
                await Task.Delay(500, cancellationToken);
                
                // Remove overlapping used areas
                RemoveOverlappingUsedAreas(account, logger, matchRect, accountId, settings);
                
                // Click the confirm point
                if (await ClickAsync(account.InstanceNumber, logger, AutoHuntConstants.TickConfirmPoint))
                {
                    logger.LogInfo($"[{account.AccountName}] Clicked tick confirm point");
                }
            }
        }

        private void RemoveOverlappingUsedAreas(
            AccountSettings account, 
            LogService logger, 
            Rectangle matchRect, 
            string accountId, 
            AutoHuntSettings settings)
        {
            var tickArea = new Rectangle(
                matchRect.X - AutoHuntConstants.TARGET_AREA_PADDING,
                matchRect.Y - AutoHuntConstants.TARGET_AREA_PADDING,
                matchRect.Width + (AutoHuntConstants.TARGET_AREA_PADDING * 2),
                matchRect.Height + (AutoHuntConstants.TARGET_AREA_PADDING * 2)
            );

            var usedAreas = settings.GetUsedTargetAreas(accountId);
            var overlappingAreas = usedAreas.Where(used => 
                !(tickArea.Left > used.Right || 
                  tickArea.Right < used.Left || 
                  tickArea.Top > used.Bottom || 
                  tickArea.Bottom < used.Top)).ToList();

            foreach (var area in overlappingAreas)
            {
                settings.RemoveUsedTargetArea(accountId, area);
                logger.LogInfo($"[{account.AccountName}] Removed used area restriction at {area} due to tick click");
            }
            
            SaveAutoHuntSettings(account, settings);
        }

        private async Task ScanForTargets(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences, 
            bool huntModeFound)
        {
            logger.LogInfo($"[{account.AccountName}] Finished processing ticks, now scanning for targets (up to 5 seconds)...");

            var targetTypes = new[]
            {
                ("king.png", true),
                ("bear.png", true),
                ("scout.png", false),
                ("attack.png", false)
            };

            DateTime startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;

                foreach (var (imageName, requiresMarch) in targetTypes)
                {
                    // Skip certain targets if not in hunt mode
                    if (!huntModeFound && (imageName == "king.png" || imageName == "scout.png" || imageName == "attack.png"))
                    {
                        continue;
                    }

                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        Path.Combine(ImageTemplateFolder, imageName),
                        account.InstanceNumber,
                        threshold: 0.68,
                        searchArea: AutoHuntConstants.TargetSearchArea
                    );

                    // Track all detections with confidence > 0.5
                    if (confidence > 0.5)
                    {
                        if (!bestConfidences.ContainsKey(imageName))
                        {
                            bestConfidences[imageName] = new List<(Rectangle, double)>();
                        }

                        // Check if we already have a similar location
                        bool isDuplicate = bestConfidences[imageName].Any(existing =>
                            Math.Abs(existing.location.X - matchRect.X) < AutoHuntConstants.TARGET_AREA_PADDING &&
                            Math.Abs(existing.location.Y - matchRect.Y) < AutoHuntConstants.TARGET_AREA_PADDING);

                        if (!isDuplicate)
                        {
                            bestConfidences[imageName].Add((matchRect, confidence));
                            logger.LogInfo($"[{account.AccountName}] Found potential {imageName} at {matchRect} with confidence {confidence:F3}");
                        }
                    }
                }

                // If we have found at least one high-confidence target, we can break early
                if (bestConfidences.Any(kv => kv.Value.Any(v => v.confidence >= 0.68)))
                {
                    break;
                }

                await Task.Delay(200, cancellationToken);
            }
        }

        private List<HuntTarget> ProcessBestDetections(
            AccountSettings account, 
            LogService logger,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences,
            AutoHuntSettings settings, 
            string accountId)
        {
            var targets = new List<HuntTarget>();
            var targetTypes = new[]
            {
                ("king.png", true),
                ("bear.png", true),
                ("scout.png", false),
                ("attack.png", false)
            };

            foreach (var (imageName, detections) in bestConfidences)
            {
                var sortedDetections = detections.OrderByDescending(d => d.confidence).ToList();

                foreach (var (matchRect, confidence) in sortedDetections)
                {
                    if (confidence < 0.68) continue;

                    var targetArea = new Rectangle(
                        matchRect.X - AutoHuntConstants.TARGET_AREA_PADDING,
                        matchRect.Y - AutoHuntConstants.TARGET_AREA_PADDING,
                        matchRect.Width + (AutoHuntConstants.TARGET_AREA_PADDING * 2),
                        matchRect.Height + (AutoHuntConstants.TARGET_AREA_PADDING * 2)
                    );

                    var usedAreas = settings.GetUsedTargetAreas(accountId);
                    bool areaAlreadyUsed = IsAreaAlreadyUsed(targetArea, usedAreas);

                    if (!areaAlreadyUsed)
                    {
                        logger.LogInfo($"[{account.AccountName}] Adding valid {imageName} at {matchRect} with confidence {confidence:F3}");
                        targets.Add(new HuntTarget
                        {
                            Type = imageName.Replace(".png", ""),
                            RequiresMarch = targetTypes.First(t => t.Item1 == imageName).Item2,
                            MatchLocation = matchRect,
                            TargetArea = targetArea,
                            Confidence = confidence
                        });
                    }
                    else
                    {
                        LogBlockedTarget(account, logger, imageName, matchRect, targetArea, usedAreas, accountId);
                    }
                }
            }

            return targets;
        }

        private bool IsAreaAlreadyUsed(Rectangle targetArea, List<Rectangle> usedAreas)
        {
            return usedAreas.Any(used => 
                !(targetArea.Left > used.Right || 
                  targetArea.Right < used.Left || 
                  targetArea.Top > used.Bottom || 
                  targetArea.Bottom < used.Top));
        }

        private void LogBlockedTarget(
            AccountSettings account, 
            LogService logger, 
            string imageName, 
            Rectangle matchRect, 
            Rectangle targetArea, 
            List<Rectangle> usedAreas, 
            string accountId)
        {
            logger.LogInfo($"[{account.AccountName}] BLOCKED: Skipping {imageName} at {matchRect} (blocked area: {targetArea}) - overlaps with previously clicked area for account {accountId}");
            
            var overlappingArea = usedAreas.FirstOrDefault(used => 
                !(targetArea.Left > used.Right || 
                  targetArea.Right < used.Left || 
                  targetArea.Top > used.Bottom || 
                  targetArea.Bottom < used.Top));
            
            if (overlappingArea != Rectangle.Empty)
            {
                logger.LogInfo($"[{account.AccountName}] -> Overlaps with used area: {overlappingArea}");
            }
        }

        private async Task HandleNoTargetsFound(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences, 
            bool foundAnyTick)
        {
            // Log potential targets that didn't meet the threshold
            var lowConfidenceTargets = bestConfidences
                .SelectMany(kv => kv.Value.Select(v => (type: kv.Key, location: v.location, confidence: v.confidence)))
                .OrderByDescending(t => t.confidence)
                .ToList();

            if (lowConfidenceTargets.Any())
            {
                logger.LogInfo($"[{account.AccountName}] No valid targets found, but detected these potential targets (confidence < 0.68):");
                foreach (var (type, location, confidence) in lowConfidenceTargets)
                {
                    logger.LogInfo($"[{account.AccountName}] - {type} at {location} with confidence {confidence:F3}");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] No targets detected at all during scanning period");
            }

            // If no targets found and no ticks were processed, check for level-up
            if (!foundAnyTick)
            {
                await HandleLevelUpPopup(account, logger, cancellationToken);
            }
        }

        private async Task HandleLevelUpPopup(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] No ticks or targets found, checking for level-up...");
            
            var levelUpScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (levelUpScreenshot != null)
            {
                var (levelUpFound, _, levelUpConfidence) = _templateMatcher.MatchTemplate(
                    levelUpScreenshot,
                    Path.Combine(ImageTemplateFolder, "level-up.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.LevelUpArea
                );

                if (levelUpFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Level-up detected, clicking dismiss point");
                    if (await ClickAsync(account.InstanceNumber, logger, AutoHuntConstants.LevelUpDismissPoint))
                    {
                        logger.LogInfo($"[{account.AccountName}] Clicked level-up dismiss point");
                        await Task.Delay(500, cancellationToken);
                    }
                }
            }
        }

        private async Task CreateDebugLog(
            AccountSettings account, 
            LogService logger, 
            List<HuntTarget> targets, 
            AutoHuntSettings settings, 
            string accountId)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting visual blocking debug log creation...");
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null)
                {
                    logger.LogInfo($"[{account.AccountName}] Screenshot captured ({screenshot.Length} bytes), creating target info...");
                    var targetInfos = targets.Select(t => new TargetInfo
                    {
                        Type = t.Type,
                        TargetArea = t.TargetArea,
                        Confidence = t.Confidence
                    }).ToList();

                    var blockedAreasForVisual = settings.GetUsedTargetAreas(accountId).ToList();
                    var action = targets.Any() ? $"found_{targets.Count}_targets" : "no_targets_found";
                    logger.LogInfo($"[{account.AccountName}] Creating visual log: {targetInfos.Count} targets, {blockedAreasForVisual.Count} blocked areas, action: {action}");
                    
                    _ = Task.Run(() =>
                    {
                        CreateVisualBlockingLogSync(screenshot, account, logger, targetInfos, blockedAreasForVisual, action);
                    });
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] Screenshot was null, skipping visual blocking log");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{account.AccountName}] Failed to create visual blocking log: {ex.Message}");
            }
        }

        private void LogBlockedAreas(AccountSettings account, LogService logger, string accountId, List<Rectangle> blockedAreas)
        {
            if (blockedAreas.Any())
            {
                logger.LogInfo($"[{account.AccountName}] Currently blocked areas for account {accountId}: {blockedAreas.Count} areas");
                foreach (var area in blockedAreas)
                {
                    logger.LogInfo($"[{account.AccountName}] -> Blocked: {area}");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] No blocked areas for account {accountId}");
            }
        }

        #endregion

        private void CreateVisualBlockingLogSync(
            byte[] screenshotBytes,
            AccountSettings account,
            LogService logger,
            List<TargetInfo> foundTargets,
            List<Rectangle> blockedAreas,
            string action)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] CreateVisualBlockingLogSync called with action: {action}");
                
                if (screenshotBytes == null || screenshotBytes.Length == 0) 
                {
                    logger.LogWarning($"[{account.AccountName}] Screenshot bytes are null or empty");
                    return;
                }

                var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots", AutoHuntConstants.DEBUG_SCREENSHOT_FOLDER);
                Directory.CreateDirectory(logDirectory);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var filename = $"blocking_{account.InstanceNumber}_{account.AccountName}_{action}_{timestamp}.png";
                var filepath = Path.Combine(logDirectory, filename);
                
                using var originalImage = Image.FromStream(new MemoryStream(screenshotBytes));
                using var bitmap = new Bitmap(originalImage);
                using var graphics = Graphics.FromImage(bitmap);

                // Set up drawing tools
                using var blockedPen = new Pen(Color.Red, 3);
                using var availablePen = new Pen(Color.Green, 3);
                using var font = new Font("Arial", 8, FontStyle.Bold);
                using var blockedBrush = new SolidBrush(Color.FromArgb(100, Color.Red));
                using var availableBrush = new SolidBrush(Color.FromArgb(100, Color.Green));
                using var textBrush = new SolidBrush(Color.White);

                // Draw blocked areas (red)
                if (blockedAreas != null)
                {
                    foreach (var blockedArea in blockedAreas)
                    {
                        graphics.FillRectangle(blockedBrush, blockedArea);
                        graphics.DrawRectangle(blockedPen, blockedArea);
                        graphics.DrawString("BLOCKED", font, textBrush, blockedArea.X + 2, blockedArea.Y + 2);
                    }
                }

                // Draw available/found targets (green)
                if (foundTargets != null)
                {
                    foreach (var target in foundTargets)
                    {
                        graphics.FillRectangle(availableBrush, target.TargetArea);
                        graphics.DrawRectangle(availablePen, target.TargetArea);
                        graphics.DrawString($"{target.Type.ToUpper()}", font, textBrush, target.TargetArea.X + 2, target.TargetArea.Y + 2);
                    }
                }

                bitmap.Save(filepath, ImageFormat.Png);
                logger.LogInfo($"[{account.AccountName}] Visual blocking log saved: {filepath}");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error creating visual blocking log: {ex.Message}");
            }
        }

        #region March Management

        private async Task<int> GetAvailableMarchesDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return 0;

            // Use OCR to read march count from UI
            try
            {
                var ocrService = new OCRService(logger);
                var text = ocrService.ExtractTextFromScreenArea(screenshot, AutoHuntConstants.MarchCountArea);
                
                logger.LogInfo($"[{account.AccountName}] OCR extracted text: '{text}' from area {AutoHuntConstants.MarchCountArea}");
                
                // Clean the text - remove any non-numeric characters except forward slash
                var cleanText = new string(text.Where(c => char.IsDigit(c) || c == '/').ToArray());
                
                if (!string.IsNullOrEmpty(cleanText))
                {
                    // If it contains a slash, take the first number (available marches)
                    var parts = cleanText.Split('/');
                    if (parts.Length > 0 && int.TryParse(parts[0], out var marches))
                    {
                        logger.LogInfo($"[{account.AccountName}] Successfully parsed {marches} marches from OCR text: '{cleanText}'");
                        return marches;
                    }
                }
                
                logger.LogWarning($"[{account.AccountName}] Could not parse march count from OCR: '{text}' (cleaned: '{cleanText}')");
                
                // Fallback: assume at least 1 march is available if we can't read it
                logger.LogInfo($"[{account.AccountName}] Using fallback: assuming 1 march available");
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error reading march count: {ex.Message}");
                // Fallback: assume at least 1 march is available
                logger.LogInfo($"[{account.AccountName}] Using error fallback: assuming 1 march available");
                return 1;
            }
        }

        private async Task<int> WaitForAvailableMarchesDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;
            const int maxWaitSeconds = 120;
            int availableMarches = 0;

            while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds && !cancellationToken.IsCancellationRequested)
            {
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return -1;

                availableMarches = await GetAvailableMarchesDirectly(account, logger, cancellationToken);
                
                if (availableMarches > 0)
                {
                    logger.LogInfo($"[{account.AccountName}] Found {availableMarches} available marches");
                    return availableMarches;
                }

                // Check for stamina depletion while waiting
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null)
                {
                    var staminaResult = await CheckForStaminaLowDirectly(screenshot, AutoHuntConstants.StaminaArea, account, logger);
                    if (staminaResult == StaminaCheckResult.StaminaDepleted)
                    {
                        return -1; // Signal stamina depletion
                    }
                }

                logger.LogInfo($"[{account.AccountName}] No marches available, waiting... ({(DateTime.UtcNow - startTime).TotalSeconds:F0}s elapsed)");
                await Task.Delay(5000, cancellationToken);
            }

            logger.LogWarning($"[{account.AccountName}] Timeout waiting for marches after {maxWaitSeconds} seconds");
            return 0;
        }

        private async Task<bool> CheckMaxMarchDirectly(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            var (maxMarchFound, _, _) = _templateMatcher.MatchTemplate(
                screenshot,
                Path.Combine(ImageTemplateFolder, "max-march.png"),
                account.InstanceNumber,
                threshold: 0.6
            );

            if (maxMarchFound)
            {
                logger.LogInfo($"[{account.AccountName}] Max march detected, clicking dismiss area");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, AutoHuntConstants.MaxMarchClickArea);
                
                var settings = GetAutoHuntSettings(account);
                settings.LastMaxMarchTime = DateTime.UtcNow;
                SaveAutoHuntSettings(account, settings);
                
                return true;
            }

            return false;
        }

        private async Task<bool> FindAndClickQuickDeployDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for quick-deploy button (waiting up to 3 seconds)...");

            DateTime startTime = DateTime.UtcNow;
            bool quickDeployFound = false;
            Rectangle deployRect = Rectangle.Empty;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !quickDeployFound && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                var (found, rect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    Path.Combine(ImageTemplateFolder, "quick-deploy.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.QuickDeployArea
                );

                if (found)
                {
                    quickDeployFound = true;
                    deployRect = rect;
                    logger.LogInfo($"[{account.AccountName}] Found quick-deploy button with confidence {confidence:F3} after {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                    break;
                }

                // Log progress every second
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                if (elapsed >= 1 && elapsed % 1 < 0.2) // Log roughly every second
                {
                    logger.LogInfo($"[{account.AccountName}] Still looking for quick-deploy button... ({elapsed:F1}s elapsed)");
                }

                await Task.Delay(200, cancellationToken);
            }

            if (quickDeployFound)
            {
                logger.LogInfo($"[{account.AccountName}] Found quick-deploy button, clicking");
                return await ClickRandomInRectAsync(account.InstanceNumber, logger, deployRect);
            }

            logger.LogWarning($"[{account.AccountName}] Quick-deploy button not found after 3 seconds");
            return false;
        }

        #endregion

        #region Stamina Management

        private async Task<StaminaCheckResult> CheckForStaminaLowDirectly(
            byte[]? screenshot,
            Rectangle staminaArea,
            AccountSettings account,
            LogService logger)
        {
            if (screenshot == null) return StaminaCheckResult.NoStaminaIssue;

            try
            {
                var ocrService = new OCRService(logger);

                // Create full screen rectangle instead of using limited area
                // Standard emulator resolution is 720x1280, but we'll use a large rectangle to cover the full screen
                var fullScreenArea = new Rectangle(0, 0, 720, 1280);
                var text = ocrService.ExtractTextFromScreenArea(screenshot, fullScreenArea);

                if (!string.IsNullOrEmpty(text))
                {
                    text = text.ToLower().Trim();
                    logger.LogInfo($"[{account.AccountName}] Full screen OCR text for stamina check: '{text}'");

                    // Check for traditional stamina low messages
                    if (text.Contains("stamina") && text.Contains("low"))
                    {
                        logger.LogWarning($"[{account.AccountName}] Stamina low detected: '{text}'");
                        return StaminaCheckResult.StaminaDepleted;
                    }

                    if (text.Contains("not enough") || text.Contains("insufficient"))
                    {
                        logger.LogWarning($"[{account.AccountName}] Insufficient stamina detected: '{text}'");
                        return StaminaCheckResult.StaminaDepleted;
                    }

                    // Check for stamina restoration/replenishment dialogs
                    if (text.Contains("stamina-replenishing") ||
                        text.Contains("restore") && text.Contains("stamina") ||
                        text.Contains("governor stamina") ||
                        text.Contains("storehouse feast"))
                    {
                        logger.LogWarning($"[{account.AccountName}] Stamina restoration dialog detected: '{text}'");
                        return StaminaCheckResult.StaminaDepleted;
                    }
                }

                return StaminaCheckResult.NoStaminaIssue;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error checking stamina: {ex.Message}");
                return StaminaCheckResult.NoStaminaIssue;
            }
        }

        #endregion

        #region Hunt Mode Navigation


        private async Task<bool> EnterHuntModeDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for hunt button...");
            if (await WaitForImageAsync("hunt-button.png", account.InstanceNumber, logger, cancellationToken,
                timeoutMs: 2000, threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
            {
                // Found the hunt button, now click it
                if (!await FindAndClickImageAsync("hunt-button.png", account.InstanceNumber, logger,
                    threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
                {
                    logger.LogError($"[{account.AccountName}] Found hunt button but failed to click it");
                    return false;
                }
                return true;
            }
            else
            {
                logger.LogError($"[{account.AccountName}] Error: Failed to find hunt-button.png, calling locator module for recovery");
                var huntLocator = new LocatorService(logger, account);
                try
                {
                    await huntLocator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying hunt button detection");
                    
                    // Retry after locator service with timeout
                    if (await WaitForImageAsync("hunt-button.png", account.InstanceNumber, logger, cancellationToken,
                        timeoutMs: 2000, threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
                    {
                        // Found the hunt button, now click it
                        if (!await FindAndClickImageAsync("hunt-button.png", account.InstanceNumber, logger,
                            threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
                        {
                            logger.LogError($"[{account.AccountName}] Found hunt button after recovery but failed to click it");
                            return false;
                        }
                        return true;
                    }
                    else
                    {
                        logger.LogError($"[{account.AccountName}] Could not find hunt button even after locator recovery");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                    return false;
                }
            }
        }

        private async Task ReturnToMapViewDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Returning to map view");
            
            // Try to click the close details button
            if (await FindAndClickImageAsync("close-details.png", account.InstanceNumber, logger, threshold: 0.6))
            {
                logger.LogInfo($"[{account.AccountName}] Clicked close details button");
                await Task.Delay(1000, cancellationToken);
            }
            
            // Use locator service to ensure map view
            var locator = new LocatorService(logger, account);
            await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
        }

        private async Task<bool> IsTargetMarchingDirectly(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            // Check for "Marching" text in the marching text area
            try
            {
                var ocrService = new OCRService(logger);
                var text = ocrService.ExtractTextFromScreenArea(screenshot, AutoHuntConstants.MarchingTextArea);
                
                if (!string.IsNullOrEmpty(text) && text.ToLower().Contains("marching"))
                {
                    logger.LogInfo($"[{account.AccountName}] Detected marching text: '{text}'");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{account.AccountName}] Error checking for marching text: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private async Task<bool> HandleDeployment(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            string accountId)
        {
            DateTime startTime = DateTime.UtcNow;
            bool buttonFound = false;
            
            while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !buttonFound && !cancellationToken.IsCancellationRequested)
            {
                var deployScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (deployScreenshot == null) continue;

                // Check for deploy button
                var (deployFound, deployRect, deployConfidence) = _templateMatcher.MatchTemplate(
                    deployScreenshot,
                    Path.Combine(ImageTemplateFolder, "deploy-button.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.DeployButtonArea
                );

                if (deployFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Found deploy button, proceeding with attack");
                    buttonFound = true;
                    if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, deployRect))
                        return false;
                    break;
                }

                // Check for max march
                var (maxMarchFound, _, _) = _templateMatcher.MatchTemplate(
                    deployScreenshot,
                    Path.Combine(ImageTemplateFolder, "max-march.png"),
                    account.InstanceNumber,
                    threshold: 0.6
                );

                if (maxMarchFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected, clicking dismiss area");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, AutoHuntConstants.MaxMarchClickArea);
                    var maxMarchSettings = GetAutoHuntSettings(account);
                    maxMarchSettings.LastMaxMarchTime = DateTime.UtcNow;
                    SaveAutoHuntSettings(account, maxMarchSettings);
                    return false;
                }

                await Task.Delay(200, cancellationToken);
            }

            return buttonFound;
        }

        private async Task<TargetProcessResult> HandleConfirmationAndStamina(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken)
        {
            await Task.Delay(1000, cancellationToken);
            var confirmScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (confirmScreenshot != null)
            {
                var staminaLowArea = new Rectangle(285, 122, 155, 41);
                var staminaResult = await CheckForStaminaLowDirectly(confirmScreenshot, staminaLowArea, account, logger);
                
                if (staminaResult == StaminaCheckResult.StaminaDepleted)
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted");
                }
                else if (staminaResult == StaminaCheckResult.StaminaClaimed_Retry)
                {
                    logger.LogInfo($"[{account.AccountName}] Stamina claimed, continuing with deployment");
                }

                // Check for confirmation dialog
                var (confirmationFound, _, _) = _templateMatcher.MatchTemplate(
                    confirmScreenshot,
                    Path.Combine(ImageTemplateFolder, "confirmation.png"),
                    account.InstanceNumber,
                    threshold: 0.6
                );

                if (confirmationFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Found confirmation dialog, clicking confirm points");
                    await ClickAsync(account.InstanceNumber, logger, new Point(212, 714));
                    await Task.Delay(500, cancellationToken);
                    await ClickAsync(account.InstanceNumber, logger, new Point(456, 770));
                    await Task.Delay(1000, cancellationToken);
                }
            }

            return TargetProcessResult.Successful("Confirmation handled");
        }

        private async Task<string> GetAccountIdAsync(AccountSettings account, LogService logger)
        {
            // Use account name as ID since AccountDetectionTask is not available
            return $"{account.InstanceNumber}_{account.AccountName}";
        }

        private AutoHuntSettings GetAutoHuntSettings(AccountSettings account)
        {
            var configManager = ConfigurationManager.Instance;
            var config = configManager.GetConfig();
            
            if (!account.TaskSettings.TryGetValue("AutoHunt", out var settingsJson) || string.IsNullOrEmpty(settingsJson))
            {
                return new AutoHuntSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AutoHuntSettings>(settingsJson) ?? new AutoHuntSettings();
            }
            catch (Exception)
            {
                return new AutoHuntSettings();
            }
        }

        private void SaveAutoHuntSettings(AccountSettings account, AutoHuntSettings settings)
        {
            var configManager = ConfigurationManager.Instance;
            var settingsJson = JsonSerializer.Serialize(settings);
            account.TaskSettings["AutoHunt"] = settingsJson;
            // Configuration is auto-saved via ConfigurationManager.Instance
        }

        private async Task<string> ReadPredictionText(AccountSettings account, LogService logger)
        {
            // Use OCR to read prediction text - placeholder for now
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot != null)
            {
                try
                {
                    var ocrService = new OCRService(logger);
                    var text = ocrService.ExtractTextFromScreenArea(screenshot, AutoHuntConstants.OcrPredictionArea);
                    return text ?? string.Empty;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[{account.AccountName}] Failed to read prediction text: {ex.Message}");
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }


        #endregion
    }
}