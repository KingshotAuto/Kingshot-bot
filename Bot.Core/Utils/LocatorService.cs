using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Exceptions;
using Bot.Core.Tasks.Modules;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Drawing;

namespace Bot.Core.Utils
{
    public class LocatorService
    {
        private readonly LogService _logger;
        private readonly UnifiedTemplateMatchingService _templateMatcher;
        private readonly string _templateFolder;
        private readonly AccountSettings _account;
        private TaskType? _currentTaskType;

        public LocatorService(LogService logger, AccountSettings account, TaskType? currentTaskType = null)
        {
            _logger = logger;
            _account = account;
            _currentTaskType = currentTaskType;
            _templateMatcher = new UnifiedTemplateMatchingService(logger);

            // Get the application's base directory and combine with template path
            string baseDir = AppContext.BaseDirectory;
            _templateFolder = Path.Combine(baseDir, "templates", "images", "locator");

            // Ensure template directory exists
            if (!Directory.Exists(_templateFolder))
            {
                Directory.CreateDirectory(_templateFolder);
                _logger.LogInfo($"Created locator template folder: {_templateFolder}");
            }

            // Create README if it doesn't exist
            _ = CreateReadmeAsync().ConfigureAwait(false);
        }

        private async Task<bool> HandleMapNotFound(int instanceNumber, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"[{_account.AccountName}] Maps not found, initiating recovery process...");
            
            var recoveryTask = new RecoveryTask();
            if (_currentTaskType.HasValue)
            {
                recoveryTask.SetLastFailedTask(_currentTaskType.Value);
            }
            
            var result = await recoveryTask.ExecuteAsync(_account, _logger, cancellationToken);
            return result.Success;
        }

        private void HandleBotLost(string errorMessage)
        {
            _logger.LogError(errorMessage);
            
            // Throw a BotLostException that indicates recovery is needed
            throw new BotLostException(errorMessage) { NeedsRecovery = true };
        }

        /// <summary>
        /// Ensures the bot is in the specified view, navigating if necessary
        /// </summary>
        /// <param name="desiredView">The view the bot should be in</param>
        /// <param name="instanceNumber">LDPlayer instance number</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <returns>True if successfully in desired view, false otherwise</returns>
        public async Task<bool> EnsureViewAsync(ViewType desiredView, int instanceNumber, CancellationToken cancellationToken = default, int maxRetries = 3)
        {
            _logger.LogInfo($"[Locator] 🧭 Ensuring bot is in {desiredView} view...");

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInfo($"[Locator] 📍 Attempt {attempt}/{maxRetries}: Detecting current view...");

                ViewType currentView;
                try
                {
                    currentView = await DetectCurrentViewAsync(instanceNumber, cancellationToken);
                }
                catch (BotLostException)
                {
                    if (attempt == maxRetries)
                    {
                        HandleBotLost($"[Locator] 💥 Failed to detect view after {maxRetries} attempts - bot is lost!");
                        return false; // Should not be reached
                    }
                    _logger.LogInfo($"[Locator] ⏳ View not detected, waiting 2 seconds before retry...");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }
                
                if (currentView == desiredView)
                {
                    _logger.LogInfo($"[Locator] ✅ Already in {desiredView} view!");
                    return true;
                }

                _logger.LogInfo($"[Locator] 🗺️ Currently in {currentView}, navigating to {desiredView}...");
                
                if (await NavigateToViewAsync(currentView, desiredView, instanceNumber, cancellationToken))
                {
                    _logger.LogInfo($"[Locator] ✅ Successfully navigated to {desiredView} view!");
                    return true;
                }
                
                _logger.LogError($"[Locator] ❌ Failed to navigate to {desiredView} view on attempt {attempt}");
                
                if (attempt < maxRetries)
                {
                    _logger.LogInfo($"[Locator] ⏳ Waiting 2 seconds before retry...");
                    await Task.Delay(2000, cancellationToken);
                }
            }

            HandleBotLost($"[Locator] 💥 Failed to ensure {desiredView} view after {maxRetries} attempts!");
            return false; // Should not be reached
        }

        /// <summary>
        /// Detects which view the bot is currently in
        /// </summary>
        /// <param name="instanceNumber">LDPlayer instance number</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Current view type or throws BotLostException if cannot detect</returns>
        public async Task<ViewType> DetectCurrentViewAsync(int instanceNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"[Locator] Looking for templates in: {_templateFolder}");

                var adbController = await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger, cancellationToken);
                if (adbController == null)
                {
                    return await HandleNoViewDetected("[Locator] ❌ Could not create ADB controller");
                }

                var screenshot = await adbController!.TakeScreenshotAsync(cancellationToken);
                if (screenshot == null || screenshot.Length == 0)
                {
                    return await HandleNoViewDetected("[Locator] ❌ Failed to capture screenshot");
                }

                var baseViewPath = Path.Combine(_templateFolder, "base-view.png");
                if (File.Exists(baseViewPath))
                {
                    var (foundBase, _, confidenceBase) = _templateMatcher.MatchTemplate(screenshot, baseViewPath, instanceNumber, 0.7f);
                    if (foundBase)
                    {
                        _logger.LogInfo($"[Locator] 🏠 Detected BASE view (confidence: {confidenceBase:F3})");
                        return ViewType.BaseView;
                    }
                    else
                    {
                        _logger.LogInfo($"[Locator] Base view not detected - confidence {confidenceBase:F3} below threshold 0.7");
                    }
                }

                var mapViewPath = Path.Combine(_templateFolder, "map-view.png");
                if (File.Exists(mapViewPath))
                {
                    var (foundMap, _, confidenceMap) = _templateMatcher.MatchTemplate(screenshot, mapViewPath, instanceNumber, 0.7f);
                    if (foundMap)
                    {
                        _logger.LogInfo($"[Locator] 🗺️ Detected MAP view (confidence: {confidenceMap:F3})");
                        return ViewType.MapView;
                    }
                    else
                    {
                        _logger.LogInfo($"[Locator] Map view not detected - confidence {confidenceMap:F3} below threshold 0.7");
                    }
                }

                // Debug screenshot saving disabled for performance
                // try
                // {
                //     var screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "screenshots");
                //     Directory.CreateDirectory(screenshotsDir);
                //     var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                //     var debugImagePath = Path.Combine(screenshotsDir, $"locator_failed_{instanceNumber}_{timestamp}.png");
                //     await File.WriteAllBytesAsync(debugImagePath, screenshot, cancellationToken);
                //     _logger.LogWarning($"[Locator] 🕵️ Bot is lost. Saved a debug screenshot to: {debugImagePath}");
                // }
                // catch (Exception ex)
                // {
                //     _logger.LogError($"[Locator] 💥 Failed to save debug screenshot: {ex.Message}");
                // }

                return await HandleNoViewDetected("[Locator] ❌ Could not detect any known view - bot may be lost");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Locator] 💥 Error detecting current view: {ex.Message}");
                return await HandleNoViewDetected($"[Locator] Error detecting view: {ex.Message}");
            }
        }

        private async Task<ViewType> HandleNoViewDetected(string errorMessage)
        {
            _logger.LogError(errorMessage);
            
            // If we're being called from Recovery task, don't try to trigger recovery recursively
            if (_currentTaskType == TaskType.Recovery)
            {
                _logger.LogWarning($"[{_account.AccountName}] Already in recovery mode, skipping recursive recovery call");
                var exception = new BotLostException("Could not detect any known view during recovery - bot may be lost");
                exception.NeedsRecovery = false;
                throw exception;
            }
            
            // Try ad detection loop before triggering recovery
            const int MAX_AD_ATTEMPTS = 10; // Limit attempts to prevent infinite loop
            const int AD_CHECK_DELAY = 2000; // 2 second delay between attempts
            
            _logger.LogInfo($"[{_account.AccountName}] Starting ad detection loop (max {MAX_AD_ATTEMPTS} attempts)");
            
            for (int attempt = 1; attempt <= MAX_AD_ATTEMPTS; attempt++)
            {
                _logger.LogInfo($"[{_account.AccountName}] Ad detection attempt {attempt}/{MAX_AD_ATTEMPTS}");
                
                // Check for ads to close
                if (await TryCloseAdsAsync())
                {
                    _logger.LogInfo($"[{_account.AccountName}] Closed ad, checking for views again...");
                    await Task.Delay(AD_CHECK_DELAY);
                    
                    // Try detecting view again after closing ad
                    var viewResult = await TryDetectViewAsync();
                    if (viewResult != ViewType.Unknown)
                    {
                        _logger.LogInfo($"[{_account.AccountName}] ✅ Found {viewResult} view after closing ad!");
                        return viewResult;
                    }
                }
                else
                {
                    // No ads found, try view detection with current threshold
                    var viewResult = await TryDetectViewAsync();
                    if (viewResult != ViewType.Unknown)
                    {
                        _logger.LogInfo($"[{_account.AccountName}] ✅ Found {viewResult} view!");
                        return viewResult;
                    }
                    
                    _logger.LogInfo($"[{_account.AccountName}] No ads or views found, waiting before retry...");
                    await Task.Delay(AD_CHECK_DELAY);
                }
            }
            
            // If still no success after all attempts, trigger recovery
            _logger.LogError($"[{_account.AccountName}] Failed to find views or ads after {MAX_AD_ATTEMPTS} attempts, falling back to recovery");
            if (await HandleMapNotFound(_account.InstanceNumber, CancellationToken.None))
            {
                // If recovery succeeded, try detecting view again
                var adbController = await ADBConnectionManager.GetConnectionAsync(_account.InstanceNumber, _logger, CancellationToken.None);
                if (adbController != null)
                {
                    var screenshot = await adbController.TakeScreenshotAsync(CancellationToken.None);
                    if (screenshot != null && screenshot.Length > 0)
                    {
                        var baseViewPath = Path.Combine(_templateFolder, "base-view.png");
                        var mapViewPath = Path.Combine(_templateFolder, "map-view.png");

                        if (File.Exists(baseViewPath))
                        {
                            var (foundBase, _, _) = _templateMatcher.MatchTemplate(screenshot, baseViewPath, _account.InstanceNumber, 0.7);
                            if (foundBase) return ViewType.BaseView;
                        }

                        if (File.Exists(mapViewPath))
                        {
                            var (foundMap, _, _) = _templateMatcher.MatchTemplate(screenshot, mapViewPath, _account.InstanceNumber, 0.7);
                            if (foundMap) return ViewType.MapView;
                        }
                    }
                }
            }

            // If we get here, recovery failed or didn't find a view
            throw new BotLostException(errorMessage) { NeedsRecovery = true };
        }

        /// <summary>
        /// Navigates from current view to desired view
        /// </summary>
        private async Task<bool> NavigateToViewAsync(ViewType currentView, ViewType desiredView, int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                var adbController = await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger, cancellationToken);
                if (adbController == null)
                {
                    _logger.LogError("[Locator] ❌ Could not create ADB controller for navigation");
                    return false;
                }

                string targetImageName;
                string verifyImageName;

                if (currentView == ViewType.BaseView && desiredView == ViewType.MapView)
                {
                    targetImageName = "base-view.png";
                    verifyImageName = "map-view.png";
                    _logger.LogInfo("[Locator] 🗺️ Need to go to map view, clicking base-view.png...");
                }
                else if (currentView == ViewType.MapView && desiredView == ViewType.BaseView)
                {
                    targetImageName = "map-view.png";
                    verifyImageName = "base-view.png";
                    _logger.LogInfo("[Locator] 🏠 Need to go to base view, clicking map-view.png...");
                }
                else
                {
                    _logger.LogError($"[Locator] ❌ Invalid navigation: {currentView} -> {desiredView}");
                    return false;
                }
                
                var targetImagePath = Path.Combine(_templateFolder, targetImageName);
                var verifyImagePath = Path.Combine(_templateFolder, verifyImageName);

                var screenshot = await adbController.TakeScreenshotAsync(cancellationToken);
                if (screenshot == null || screenshot.Length == 0)
                {
                    _logger.LogError("[Locator] ❌ Failed to capture screenshot for navigation");
                    return false;
                }

                _logger.LogInfo($"[Locator] 🔍 Attempting to match template: {targetImageName}");
                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(screenshot, targetImagePath, instanceNumber, 0.7f);
                if (!found)
                {
                    _logger.LogError($"[Locator] ❌ Could not find {targetImageName} for navigation (confidence: {confidence:F3} < threshold: 0.7)");
                    // await SaveDebugScreenshot(screenshot, instanceNumber, $"navigation_failed_{targetImageName}_conf{confidence:F3}"); // DISABLED FOR PERFORMANCE
                    return await HandleMapNotFound(instanceNumber, cancellationToken);
                }
                _logger.LogInfo($"[Locator] ✅ Found {targetImageName} with confidence: {confidence:F3}");

                var clickPoint = matchRect.GetCenter();
                _logger.LogInfo($"[Locator] 🖱️ Clicking center location ({clickPoint.X}, {clickPoint.Y}) within {targetImageName} bounds {matchRect}");
                await adbController.TapAsync(clickPoint.X, clickPoint.Y, cancellationToken);
                
                await Task.Delay(1000, cancellationToken);

                _logger.LogInfo($"[Locator] 👀 Looking for {verifyImageName} for 5 seconds to verify navigation...");
                
                var startTime = DateTime.Now;
                while ((DateTime.Now - startTime).TotalSeconds < 5)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    screenshot = await adbController.TakeScreenshotAsync(cancellationToken);
                    if (screenshot != null && screenshot.Length > 0)
                    {
                        var (foundVerify, _, confidenceVerify) = _templateMatcher.MatchTemplate(screenshot, verifyImagePath, instanceNumber, 0.7f);
                        if (foundVerify)
                        {
                            _logger.LogInfo($"[Locator] ✅ Found {verifyImageName}! Navigation successful (confidence: {confidenceVerify:F3})");
                            return true;
                        }
                        else if ((DateTime.Now - startTime).TotalSeconds >= 4.5)
                        {
                            // Log confidence on last attempt before timeout
                            _logger.LogWarning($"[Locator] {verifyImageName} not found - confidence: {confidenceVerify:F3} < threshold: 0.7");
                        }
                    }
                    
                    await Task.Delay(500, cancellationToken);
                }

                _logger.LogError($"[Locator] ❌ Timed out waiting for {verifyImageName} after navigation");
                if (screenshot != null)
                {
                    // await SaveDebugScreenshot(screenshot, instanceNumber, $"navigation_timeout_{verifyImageName}"); // DISABLED FOR PERFORMANCE
                }
                return await HandleMapNotFound(instanceNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Locator] 💥 Error navigating view: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to close any visible ads or find recovery buttons on the screen
        /// </summary>
        /// <returns>True if an ad or recovery button was found and clicked, false otherwise</returns>
        private async Task<bool> TryCloseAdsAsync()
        {
            try
            {
                var adbController = await ADBConnectionManager.GetConnectionAsync(_account.InstanceNumber, _logger, CancellationToken.None);
                if (adbController == null)
                {
                    _logger.LogError("[Locator] Could not get ADB controller for ad detection");
                    return false;
                }

                var screenshot = await adbController.TakeScreenshotAsync(CancellationToken.None);
                if (screenshot == null || screenshot.Length == 0)
                {
                    _logger.LogError("[Locator] Failed to capture screenshot for ad detection");
                    return false;
                }

                // Define ad close search area (top portion of screen)
                var adCloseArea = new Rectangle(0, 0, 720, 300);
                
                // Define ad close image paths
                var baseDir = AppContext.BaseDirectory;
                var startupFolder = Path.Combine(baseDir, "templates", "images", "startup");
                var adClosePaths = new[]
                {
                    Path.Combine(startupFolder, "ad-close.png"),
                    Path.Combine(startupFolder, "ad-close2.png"),
                    Path.Combine(startupFolder, "ad-close3.png")
                };

                // Check for each ad close image
                foreach (var adClosePath in adClosePaths)
                {
                    if (File.Exists(adClosePath))
                    {
                        var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(screenshot, adClosePath, _account.InstanceNumber, 0.7, null, false, adCloseArea);
                        if (found)
                        {
                            _logger.LogInfo($"[Locator] 🎯 Found ad close button {Path.GetFileName(adClosePath)} (confidence: {confidence:F3}), clicking...");
                            var clickPoint = matchRect.GetCenter();
                            await adbController.TapAsync(clickPoint.X, clickPoint.Y, CancellationToken.None);
                            return true;
                        }
                    }
                }

                // Check for recovery back buttons (full screen search)
                var recoveryFolder = Path.Combine(baseDir, "templates", "images", "recovery");
                var backButtonPaths = new[]
                {
                    Path.Combine(recoveryFolder, "back-arrow.png"),
                    Path.Combine(recoveryFolder, "deploy-back.png")
                };

                foreach (var backButtonPath in backButtonPaths)
                {
                    if (File.Exists(backButtonPath))
                    {
                        var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(screenshot, backButtonPath, _account.InstanceNumber, 0.7);
                        if (found)
                        {
                            _logger.LogInfo($"[Locator] 🔙 Found recovery back button {Path.GetFileName(backButtonPath)} (confidence: {confidence:F3}), clicking...");
                            var clickPoint = matchRect.GetCenter();
                            await adbController.TapAsync(clickPoint.X, clickPoint.Y, CancellationToken.None);
                            return true;
                        }
                    }
                }

                _logger.LogInfo("[Locator] No ad close buttons or recovery back buttons detected");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Locator] Error during ad detection: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to detect current view without throwing exceptions
        /// </summary>
        /// <returns>ViewType.BaseView, ViewType.MapView, or ViewType.Unknown</returns>
        private async Task<ViewType> TryDetectViewAsync()
        {
            try
            {
                var adbController = await ADBConnectionManager.GetConnectionAsync(_account.InstanceNumber, _logger, CancellationToken.None);
                if (adbController == null)
                {
                    return ViewType.Unknown;
                }

                var screenshot = await adbController.TakeScreenshotAsync(CancellationToken.None);
                if (screenshot == null || screenshot.Length == 0)
                {
                    return ViewType.Unknown;
                }

                var baseViewPath = Path.Combine(_templateFolder, "base-view.png");
                if (File.Exists(baseViewPath))
                {
                    var (foundBase, _, confidenceBase) = _templateMatcher.MatchTemplate(screenshot, baseViewPath, _account.InstanceNumber, 0.7);
                    if (foundBase)
                    {
                        _logger.LogInfo($"[Locator] 🏠 Detected BASE view (confidence: {confidenceBase:F3})");
                        return ViewType.BaseView;
                    }
                }

                var mapViewPath = Path.Combine(_templateFolder, "map-view.png");
                if (File.Exists(mapViewPath))
                {
                    var (foundMap, _, confidenceMap) = _templateMatcher.MatchTemplate(screenshot, mapViewPath, _account.InstanceNumber, 0.7);
                    if (foundMap)
                    {
                        _logger.LogInfo($"[Locator] 🗺️ Detected MAP view (confidence: {confidenceMap:F3})");
                        return ViewType.MapView;
                    }
                }

                return ViewType.Unknown;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Locator] Error during view detection: {ex.Message}");
                return ViewType.Unknown;
            }
        }

        /// <summary>
        /// Creates an ADB controller for the specified instance
        /// </summary>
        private async Task<ADBController?> GetADBControllerAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                return await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Locator] Error getting ADB controller: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves a debug screenshot with descriptive filename
        /// </summary>
        private async Task SaveDebugScreenshot(byte[] screenshot, int instanceNumber, string description)
        {
            try
            {
                var screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "screenshots");
                Directory.CreateDirectory(screenshotsDir);
                
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var safeDescription = description.Replace(".", "_").Replace(" ", "_").Replace(":", "");
                var filename = $"locator_{instanceNumber}_{timestamp}_{safeDescription}.png";
                var filePath = Path.Combine(screenshotsDir, filename);
                
                await File.WriteAllBytesAsync(filePath, screenshot);
                _logger.LogInfo($"[Locator] 📸 Saved debug screenshot: {filename}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Locator] Failed to save debug screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates README file with instructions for locator images
        /// </summary>
        public async Task CreateReadmeAsync()
        {
            var readmePath = Path.Combine(_templateFolder, "README.md");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath,
                    "Locator Module Image Templates:\n\n" +
                    "Required images for view detection and navigation:\n\n" +
                    "1. base-view.png - Clickable UI element visible in base/home view\n" +
                    "   (e.g., world map button, campaign button - clicking takes you TO map view)\n\n" +
                    "2. map-view.png - Clickable UI element visible in world map view\n" +
                    "   (e.g., home button, base button - clicking takes you TO base view)\n\n" +
                    "Important Navigation Logic:\n" +
                    "- Only ONE image will be visible at any time\n" +
                    "- When in BASE view: base-view.png is visible, click it to go to MAP view\n" +
                    "- When in MAP view: map-view.png is visible, click it to go to BASE view\n" +
                    "- After clicking, the bot verifies navigation by looking for the OTHER image\n\n" +
                    "Instructions:\n" +
                    "- Take screenshots of buttons/UI elements that switch between views\n" +
                    "- Crop to unique, clickable elements (buttons, icons, etc.)\n" +
                    "- Avoid static UI elements that don't trigger navigation\n" +
                    "- Images should be at least 50x50 pixels\n" +
                    "- Test at different zoom levels and screen resolutions\n\n" +
                    "How it works:\n" +
                    "1. Detects current view by checking which image is visible\n" +
                    "2. Clicks random area within the visible navigation element\n" +
                    "3. Waits 1 second for transition\n" +
                    "4. Verifies successful navigation by looking for the other image\n" +
                    "5. Provides retry logic for failed navigation attempts");

                _logger.LogInfo($"Created locator images README at: {readmePath}");
            }
        }
    }
} 