using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Bot.Core.Services
{
    /// <summary>
    /// Service to detect the unique account ID of the currently active game account.
    /// This is used to create unique timer keys when ChangeAccount task switches between accounts.
    /// </summary>
    public class AccountDetectionService
    {
        private readonly LogService _logger;
        
        // UI coordinates
        private static readonly Point AccountInfoButton = new Point(54, 49);
        private static readonly Point BackButton = new Point(42, 38);
        private static readonly Rectangle AccountIdArea = new Rectangle(310, 942, 149, 36); // 310,942 to 459,978
        
        public AccountDetectionService(LogService logger)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// Detects the unique account ID for the currently active game account.
        /// Returns a string that can be used as a unique identifier for timers.
        /// </summary>
        public async Task<string> GetCurrentAccountIdAsync(AccountSettings account, CancellationToken cancellationToken = default)
        {
            try
            {
                // Check if we already have a cached account ID for this instance - skip all UI operations if we do
                var cachedId = Tasks.Modules.AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                if (!string.IsNullOrEmpty(cachedId))
                {
                    _logger.LogInfo($"[{account.AccountName}] ✅ Using cached account ID: '{cachedId}' - skipping UI operations");
                    return cachedId;
                }
                
                _logger.LogInfo($"[{account.AccountName}] Starting account ID detection...");
                
                // Step 1: Ensure bot is in world map or base view using LocatorService
                var locator = new LocatorService(_logger, account);
                bool inValidView = false;
                
                // First detect current view
                _logger.LogInfo($"[{account.AccountName}] Detecting current view for account detection...");
                ViewType currentView = await locator.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);
                
                if (currentView == ViewType.BaseView)
                {
                    _logger.LogInfo($"[{account.AccountName}] Already in base view - perfect for account detection");
                    inValidView = true;
                }
                else if (currentView == ViewType.MapView)
                {
                    _logger.LogInfo($"[{account.AccountName}] Already in map view - perfect for account detection");
                    inValidView = true;
                }
                else
                {
                    _logger.LogInfo($"[{account.AccountName}] Not in ideal view (current: {currentView}), navigating to base view...");
                    try
                    {
                        await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        _logger.LogInfo($"[{account.AccountName}] ✅ Successfully navigated to base view for account detection");
                        inValidView = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to navigate to base view, trying map view: {ex.Message}");
                        try
                        {
                            await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
                            _logger.LogInfo($"[{account.AccountName}] ✅ Successfully navigated to map view for account detection");
                            inValidView = true;
                        }
                        catch (Exception mapEx)
                        {
                            _logger.LogError($"[{account.AccountName}] ❌ Failed to navigate to any valid view for account detection: {mapEx.Message}");
                            return GetFallbackAccountId(account);
                        }
                    }
                }
                
                if (!inValidView)
                {
                    _logger.LogError($"[{account.AccountName}] ❌ Could not confirm valid view for account detection");
                    return GetFallbackAccountId(account);
                }
                
                // Step 2: Click account info button (54, 49)
                _logger.LogInfo($"[{account.AccountName}] Clicking account info button at ({AccountInfoButton.X}, {AccountInfoButton.Y})...");
                var adbController = await ADBConnectionManager.GetConnectionAsync(account.InstanceNumber, _logger, cancellationToken);
                if (adbController == null)
                {
                    _logger.LogError($"[{account.AccountName}] Failed to get ADB connection for account detection");
                    return GetFallbackAccountId(account);
                }
                
                await adbController.TapAsync(AccountInfoButton.X, AccountInfoButton.Y, cancellationToken);
                
                // Step 3: Wait 1 second for account info to load
                await Task.Delay(1000, cancellationToken);
                
                // Step 4: Use OCR to read account ID from specified area (with retries)
                _logger.LogInfo($"[{account.AccountName}] Reading account ID from area {AccountIdArea}...");
                string accountId = await ReadAccountIdWithRetriesAsync(account.InstanceNumber, cancellationToken);
                
                // Step 5: Click back button (42, 38)
                _logger.LogInfo($"[{account.AccountName}] Clicking back button at ({BackButton.X}, {BackButton.Y})...");
                await adbController.TapAsync(BackButton.X, BackButton.Y, cancellationToken);
                
                // Wait a moment for UI to return to previous state
                await Task.Delay(500, cancellationToken);
                
                if (!string.IsNullOrEmpty(accountId))
                {
                    _logger.LogInfo($"[{account.AccountName}] ✅ Successfully detected account ID: '{accountId}'");
                    return accountId;
                }
                else
                {
                    _logger.LogWarning($"[{account.AccountName}] ⚠️ OCR failed to detect account ID, using fallback");
                    return GetFallbackAccountId(account);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{account.AccountName}] Error during account detection: {ex.Message}");
                return GetFallbackAccountId(account);
            }
        }
        
        /// <summary>
        /// Uses OCR to read the account ID with retry logic
        /// </summary>
        private async Task<string> ReadAccountIdWithRetriesAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            const int delayBetweenRetries = 1000; // 1 second
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                _logger.LogInfo($"OCR attempt {attempt}/{maxRetries} for account ID detection");
                
                string result = await ReadAccountIdWithOcrAsync(instanceNumber, cancellationToken);
                if (!string.IsNullOrEmpty(result))
                {
                    _logger.LogInfo($"✅ Account ID detected successfully on attempt {attempt}: '{result}'");
                    return result;
                }
                
                if (attempt < maxRetries)
                {
                    _logger.LogWarning($"⚠️ OCR attempt {attempt} failed, waiting {delayBetweenRetries}ms before retry...");
                    await Task.Delay(delayBetweenRetries, cancellationToken);
                }
            }
            
            _logger.LogError($"❌ All {maxRetries} OCR attempts failed for account ID detection");
            return string.Empty;
        }

        /// <summary>
        /// Uses OCR to read the account ID from the specified screen area
        /// </summary>
        private async Task<string> ReadAccountIdWithOcrAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Take screenshot
                var adbController = await ADBConnectionManager.GetConnectionAsync(instanceNumber, _logger, cancellationToken);
                if (adbController == null) return string.Empty;
                
                var screenshot = await adbController.TakeScreenshotAsync(cancellationToken);
                if (screenshot == null || screenshot.Length == 0)
                {
                    _logger.LogWarning("Failed to take screenshot for account ID detection");
                    return string.Empty;
                }
                
                // Configure OCR for account ID reading
                var ocrConfig = new OCRConfiguration
                {
                    ScaleFactor = 3,
                    AdaptiveC = 7,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_" // Allow alphanumeric and common separators
                };
                
                // Add timeout protection to OCR operation
                using var ocrTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ocrTimeoutCts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout for OCR
                
                using var ocr = new OCRService(_logger, ocrConfig);
                string rawText;
                
                try
                {
                    var ocrTask = Task.Run(() => ocr.ExtractTextFromScreenArea(screenshot, AccountIdArea), ocrTimeoutCts.Token);
                    rawText = await ocrTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ocrTimeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"OCR operation timed out after 10 seconds for account ID detection");
                    return string.Empty;
                }
                
                _logger.LogInfo($"Raw OCR text for account ID: '{rawText}'");
                
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    return string.Empty;
                }
                
                // Clean up the text - remove whitespace and extract meaningful part
                string cleanedText = rawText.Trim();
                
                // Try to extract account ID pattern - look for sequences of alphanumeric characters
                var match = Regex.Match(cleanedText, @"[A-Za-z0-9_-]+");
                if (match.Success && match.Value.Length >= 3) // Minimum 3 characters for valid ID
                {
                    string accountId = match.Value;
                    _logger.LogInfo($"Extracted account ID: '{accountId}' from raw text: '{rawText}'");
                    return accountId;
                }
                
                // If no clear pattern found but we have text, use the cleaned version
                if (cleanedText.Length >= 3)
                {
                    _logger.LogInfo($"Using cleaned text as account ID: '{cleanedText}' from raw text: '{rawText}'");
                    return cleanedText;
                }
                
                _logger.LogWarning($"Could not extract valid account ID from text: '{rawText}'");
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during OCR account ID reading: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Generates a fallback account ID when detection fails
        /// </summary>
        private string GetFallbackAccountId(AccountSettings account)
        {
            // Use instance number and account name as fallback
            string fallbackId = $"inst{account.InstanceNumber}_{account.AccountName}_fallback";
            _logger.LogWarning($"[{account.AccountName}] ⚠️ Account detection failed - using fallback ID: '{fallbackId}'");
            _logger.LogInfo($"[{account.AccountName}] Fallback ID will be used for timer tracking (may be less accurate across account switches)");
            return fallbackId;
        }
        
        /// <summary>
        /// Creates a unique timer key using the detected account ID
        /// </summary>
        public static string CreateTimerKey(int instanceNumber, string accountId)
        {
            return $"{instanceNumber}_{accountId}";
        }
    }
}