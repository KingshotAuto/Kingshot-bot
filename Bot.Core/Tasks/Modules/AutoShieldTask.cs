using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Exceptions;
using Bot.Core.Config;
using Bot.Core.Services;
using System.Text.RegularExpressions;

namespace Bot.Core.Tasks.Modules
{
    public class AutoShieldTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoShield;
        public override string Name => "Auto Shield";

        private readonly string _templateFolder;
        private readonly AutoShieldMetrics _metrics;
        private readonly IConfigurationManager _configManager;
        private readonly IServiceProvider? _serviceProvider;
        private const int POLL_INTERVAL_MS = 200;
        private const double TEMPLATE_MATCH_THRESHOLD = 0.7;

        // UI Element Coordinates
        private static readonly Rectangle ShieldTimerRect = new Rectangle(325, 275, 138, 29);  // 325,275 to 463,304
        private static readonly Rectangle RechargeableCountRect = new Rectangle(554, 410, 65, 32);  // 554,410 to 619,442
        private static readonly Rectangle TwoHourButtonRect = new Rectangle(548, 535, 78, 50);  // 548,535 to 626,585
        private static readonly Rectangle EightHourButtonRect = new Rectangle(548, 685, 72, 44);  // 548,685 to 620,729
        private static readonly Rectangle TwentyFourHourButtonRect = new Rectangle(546, 837, 77, 41);  // 546,837 to 623,878
        private static readonly Rectangle SeventyTwoHourButtonRect = new Rectangle(552, 986, 71, 43);  // 552,986 to 623,1029
        private static readonly Rectangle ConfirmButtonRect = new Rectangle(408, 741, 177, 60);  // 408,741 to 585,801
        private static readonly Rectangle BuyUseButtonRect = new Rectangle(509, 347, 134, 48);  // 509,347 to 643,395

        public AutoShieldTask(IConfigurationManager configManager, IServiceProvider? serviceProvider = null)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _serviceProvider = serviceProvider;
            string baseDir = AppContext.BaseDirectory;
            _templateFolder = Path.Combine(baseDir, "templates", "images", "autoshield");
            _metrics = new AutoShieldMetrics();

            if (!Directory.Exists(_templateFolder))
            {
                Directory.CreateDirectory(_templateFolder);
            }
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🛡️ Starting Auto Shield task...");

                var settings = GetAutoShieldSettings(account);
                if (settings == null)
                {
                    logger.LogWarning($"[{account.AccountName}] No shield settings found, using defaults.");
                    settings = new AutoShieldSettings();
                }

                // Navigate to shield menu
                if (!await NavigateToShieldMenuAsync(account, logger, cancellationToken))
                {
                    return new TaskExecutionDetails(false, message: "Failed to navigate to shield menu.");
                }

                // Check current shield timer
                var currentShieldTime = await ReadShieldTimerAsync(account, logger, cancellationToken);
                if (currentShieldTime == null)
                {
                    logger.LogInfo($"[{account.AccountName}] No shield timer detected - likely no shield active. Will apply shield.");
                    currentShieldTime = TimeSpan.Zero; // Set to zero to indicate no shield
                }
                else
                {
                    // Update UI with current shield time
                    UpdateTaskStatusWithShieldTime(account, currentShieldTime.Value);
                    
                    // Check if shield has enough time remaining (using shield overlap setting)
                    if (currentShieldTime.Value.TotalMinutes > settings.ShieldOverlapMinutes)
                    {
                        logger.LogInfo($"[{account.AccountName}] Shield time ({currentShieldTime.Value.TotalMinutes:F1} mins) exceeds shield overlap threshold ({settings.ShieldOverlapMinutes} mins). No action needed.");
                        
                        // Click back button to exit shield menu before moving to next module
                        logger.LogInfo($"[{account.AccountName}] Clicking back button to exit shield menu.");
                        string baseDir = AppContext.BaseDirectory;
                        var backButtonPath = Path.Combine(baseDir, "templates", "images", "recovery", "deploy-back.png");
                        bool backButtonClicked = await ClickTemplateAsync(account.InstanceNumber, backButtonPath, logger);
                        if (!backButtonClicked)
                        {
                            logger.LogWarning($"[{account.AccountName}] Could not find back button, shield menu may still be open.");
                        }
                        await Task.Delay(1000, cancellationToken); // Wait for navigation
                        
                        return new TaskExecutionDetails(true, message: $"Shield active: {currentShieldTime.Value.TotalHours:F1}h");
                    }
                }

                // Apply new shield
                bool shieldApplied = await ApplyShieldAsync(account, settings, logger, cancellationToken);
                if (!shieldApplied)
                {
                    return new TaskExecutionDetails(false, message: "Failed to apply shield.");
                }

                // Verify shield was applied
                var newShieldTime = await ReadShieldTimerAsync(account, logger, cancellationToken);
                if (newShieldTime == null)
                {
                    _metrics.FailedAttempts++;
                    logger.LogError($"[{account.AccountName}] Shield verification failed. No timer detected after applying shield.");
                    return new TaskExecutionDetails(false, message: "Shield verification failed.");
                }
                
                // If we started with no shield (TimeSpan.Zero), any shield time is good
                // Otherwise, new shield time should be greater than old shield time
                if (currentShieldTime.Value > TimeSpan.Zero && newShieldTime.Value.TotalMinutes <= currentShieldTime.Value.TotalMinutes)
                {
                    _metrics.FailedAttempts++;
                    logger.LogError($"[{account.AccountName}] Shield verification failed. New timer ({newShieldTime.Value.TotalMinutes:F1} mins) not greater than old timer ({currentShieldTime.Value.TotalMinutes:F1} mins).");
                    return new TaskExecutionDetails(false, message: "Shield verification failed.");
                }

                // Update metrics and settings
                UpdateMetricsAndSettings(account, settings, newShieldTime.Value);
                logger.LogInfo($"[{account.AccountName}] ✅ Shield successfully applied. Duration: {newShieldTime.Value.TotalHours:F1} hours");

                // Update UI with shield duration
                UpdateTaskStatusWithShieldTime(account, newShieldTime.Value);

                return new TaskExecutionDetails(true, message: $"Shield applied: {newShieldTime.Value.TotalHours:F1}h");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Auto Shield task: {ex.Message}");
                return new TaskExecutionDetails(false, message: $"Error: {ex.Message}");
            }
        }

        private async Task<bool> NavigateToShieldMenuAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Click city bonus icon
                var cityBonusPath = Path.Combine(_templateFolder, "city-bonus.png");
                if (!await ClickTemplateAsync(account.InstanceNumber, cityBonusPath, logger))
                {
                    logger.LogError($"[{account.AccountName}] Error: Failed to find city-bonus.png, calling locator module for recovery");
                    var locator = new LocatorService(logger, account);
                    try
                    {
                        await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying city bonus detection");
                        
                        // Retry after locator service
                        if (!await ClickTemplateAsync(account.InstanceNumber, cityBonusPath, logger))
                        {
                            logger.LogError($"[{account.AccountName}] Could not find city bonus icon even after locator recovery");
                            _metrics.TemplateNotFoundErrors++;
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                        _metrics.TemplateNotFoundErrors++;
                        return false;
                    }
                }

                await Task.Delay(POLL_INTERVAL_MS, cancellationToken);

                // Click shield icon
                var shieldIconPath = Path.Combine(_templateFolder, "shield-icon.png");
                if (!await ClickTemplateAsync(account.InstanceNumber, shieldIconPath, logger))
                {
                    logger.LogError($"[{account.AccountName}] Error: Failed to find shield-icon.png, calling locator module for recovery");
                    var locator = new LocatorService(logger, account);
                    try
                    {
                        await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying shield icon detection");
                        
                        // Retry after locator service
                        if (!await ClickTemplateAsync(account.InstanceNumber, shieldIconPath, logger))
                        {
                            logger.LogError($"[{account.AccountName}] Could not find shield icon even after locator recovery");
                            _metrics.TemplateNotFoundErrors++;
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                        _metrics.TemplateNotFoundErrors++;
                        return false;
                    }
                }

                // Wait 1 second after clicking shield icon for OCR to work properly
                await Task.Delay(1000, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Navigation error: {ex.Message}");
                return false;
            }
        }

        private async Task<TimeSpan?> ReadShieldTimerAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                var timerOcrConfig = new OCRConfiguration
                {
                    ScaleFactor = 4,
                    CharacterWhitelist = "0123456789:",
                    MedianBlurKernelSize = 1
                };

                using var timerOcr = new OCRService(logger, timerOcrConfig);
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) return null;

                string timerText = timerOcr.ExtractTextFromScreenArea(screenshot, ShieldTimerRect);
                logger.LogInfo($"[{account.AccountName}] Shield timer OCR text: '{timerText}'");

                var match = Regex.Match(timerText, @"(\d{1,2}):(\d{2}):(\d{2})");
                if (!match.Success)
                {
                    logger.LogError($"[{account.AccountName}] Could not parse timer format from: {timerText}");
                    _metrics.OcrTimeouts++;
                    return null;
                }

                int hours = int.Parse(match.Groups[1].Value);
                int minutes = int.Parse(match.Groups[2].Value);
                int seconds = int.Parse(match.Groups[3].Value);

                return new TimeSpan(hours, minutes, seconds);
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error reading shield timer: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> ApplyShieldAsync(AccountSettings account, AutoShieldSettings settings, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting shield application process...");
                
                if (settings.UseRechargeableShield)
                {
                    logger.LogInfo($"[{account.AccountName}] Attempting to apply rechargeable shield first...");
                    if (await TryApplyRechargeableShieldAsync(account, logger, cancellationToken))
                    {
                        logger.LogInfo($"[{account.AccountName}] Rechargeable shield application successful.");
                        return true;
                    }
                    logger.LogInfo($"[{account.AccountName}] Rechargeable shield unavailable, trying backup shield.");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Rechargeable shield disabled, using backup shield only.");
                }

                logger.LogInfo($"[{account.AccountName}] Attempting to apply backup shield...");
                return await ApplyBackupShieldAsync(account, settings, logger, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error applying shield: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TryApplyRechargeableShieldAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                var countOcrConfig = new OCRConfiguration
                {
                    ScaleFactor = 4,
                    CharacterWhitelist = "0123456789/",
                    MedianBlurKernelSize = 1
                };

                using var countOcr = new OCRService(logger, countOcrConfig);
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) return false;

                string countText = countOcr.ExtractTextFromScreenArea(screenshot, RechargeableCountRect);
                logger.LogInfo($"[{account.AccountName}] Rechargeable shield count text: '{countText}'");

                var match = Regex.Match(countText, @"(\d+)/\d+");
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int available) || available <= 0)
                {
                    logger.LogInfo($"[{account.AccountName}] No rechargeable shields available.");
                    _metrics.ShieldUnavailableErrors++;
                    return false;
                }

                // Click the count text area to use rechargeable shield
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, RechargeableCountRect))
                {
                    return false;
                }

                await Task.Delay(POLL_INTERVAL_MS * 2, cancellationToken);

                // Check for confirmation buttons after using rechargeable shield
                logger.LogInfo($"[{account.AccountName}] Attempting to find confirmation buttons for rechargeable shield...");
                string baseDir = AppContext.BaseDirectory;
                var confirmPath = Path.Combine(baseDir, "templates", "images", "ChangeAccount", "confirm.png");
                var buyUsePath = Path.Combine(_templateFolder, "buy-use.png");

                logger.LogInfo($"[{account.AccountName}] Looking for confirm button at: {confirmPath}");
                bool confirmFound = await ClickTemplateInAreaAsync(account.InstanceNumber, confirmPath, ConfirmButtonRect, logger, threshold: 0.5);
                logger.LogInfo($"[{account.AccountName}] Confirm button found: {confirmFound}");
                
                bool buyUseFound = false;
                
                if (!confirmFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Looking for buy-use button at: {buyUsePath}");
                    buyUseFound = await ClickTemplateInAreaAsync(account.InstanceNumber, buyUsePath, BuyUseButtonRect, logger, threshold: 0.65);
                    logger.LogInfo($"[{account.AccountName}] Buy-use button found: {buyUseFound}");
                    
                    if (buyUseFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] Buy-use button clicked, waiting 1 second then clicking confirmation at 357,781");
                        await Task.Delay(1000, cancellationToken);
                        await ClickAsync(account.InstanceNumber, logger, new Point(357, 781));
                        logger.LogInfo($"[{account.AccountName}] Clicked confirmation point at 357,781");
                    }
                }
                
                bool confirmed = confirmFound || buyUseFound;

                if (!confirmed)
                {
                    logger.LogInfo($"[{account.AccountName}] No confirmation button found for rechargeable shield - shield may have been applied directly");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Confirmation button clicked for rechargeable shield");
                }

                _metrics.RechargeableShieldsUsed++;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error applying rechargeable shield: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ApplyBackupShieldAsync(AccountSettings account, AutoShieldSettings settings, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Handle the case where no backup shield is configured
                if (settings.SelectedBackupShield == ShieldType.None)
                {
                    logger.LogInfo($"[{account.AccountName}] No backup shield configured. Cannot apply shield.");
                    return false;
                }

                Rectangle targetRect = settings.SelectedBackupShield switch
                {
                    ShieldType.TwoHour => TwoHourButtonRect,
                    ShieldType.EightHour => EightHourButtonRect,
                    ShieldType.TwentyFourHour => TwentyFourHourButtonRect,
                    ShieldType.SeventyTwoHour => SeventyTwoHourButtonRect,
                    _ => throw new InvalidOperationException($"Unsupported backup shield type: {settings.SelectedBackupShield}")
                };

                var useButtonPath = Path.Combine(_templateFolder, "use-button.png");
                if (!await ClickTemplateInAreaAsync(account.InstanceNumber, useButtonPath, targetRect, logger, threshold: 0.65))
                {
                    logger.LogError($"[{account.AccountName}] Could not find use button for {settings.SelectedBackupShield}");
                    _metrics.TemplateNotFoundErrors++;
                    return false;
                }

                await Task.Delay(POLL_INTERVAL_MS * 2, cancellationToken);

                // Try both confirmation buttons
                string baseDir = AppContext.BaseDirectory;
                var confirmPath = Path.Combine(baseDir, "templates", "images", "ChangeAccount", "confirm.png");
                var buyUsePath = Path.Combine(_templateFolder, "buy-use.png");

                logger.LogInfo($"[{account.AccountName}] Attempting to find confirmation buttons for backup shield...");
                logger.LogInfo($"[{account.AccountName}] Looking for confirm button at: {confirmPath}");
                bool confirmFound = await ClickTemplateInAreaAsync(account.InstanceNumber, confirmPath, ConfirmButtonRect, logger, threshold: 0.5);
                logger.LogInfo($"[{account.AccountName}] Confirm button found: {confirmFound}");
                
                bool buyUseFound = false;
                
                if (!confirmFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Looking for buy-use button at: {buyUsePath}");
                    buyUseFound = await ClickTemplateInAreaAsync(account.InstanceNumber, buyUsePath, BuyUseButtonRect, logger, threshold: 0.65);
                    logger.LogInfo($"[{account.AccountName}] Buy-use button found: {buyUseFound}");
                    
                    if (buyUseFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] Buy-use button clicked, waiting 1 second then clicking confirmation at 357,781");
                        await Task.Delay(1000, cancellationToken);
                        await ClickAsync(account.InstanceNumber, logger, new Point(357, 781));
                        logger.LogInfo($"[{account.AccountName}] Clicked confirmation point at 357,781");
                    }
                }
                
                bool confirmed = confirmFound || buyUseFound;

                if (!confirmed)
                {
                    logger.LogInfo($"[{account.AccountName}] Could not find confirmation button - checking if shield was applied anyway...");
                    
                    // Sometimes the shield is applied without needing confirmation
                    // Use OCR to check if a shield timer is now showing
                    var verificationShieldTime = await ReadShieldTimerAsync(account, logger, cancellationToken);
                    if (verificationShieldTime != null && verificationShieldTime.Value > TimeSpan.Zero)
                    {
                        logger.LogInfo($"[{account.AccountName}] Shield was successfully applied without confirmation. Timer shows: {verificationShieldTime.Value.TotalHours:F1}h");
                        
                        // Click back button to exit shield menu
                        var backButtonPath = Path.Combine(baseDir, "templates", "images", "recovery", "deploy-back.png");
                        bool backButtonClicked = await ClickTemplateAsync(account.InstanceNumber, backButtonPath, logger);
                        if (!backButtonClicked)
                        {
                            logger.LogWarning($"[{account.AccountName}] Could not find back button, shield menu may still be open.");
                        }
                        await Task.Delay(1000, cancellationToken); // Wait for navigation
                        
                        return true; // Shield was applied successfully
                    }
                    
                    logger.LogError($"[{account.AccountName}] No confirmation button found and no shield timer detected");
                    _metrics.ConfirmationFailedErrors++;
                    return false;
                }

                _metrics.BackupShieldsUsed++;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error applying backup shield: {ex.Message}");
                return false;
            }
        }

        private void UpdateMetricsAndSettings(AccountSettings account, AutoShieldSettings settings, TimeSpan newDuration)
        {
            settings.LastShieldActivatedTime = DateTime.UtcNow;
            settings.LastShieldDuration = newDuration;
            _metrics.TotalShieldsApplied++;
            _metrics.LastUpdated = DateTime.UtcNow;

            // Update the running average
            if (_metrics.AverageRemainingTime == TimeSpan.Zero)
            {
                _metrics.AverageRemainingTime = newDuration;
            }
            else
            {
                _metrics.AverageRemainingTime = TimeSpan.FromTicks(
                    (_metrics.AverageRemainingTime.Ticks + newDuration.Ticks) / 2);
            }

            // Update shield time manager
            ShieldTimeManager.Instance.UpdateShieldTime(account.InstanceNumber, settings.LastShieldActivatedTime.Value, newDuration);

            // Save updated settings
            SaveAutoShieldSettings(account, settings);
        }

        private AutoShieldSettings GetAutoShieldSettings(AccountSettings account)
        {
            // First try the new taskSettings format
            if (account.TaskSettings.TryGetValue("AutoShield", out var settingsJson))
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<AutoShieldSettings>(settingsJson)
                        ?? new AutoShieldSettings();
                }
                catch
                {
                    // Fall through to legacy format check
                }
            }

            // Fallback to legacy autoShieldSettings format using reflection
            try
            {
                var accountType = account.GetType();
                var autoShieldProperty = accountType.GetProperty("autoShieldSettings", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                
                if (autoShieldProperty != null)
                {
                    var legacySettings = autoShieldProperty.GetValue(account);
                    if (legacySettings != null)
                    {
                        // Convert legacy settings to new format
                        var json = System.Text.Json.JsonSerializer.Serialize(legacySettings);
                        var newSettings = System.Text.Json.JsonSerializer.Deserialize<AutoShieldSettings>(json);
                        
                        // Migrate to new format for future use
                        if (newSettings != null)
                        {
                            SaveAutoShieldSettings(account, newSettings);
                            return newSettings;
                        }
                    }
                }
            }
            catch
            {
                // If reflection fails, return defaults
            }

            return new AutoShieldSettings();
        }

        private void SaveAutoShieldSettings(AccountSettings account, AutoShieldSettings settings)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(settings);
            account.TaskSettings["AutoShield"] = json;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private async Task<bool> ClickTemplateAsync(int instanceNumber, string templatePath, LogService logger)
        {
            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);

            // Wait up to 2 seconds for the template to appear
            var (found, position) = await WaitForTemplateWithPathAsync(
                instanceNumber,
                templatePath,
                logger,
                timeoutMs: 2000,
                threshold: TEMPLATE_MATCH_THRESHOLD
            );

            if (!found) return false;

            return await ClickRandomInRectAsync(instanceNumber, logger, position);
        }

        private async Task<bool> ClickTemplateInAreaAsync(int instanceNumber, string templatePath, Rectangle searchArea, LogService logger, double threshold = TEMPLATE_MATCH_THRESHOLD)
        {
            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);

            // Wait up to 2 seconds for the template to appear in the specified area
            var (found, position) = await WaitForTemplateWithPathAsync(
                instanceNumber,
                templatePath,
                logger,
                timeoutMs: 2000,
                threshold: threshold,
                searchArea: searchArea
            );

            if (!found) return false;

            return await ClickRandomInRectAsync(instanceNumber, logger, position);
        }

        private void UpdateTaskStatusWithShieldTime(AccountSettings account, TimeSpan shieldDuration)
        {
            // This method is kept for potential future use
            // Shield duration is now included in TaskExecutionDetails message
        }

        /// <summary>
        /// Wait for a template to appear using full path, with timeout support
        /// </summary>
        private async Task<(bool found, Rectangle position)> WaitForTemplateWithPathAsync(
            int instanceNumber,
            string templatePath,
            LogService logger,
            int timeoutMs = 2000,
            double threshold = TEMPLATE_MATCH_THRESHOLD,
            Rectangle? searchArea = null)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            double bestConfidence = 0.0;
            int attemptCount = 0;
            
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                attemptCount++;
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    await Task.Delay(POLL_INTERVAL_MS);
                    continue;
                }

                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: threshold,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: true,
                    searchArea: searchArea
                );

                // Track best confidence seen
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                }

                if (found)
                {
                    logger.LogInfo($"Template {Path.GetFileName(templatePath)} found after {stopwatch.ElapsedMilliseconds}ms with confidence {confidence:F3} (attempt {attemptCount})");
                    return (true, matchRect);
                }

                // Wait before retrying, but not after the last attempt
                if (stopwatch.ElapsedMilliseconds + POLL_INTERVAL_MS < timeoutMs)
                {
                    await Task.Delay(POLL_INTERVAL_MS);
                }
            }

            logger.LogInfo($"Template {Path.GetFileName(templatePath)} not found after {timeoutMs}ms timeout. Best confidence: {bestConfidence:F3} (threshold: {threshold:F3}, attempts: {attemptCount})");
            return (false, Rectangle.Empty);
        }
    }
} 