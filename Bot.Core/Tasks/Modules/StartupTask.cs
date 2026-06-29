using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Threading;
using System;
using Bot.Core.Exceptions;
using System.Linq;
using System.IO;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Handles the complete startup sequence for the game, including:
    /// 1. Finding and launching the Kingshot app
    /// 2. Handling loading screens
    /// 3. Dismissing pop-ups and ads
    /// 4. Verifying the game state
    /// </summary>
    public class StartupTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.Startup;
        public override string Name => "Startup Check";
        
        // Specifies the folder containing image templates for startup sequence
        protected override string GetImageFolderName() => "startup";

        // Event for updating the GUI with current phase status
        public static event Action<int, string, string>? OnPhaseUpdate;
        
        private static readonly Rectangle LoadingScreenArea = new Rectangle(74, 173, 587, 197);  // Loading screen search area
        
        /// <summary>
        /// Verifies if the LDPlayer instance is running before attempting startup
        /// Makes up to 3 attempts with 3-second delays between attempts
        /// </summary>
        public override async Task<bool> CanExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default)
        {
            logger.LogInfo($"[{account.AccountName}] 🔍 Checking if startup task can execute...");
            
            const int CHECK_DURATION_MS = 30000; // 30 seconds total
            const int CHECK_INTERVAL_MS = 3000;  // Check every 3 seconds
            var startTime = DateTime.UtcNow;
            var attemptCount = 0;
            
            // Startup task should always be able to execute if instance is running
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < CHECK_DURATION_MS && !cancellationToken.IsCancellationRequested)
            {
                attemptCount++;
                logger.LogInfo($"[{account.AccountName}] Instance running check attempt {attemptCount} (elapsed time: {(DateTime.UtcNow - startTime).TotalSeconds:F1}s)...");
                
                var isRunning = await IsInstanceRunningAsync(account.InstanceNumber, logger);
                if (isRunning)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ LDPlayer instance {account.InstanceNumber} is running");
                    return true;
                }
                
                logger.LogInfo($"[{account.AccountName}] ⏳ Instance not detected as running, waiting {CHECK_INTERVAL_MS/1000} seconds before retry...");
                await Task.Delay(CHECK_INTERVAL_MS, cancellationToken);
            }
            
            logger.LogError($"[{account.AccountName}] ❌ LDPlayer instance {account.InstanceNumber} is not running after {attemptCount} attempts ({(DateTime.UtcNow - startTime).TotalSeconds:F1}s)");
            return false;
        }
        
        /// <summary>
        /// Main startup sequence that handles all phases of game initialization:
        /// 1. Find and click Kingshot app
        /// 2. Handle loading screens
        /// 3. Handle main menu and pop-ups
        /// 4. Handle welcome back screen
        /// 5. Verify final game state
        /// </summary>
        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            logger.LogInfo($"[{account.AccountName}] 🔍 Starting complete startup sequence{(isReRun ? " (Re-run)" : "")}");
            UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Initializing");
            
            try
            {
                // Check for cancellation before starting
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"[{account.AccountName}] 🛑 Startup sequence cancelled before start");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
                    return TaskExecutionDetails.Failed("Startup sequence was cancelled");
                }

                // Phase 1: Find and click Kingshot app icon
                NotifyProgress(userNotifications, "Finding game app", 10);
                logger.LogInfo($"[{account.AccountName}] Starting Phase 1: Finding Kingshot app...");
                if (!await FindAndClickKingshotApp(account, logger, cancellationToken, userNotifications))
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to find/click Kingshot app");
                    return FailDetection("Phase 1: Finding game app", "Kingshot app icon", recoveryNeeded: false);
                }

                // Check for cancellation after each major phase
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"[{account.AccountName}] 🛑 Startup sequence cancelled after app launch");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
                    return TaskExecutionDetails.Failed("Startup sequence was cancelled");
                }

                // Phase 2: Wait for loading screen and let it disappear
                logger.LogInfo($"[{account.AccountName}] 🎯 Phase 2: Handling loading screen...");
                if (!await WaitForLoadingToCompleteInternal(account, logger, cancellationToken))
                {
                    // Non-critical, log and proceed if loading screen handling fails
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Proceeding despite potential loading screen issues.");
                }

                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"[{account.AccountName}] 🛑 Startup sequence cancelled after loading screen");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
                    return TaskExecutionDetails.Failed("Startup sequence was cancelled");
                }

                // --- Begin new post-loading loop logic ---
                const int POST_LOAD_DURATION_MS = 60000; // 1 minute timeout
                const int POST_LOAD_INTERVAL_MS = 2000;  // Check every 2 seconds
                bool inKnownView = false;

                // Define search areas as rectangles
                var viewDetectionArea = new Rectangle(589, 1157, 125, 108);  // 589,1157 to 714,1265
                var closeButtonArea = new Rectangle(0, 0, 720, 300);      // 0,0 to 720,300
                var adCloseArea = closeButtonArea;            // Use same area as closeButtonArea

                var startTime = DateTime.UtcNow;
                var attemptCount = 0;

                while ((DateTime.UtcNow - startTime).TotalMilliseconds < POST_LOAD_DURATION_MS && !inKnownView && !cancellationToken.IsCancellationRequested)
                {
                    attemptCount++;
                    logger.LogInfo($"[{account.AccountName}] 🔄 Post-loading sequence attempt {attemptCount} (elapsed time: {(DateTime.UtcNow - startTime).TotalSeconds:F1}s)...");

                    // Wait a bit for any cloud transitions to clear
                    await Task.Delay(2000, cancellationToken);

                    // Use locator folder for map/base view images
                    var locatorFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator");
                    var mapViewPath = Path.Combine(locatorFolder, "map-view.png");
                    var baseViewPath = Path.Combine(locatorFolder, "base-view.png");

                    // Execute image checks sequentially to prevent resource thrashing
                    logger.LogInfo($"[{account.AccountName}] 🔍 Running sequential checks for UI elements...");
                    
                    // Take one screenshot and reuse it for all checks
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        logger.LogWarning($"[{account.AccountName}] Failed to take screenshot, skipping UI checks");
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    // Check for base/map view (game ready state)
                    bool baseViewFound = await WaitForImageAsync(baseViewPath, account.InstanceNumber, logger, 
                        cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: viewDetectionArea);
                    
                    bool mapViewFound = false;
                    if (!baseViewFound)
                    {
                        mapViewFound = await WaitForImageAsync(mapViewPath, account.InstanceNumber, logger, 
                            cancellationToken, timeoutMs: 500, threshold: 0.7, searchArea: viewDetectionArea);
                    }

                    // Only check for ads if game isn't ready
                    var adResult1 = (found: false, position: Rectangle.Empty, confidence: 0.0);
                    var adResult2 = (found: false, position: Rectangle.Empty, confidence: 0.0);
                    var adResult3 = (found: false, position: Rectangle.Empty, confidence: 0.0);
                    var defenceResult = (found: false, position: Rectangle.Empty, confidence: 0.0);

                    if (!baseViewFound && !mapViewFound)
                    {
                        adResult1 = await WaitForImageWithPositionAsync("ad-close.png", account.InstanceNumber, logger,
                            cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: adCloseArea);

                        if (!adResult1.found)
                        {
                            adResult2 = await WaitForImageWithPositionAsync("ad-close2.png", account.InstanceNumber, logger,
                                cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: adCloseArea);
                        }

                        if (!adResult1.found && !adResult2.found)
                        {
                            adResult3 = await WaitForImageWithPositionAsync("ad-close3.png", account.InstanceNumber, logger,
                                cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: adCloseArea);
                        }

                        if (!adResult1.found && !adResult2.found && !adResult3.found)
                        {
                            defenceResult = await WaitForImageWithPositionAsync("defence.png", account.InstanceNumber, logger,
                                cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: adCloseArea);
                        }
                    }

                    bool adFound = adResult1.found;
                    bool adFound2 = adResult2.found;
                    bool adFound3 = adResult3.found;
                    bool defenceFound = defenceResult.found;

                    // Check if base view was found early
                    if (baseViewFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Base view detected! Game is ready.");
                        inKnownView = true;
                        break;
                    }

                    logger.LogInfo($"[{account.AccountName}] Sequential check results - Ad: {adFound}, Ad2: {adFound2}, Ad3: {adFound3}, Defence: {defenceFound}, Map: {mapViewFound}, Base: {baseViewFound}");

                    // Process results based on priority
                    // Priority 1: Ads and Defence
                    if (adFound || adFound2 || adFound3 || defenceFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] 🖱️ Found ad close button or defence image, clicking on detected position...");

                        // Find the highest confidence ad detection and click on its position
                        var adResults = new[] { adResult1, adResult2, adResult3, defenceResult };
                        var bestAd = adResults.Where(r => r.found).OrderByDescending(r => r.confidence).FirstOrDefault();
                        
                        if (bestAd.found)
                        {
                            logger.LogInfo($"[{account.AccountName}] Clicking on best ad match at {bestAd.position} with confidence {bestAd.confidence:F3}");
                            await ClickRandomInRectAsync(account.InstanceNumber, logger, bestAd.position);
                        }
                        else
                        {
                            // Fallback to general close area if no position found
                            logger.LogInfo($"[{account.AccountName}] No position found, clicking general close area");
                            await ClickRandomInRectAsync(account.InstanceNumber, logger, closeButtonArea);
                        }
                        
                        await Task.Delay(2000, cancellationToken);
                        continue; // Go to next attempt to verify view
                    }

                    // Priority 2: Map/Base view (game is ready)
                    if (mapViewFound || baseViewFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found {(mapViewFound ? "map" : "base")} view! Skipping remaining checks.");
                        inKnownView = true;
                        break;
                    }

                    // No UI elements found
                    if ((DateTime.UtcNow - startTime).TotalMilliseconds < POST_LOAD_DURATION_MS)
                    {
                        logger.LogInfo($"[{account.AccountName}] ⏳ No UI elements detected, waiting before next attempt...");
                        await Task.Delay(POST_LOAD_INTERVAL_MS, cancellationToken);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInfo($"[{account.AccountName}] 🛑 Startup sequence was cancelled");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
                    return TaskExecutionDetails.Failed("Startup sequence was cancelled");
                }

                if (!inKnownView)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to reach a known view after {attemptCount} attempts ({(DateTime.UtcNow - startTime).TotalSeconds:F1}s)");
                    logger.LogInfo($"[{account.AccountName}] 🔄 Initiating recovery sequence...");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Recovery Needed");
                    return TaskExecutionDetails.FailedWith(
                        FailureCategory.GameState,
                        "Phase 3: Post-loading",
                        "Game did not reach a known view after loading",
                        customHint: "Game may be stuck on a dialog or ad - recovery will be attempted",
                        recoveryNeeded: true);
                }

                // --- End new post-loading loop logic ---

                // Phase 5: Final verification to ensure bot is in a known state
                NotifyProgress(userNotifications, "Verifying game state", 90);
                logger.LogInfo($"[{account.AccountName}] Starting Phase 5: Final state verification...");
                if (!await VerifyGameState(account, logger, cancellationToken))
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to verify game state after recovery attempts.");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Verification Failed");
                    return FailGameState("Phase 5: Verification", "Game state could not be verified after startup", recoveryNeeded: true);
                }

                // Phase 6: Startup complete
                logger.LogInfo($"[{account.AccountName}] ✅ 🎉 STARTUP PHASE COMPLETE! 🎉");
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Startup Complete");
                
                await Task.Delay(GameCoordinates.Delays.AfterCompletion, cancellationToken); // Brief delay after completion
                
                return TaskExecutionDetails.Succeeded();
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] 🛑 Startup sequence was cancelled");
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
                return TaskExecutionDetails.Failed("Startup sequence was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] 💥 Exception during startup sequence: {ex.Message}");
                logger.LogError($"[{account.AccountName}] Stack trace: {ex.StackTrace}");
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Error");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Startup error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }

        /// <summary>
        /// Phase 1: Launches the Kingshot app using dnconsole.exe runapp command
        /// </summary>
        private async Task<bool> FindAndClickKingshotApp(AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Send GUI notification
                userNotifications?.ShowStatus("Launching Kingshot App", NotificationType.Info);
                
                logger.LogInfo($"[{account.AccountName}] Launching Kingshot App");
                logger.LogInfo($"[{account.AccountName}] ⏳ Waiting 5 seconds to ensure LDPlayer is fully ready...");
                await Task.Delay(5000, cancellationToken); // Wait 5 seconds to ensure LDPlayer is ready
                
                logger.LogInfo($"[{account.AccountName}] 🚀 Launching Kingshot app using dnconsole.exe...");
                
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                if (!File.Exists(ldConsolePath))
                {
                    logger.LogError($"[{account.AccountName}] ❌ dnconsole.exe not found at: {ldConsolePath}");
                    return false;
                }
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = ldConsolePath,
                    Arguments = $"runapp --index {account.InstanceNumber} --packagename com.run.tower.defense",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                logger.LogInfo($"[{account.AccountName}] Executing: {ldConsolePath} runapp --index {account.InstanceNumber} --packagename com.run.tower.defense");
                
                using var process = Process.Start(startInfo);
                if (process == null) 
                { 
                    logger.LogError($"[{account.AccountName}] ❌ Failed to start dnconsole process");
                    return false; 
                }
                
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                string error = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                
                logger.LogInfo($"[{account.AccountName}] dnconsole exit code: {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(output)) 
                    logger.LogInfo($"[{account.AccountName}] dnconsole output: {output.Trim()}");
                if (!string.IsNullOrWhiteSpace(error)) 
                    logger.LogError($"[{account.AccountName}] dnconsole error: {error.Trim()}");
                
                if (process.ExitCode == 0)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully launched Kingshot app");
                    logger.LogInfo($"[{account.AccountName}] Waiting 5 seconds before checking for loading menu...");
                    await Task.Delay(5000, cancellationToken); // Wait 5 seconds before looking for loading menu
                    return true;
                }
                else
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to launch Kingshot app (exit code: {process.ExitCode})");
                    return false;
                }
            }
            catch (OperationCanceledException) 
            {
                logger.LogInfo($"[{account.AccountName}] 🛑 App launch cancelled");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] 💥 Exception launching Kingshot app: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Phase 2: Handles the loading screen that appears when starting the game
        /// Uses polling approach to check for loading screen every 1 second for up to 15 seconds
        /// </summary>
        private async Task<bool> WaitForLoadingToCompleteInternal(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int POLL_INTERVAL_MS = 1000; // Check every 1000ms (1 second)
            const int INITIAL_WAIT_TIME_MS = 15000; // Initial 15 seconds wait time
            const int EXTENDED_WAIT_TIME_MS = 30000; // Additional 30 seconds for extended search
            var startTime = DateTime.UtcNow;
            bool loadingScreenFound = false;
            string detectedLoadingScreen = string.Empty;

            // 1. Poll for loading screen appearance (up to 15 seconds)
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < INITIAL_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError($"[{account.AccountName}] Failed to get screenshot for loading check");
                    return false;
                }

                // Check for both loading screen variants
                var loadingScreen1Found = await WaitForImageAsync(
                    "loading-screen.png",
                    account.InstanceNumber,
                    logger,
                    cancellationToken,
                    timeoutMs: POLL_INTERVAL_MS,
                    threshold: 0.5,
                    useEnhancedMatching: true,
                    searchArea: LoadingScreenArea
                );

                var loadingScreen2Found = await WaitForImageAsync(
                    "loading-screen2.png",
                    account.InstanceNumber,
                    logger,
                    cancellationToken,
                    timeoutMs: POLL_INTERVAL_MS,
                    threshold: 0.5,
                    useEnhancedMatching: true,
                    searchArea: LoadingScreenArea
                );

                loadingScreenFound = loadingScreen1Found || loadingScreen2Found;
                if (loadingScreenFound)
                {
                    detectedLoadingScreen = loadingScreen1Found ? "loading-screen.png" : "loading-screen2.png";
                    logger.LogInfo($"[{account.AccountName}] {detectedLoadingScreen} detected after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms, waiting for it to disappear...");
                    break;
                }

                await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
            }

            // 2. If loading screen was not found after 15 seconds, start looking for subsequent UI elements
            if (!loadingScreenFound)
            {
                logger.LogInfo($"[{account.AccountName}] Loading screen not detected after {INITIAL_WAIT_TIME_MS}ms, starting extended search...");
                
                // Define search areas for subsequent UI elements
                var viewDetectionArea = new Rectangle(589, 1157, 125, 108);  // 589,1157 to 714,1265
                
                while ((DateTime.UtcNow - startTime).TotalMilliseconds < (INITIAL_WAIT_TIME_MS + EXTENDED_WAIT_TIME_MS) && !cancellationToken.IsCancellationRequested)
                {
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                        continue;
                    }

                    // Check for both loading screens again
                    var loadingScreen1Found = await WaitForImageAsync(
                        "loading-screen.png",
                        account.InstanceNumber,
                        logger,
                        cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS,
                        threshold: 0.5,
                        useEnhancedMatching: true,
                        searchArea: LoadingScreenArea
                    );

                    var loadingScreen2Found = await WaitForImageAsync(
                        "loading-screen2.png",
                        account.InstanceNumber,
                        logger,
                        cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS,
                        threshold: 0.5,
                        useEnhancedMatching: true,
                        searchArea: LoadingScreenArea
                    );

                    loadingScreenFound = loadingScreen1Found || loadingScreen2Found;
                    if (loadingScreenFound)
                    {
                        detectedLoadingScreen = loadingScreen1Found ? "loading-screen.png" : "loading-screen2.png";
                        logger.LogInfo($"[{account.AccountName}] {detectedLoadingScreen} detected during extended search at {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                        break;
                    }


                    // Check for base/map view indicators
                    var locatorFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator");
                    var mapViewPath = Path.Combine(locatorFolder, "map-view.png");
                    var baseViewPath = Path.Combine(locatorFolder, "base-view.png");

                    var mapViewFound = await WaitForImageAsync(
                        mapViewPath,
                        account.InstanceNumber,
                        logger,
                        cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS,
                        threshold: 0.5,
                        searchArea: viewDetectionArea
                    );

                    var baseViewFound = await WaitForImageAsync(
                        baseViewPath,
                        account.InstanceNumber,
                        logger,
                        cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS,
                        threshold: 0.65,
                        searchArea: viewDetectionArea
                    );

                    if (mapViewFound || baseViewFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] {(mapViewFound ? "Map" : "Base")} view detected during extended search, proceeding with normal flow");
                        return true;
                    }

                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }

                if (!loadingScreenFound)
                {
                    logger.LogInfo($"[{account.AccountName}] No loading screen or subsequent UI elements detected after extended search ({INITIAL_WAIT_TIME_MS + EXTENDED_WAIT_TIME_MS}ms), proceeding with normal flow");
                    return true;
                }
            }

            // 3. If loading screen was found (either initially or during extended search), wait for it to disappear
            if (loadingScreenFound)
            {
                const int MAX_LOADING_TIME = 120; // 2 minutes
                var loadingStartTime = DateTime.UtcNow;
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null)
                    {
                        logger.LogError($"[{account.AccountName}] Failed to get screenshot for loading check");
                        return false;
                    }

                    // Check if either loading screen is still visible
                    var loadingScreen1Found = await WaitForImageAsync(
                        "loading-screen.png",
                        account.InstanceNumber,
                        logger,
                        cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS,
                        threshold: 0.5,
                        useEnhancedMatching: true,
                        searchArea: LoadingScreenArea
                    );

                    var loadingScreen2Found = await WaitForImageAsync(
                        "loading-screen2.png",
                        account.InstanceNumber,
                        logger,
                        cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS,
                        threshold: 0.5,
                        useEnhancedMatching: true,
                        searchArea: LoadingScreenArea
                    );

                    var stillLoading = loadingScreen1Found || loadingScreen2Found;

                    if (!stillLoading)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Loading screen no longer detected");
                        logger.LogInfo($"[{account.AccountName}] ✅ Loading complete after {(DateTime.UtcNow - loadingStartTime).TotalSeconds:F1} seconds");
                        return true;
                    }

                    if ((DateTime.UtcNow - loadingStartTime).TotalSeconds > MAX_LOADING_TIME)
                    {
                        logger.LogError($"[{account.AccountName}] ❌ Loading timeout after {MAX_LOADING_TIME} seconds");
                        return false;
                    }

                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }
            }

            return false;
        }

        public async Task<bool> WaitForLoadingToComplete(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // Wait for loading screen to complete
            if (!await WaitForLoadingToCompleteInternal(account, logger, cancellationToken))
            {
                return false;
            }
            
            // After loading, run the same validation as the full startup task
            // This ensures ads are dismissed, popups are handled, and we're in the correct view
            var locator = new LocatorService(logger, account);
            if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
            {
                logger.LogWarning($"[{account.AccountName}] Failed to validate view after loading completion");
                return false;
            }
            
            return true;
        }


        /// <summary>
        /// Phase 5: Verifies the final game state after startup sequence
        /// Makes up to 3 attempts to verify the game is in a known state
        /// If verification fails, attempts to recover by re-running phases 3 and 4
        /// </summary>
        private async Task<bool> VerifyGameState(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int maxTimeoutMs = 10000; // 10 seconds
            const int checkIntervalMs = 500; // 0.5 seconds
            var locator = new LocatorService(logger, account);
            var startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < maxTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, $"Verifying State ({(DateTime.UtcNow - startTime).TotalSeconds:F1}s/10s)");

                try
                {
                    ViewType currentView = await locator.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);
                    if (currentView == ViewType.BaseView || currentView == ViewType.MapView)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Game state verified. In {currentView}.");
                        return true;
                    }
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Detected view is {currentView}, but expected BaseView or MapView. Retrying...");
                }
                catch (BotLostException)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Bot is not in a known view (Base or Map). Retrying...");
                }

                await Task.Delay(checkIntervalMs, cancellationToken);
            }

            logger.LogError($"[{account.AccountName}] ❌ Failed to verify game state after {maxTimeoutMs/1000} seconds");
            return await IsInCastleViewAsync(account, logger, cancellationToken); // Final fallback check
        }



        /// <summary>
        /// Updates the GUI with the current phase status
        /// </summary>
        private static void UpdatePhaseStatus(int instanceNumber, string accountName, string phase)
        {
            OnPhaseUpdate?.Invoke(instanceNumber, accountName, phase);
        }
        
        /// <summary>
        /// Initializes the startup task by creating a README file with instructions
        /// for required image templates and their usage
        /// </summary>
        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath, 
                    "Startup Image Templates:\n\n" +
                    "Required images for startup sequence:\n\n" +
                    "1. kingshot-app.png - The game app icon on home screen\n" +
                    "2. loading-screen.png - Loading screen that appears when starting game\n" +
                    "3. loading-screen2.png - Alternative loading screen (optional)\n" +
                    "4. ad-close.png, ad-close2.png, ad-close3.png - Ad close buttons\n\n" +
                    "Instructions:\n" +
                    "- Take clear screenshots of each element\n" +
                    "- Crop to just the unique part you want to detect\n" +
                    "- Save with exact names above\n" +
                    "- Images should be at least 50x50 pixels\n\n" +
                    "Enhanced Startup Sequence:\n" +
                    "1. Find and click Kingshot app icon (unified template matching)\n" +
                    "2. Wait for loading screen to appear and disappear\n" +
                    "3. Detect and close any ads that appear\n" +
                    "4. Verify game is in base or map view\n" +
                    "5. Complete startup phase\n\n" +
                    "Enhanced Features:\n" +
                    "- Unified template matching service eliminates code duplication\n" +
                    "- Centralized coordinates in GameCoordinates class\n" +
                    "- Consistent error handling patterns\n" +
                    "- Configurable confidence thresholds and timeouts\n" +
                    "- Improved resource management", cancellationToken);
                logger.LogInfo($"Created startup images README at: {readmePath}");
            }
        }

        /// <summary>
        /// Helper method to verify if the bot is in the castle view
        /// Checks for specific castle view indicators in the game
        /// </summary>
        private async Task<bool> IsInCastleViewAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var castleImageNames = new[] { "castle-view-1.png", "castle-view-2.png" };
            foreach (var imageName in castleImageNames)
            {
                if (await WaitForImageAsync(imageName, account.InstanceNumber, logger, cancellationToken, timeoutMs: 3000, threshold: 0.7))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Confirmed in castle view with {imageName}.");
                    return true;
                }
            }
            logger.LogWarning($"[{account.AccountName}] ⚠️ Could not confirm castle view after checking all images.");
            return false;
        }


    }
} 