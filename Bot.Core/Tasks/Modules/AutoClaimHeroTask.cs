using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace Bot.Core.Tasks.Modules
{
    public class AutoClaimHeroTask : BaseTaskWithCommonPatterns
    {
        private const int TEMPLATE_TIMEOUT_MS = 5000; // 5 seconds timeout for template detection
        private const double CONFIDENCE_THRESHOLD = 0.7; // Standard confidence threshold
        
        private readonly UnifiedTemplateMatchingService _templateMatchingService;

        private async Task<bool> WaitForImageWithDebug(string imageName, int instanceNumber, LogService logger, CancellationToken cancellationToken, int timeoutMs)
        {
            if (_templateMatcher == null)
            {
                logger.LogError("CRITICAL: _templateMatcher is null");
                return false;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var bestConfidence = 0.0;

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"Image search for '{imageName}' cancelled");
                    return false;
                }

                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'");
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                string imagePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(imagePath))
                {
                    logger.LogError($"Image template not found: {imagePath}");
                    return false;
                }

                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    imagePath,
                    instanceNumber,
                    CONFIDENCE_THRESHOLD
                );

                bestConfidence = Math.Max(bestConfidence, result.confidence);
                logger.LogInfo($"Template match attempt for '{imageName}': Confidence = {result.confidence:F3} (Best so far: {bestConfidence:F3})");

                if (result.found)
                {
                    logger.LogInfo($"✅ Found '{imageName}' with confidence {result.confidence:F3} at {result.matchRect}");
                    return true;
                }

                await Task.Delay(1000, cancellationToken);
            }

            logger.LogWarning($"❌ Failed to find '{imageName}' after {timeoutMs}ms. Best confidence was {bestConfidence:F3}");
            return false;
        }
        
        public AutoClaimHeroTask(UnifiedTemplateMatchingService templateMatchingService) 
            : base()
        {
            _templateMatchingService = templateMatchingService;
        }

        public override TaskType TaskType => TaskType.AutoClaimHero;
        public override string Name => "Auto Claim Hero";
        protected override string GetImageFolderName() => "autoclaimhero";

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo("Starting Auto Claim Hero task");

                // Click hero button
                var heroButtonArea = new Rectangle(155, 1173, 66, 76);
                if (!await WaitForImageWithDebug("hero-button.png", account.InstanceNumber, logger, cancellationToken, TEMPLATE_TIMEOUT_MS))
                {
                    logger.LogError($"[{account.AccountName}] Error: Failed to find hero-button.png, calling locator module for recovery");
                    var locator = new LocatorService(logger, account);
                    try
                    {
                        await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying hero button detection");
                        
                        // Retry after locator service
                        if (!await WaitForImageWithDebug("hero-button.png", account.InstanceNumber, logger, cancellationToken, TEMPLATE_TIMEOUT_MS))
                        {
                            logger.LogError($"[{account.AccountName}] Could not find hero button even after locator recovery");
                            return TaskExecutionDetails.Failed("Could not find hero button after locator recovery");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                        return TaskExecutionDetails.Failed("Could not find hero button");
                    }
                }

                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, heroButtonArea, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Failed to click hero button");
                }
                await Task.Delay(1000, cancellationToken);

                // Click recruit heroes button
                var recruitButtonArea = new Rectangle(407, 1181, 252, 56);
                if (!await WaitForImageWithDebug("recruit-heros-button.png", account.InstanceNumber, logger, cancellationToken, TEMPLATE_TIMEOUT_MS))
                {
                    logger.LogError($"[{account.AccountName}] Error: Failed to find recruit-heros-button.png, calling locator module for recovery");
                    var locator = new LocatorService(logger, account);
                    try
                    {
                        await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying recruit heroes button detection");
                        
                        // Retry after locator service
                        if (!await WaitForImageWithDebug("recruit-heros-button.png", account.InstanceNumber, logger, cancellationToken, TEMPLATE_TIMEOUT_MS))
                        {
                            logger.LogError($"[{account.AccountName}] Could not find recruit heroes button even after locator recovery");
                            return TaskExecutionDetails.Failed("Could not find recruit heroes button after locator recovery");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                        return TaskExecutionDetails.Failed("Could not find recruit heroes button");
                    }
                }

                if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, recruitButtonArea, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Failed to click recruit heroes button");
                }
                await Task.Delay(1000, cancellationToken);

                // Keep claiming free heroes until no more are found
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Check both possible free button locations
                    var freeButton1Area = new Rectangle(140, 824, 91, 55);
                    var freeButton2Area = new Rectangle(145, 1192, 90, 47);
                    
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        logger.LogWarning("Failed to get screenshot when looking for free buttons");
                        continue;
                    }

                    // Check first location
                    var freeButton1Result = _templateMatcher.MatchTemplate(
                        screenshot,
                        Path.Combine(ImageTemplateFolder, "free-button.png"),
                        account.InstanceNumber,
                        threshold: 0.8,
                        scales: null,
                        searchArea: freeButton1Area
                    );

                    // Check second location
                    var freeButton2Result = _templateMatcher.MatchTemplate(
                        screenshot,
                        Path.Combine(ImageTemplateFolder, "free-button.png"),
                        account.InstanceNumber,
                        threshold: 0.8,
                        scales: null,
                        searchArea: freeButton2Area
                    );

                    // If no free buttons found in either location, break the loop
                    if (!freeButton1Result.found && !freeButton2Result.found)
                    {
                        logger.LogInfo($"No more free heroes to claim (best confidences - top: {freeButton1Result.confidence:F3}, bottom: {freeButton2Result.confidence:F3})");
                        break;
                    }

                    // Click the first free button we find
                    Rectangle? buttonToClick = null;
                    if (freeButton1Result.found)
                    {
                        logger.LogInfo($"Found free button in top location with confidence {freeButton1Result.confidence:F3}");
                        buttonToClick = freeButton1Area;
                    }
                    else if (freeButton2Result.found)
                    {
                        logger.LogInfo($"Found free button in bottom location with confidence {freeButton2Result.confidence:F3}");
                        buttonToClick = freeButton2Area;
                    }

                    if (buttonToClick.HasValue)
                    {
                        await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, buttonToClick.Value, cancellationToken);
                        
                        // Wait for rewards image to appear
                        var rewardsArea = new Rectangle(257, 212, 203, 66);
                        DateTime startTime = DateTime.UtcNow;
                        bool rewardsFound = false;

                        while ((DateTime.UtcNow - startTime).TotalSeconds < 10 && !rewardsFound && !cancellationToken.IsCancellationRequested)
                        {
                            var rewardsScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                            if (rewardsScreenshot == null) continue;

                            var rewardsResult = _templateMatcher.MatchTemplate(
                                rewardsScreenshot,
                                Path.Combine(ImageTemplateFolder, "rewards.png"),
                                account.InstanceNumber,
                                threshold: 0.8,
                                scales: null,
                                searchArea: rewardsArea
                            );

                            if (rewardsResult.found)
                            {
                                logger.LogInfo($"Found rewards image at {rewardsResult.matchRect} with confidence {rewardsResult.confidence:F3}");
                                rewardsFound = true;
                                // Add delay after finding rewards before looking for back arrow
                                await Task.Delay(1000, cancellationToken);
                                break;
                            }

                            await Task.Delay(200, cancellationToken);
                        }

                        if (!rewardsFound)
                        {
                            logger.LogWarning("Could not find rewards image after 10 seconds");
                            continue;
                        }

                        // Look for and click back arrow (with 5 second timeout)
                        bool backArrowFound = false;
                        DateTime backArrowStartTime = DateTime.UtcNow;

                        while ((DateTime.UtcNow - backArrowStartTime).TotalSeconds < 5 && !backArrowFound && !cancellationToken.IsCancellationRequested)
                        {
                            var backArrowScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                            if (backArrowScreenshot == null)
                            {
                                logger.LogWarning("Failed to get screenshot when looking for back arrow");
                                await Task.Delay(200, cancellationToken);
                                continue;
                            }

                            var backArrowResult = _templateMatcher.MatchTemplate(
                                backArrowScreenshot,
                                Path.Combine(ImageTemplateFolder, "back-arrow.png"),
                                account.InstanceNumber,
                                threshold: 0.8,
                                scales: null,
                                searchArea: null  // Search full screen
                            );

                            if (backArrowResult.found)
                            {
                                logger.LogInfo($"Found back arrow at {backArrowResult.matchRect} with confidence {backArrowResult.confidence:F3}");
                                await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, backArrowResult.matchRect, cancellationToken);
                                backArrowFound = true;
                                await Task.Delay(1000, cancellationToken);
                                break;
                            }
                            else
                            {
                                var timeSpent = (DateTime.UtcNow - backArrowStartTime).TotalSeconds;
                                logger.LogInfo($"Back arrow not found yet (confidence: {backArrowResult.confidence:F3}), searching for {timeSpent:F1}/5.0 seconds");
                                await Task.Delay(200, cancellationToken);
                            }
                        }

                        if (!backArrowFound)
                        {
                            logger.LogWarning("Failed to find back arrow after 5 seconds of continuous checking");
                            continue;
                        }
                    }
                }

                // Return to base/map view
                var backButtonArea = new Rectangle(87, 74, 30, 30);
                for (int i = 0; i < 2; i++)
                {
                    var backArrowScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (backArrowScreenshot == null)
                    {
                        logger.LogWarning("Failed to get screenshot when looking for back arrow");
                        return TaskExecutionDetails.Failed("Failed to get screenshot for back arrow");
                    }

                    var backArrowResult = _templateMatcher.MatchTemplate(
                        backArrowScreenshot,
                        Path.Combine(ImageTemplateFolder, "back-arrow.png"),
                        account.InstanceNumber,
                        threshold: 0.8,
                        scales: null,
                        searchArea: null  // Search full screen
                    );

                    if (!backArrowResult.found)
                    {
                        logger.LogWarning("Could not find back button");
                        return TaskExecutionDetails.Failed("Could not find back button");
                    }

                    logger.LogInfo($"Found back arrow at {backArrowResult.matchRect} with confidence {backArrowResult.confidence:F3}");
                    await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, backArrowResult.matchRect, cancellationToken);
                    await Task.Delay(1000, cancellationToken);
                }

                logger.LogInfo("Auto Claim Hero task completed successfully");
                return TaskExecutionDetails.Succeeded();
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in Auto Claim Hero task: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }
    }
} 