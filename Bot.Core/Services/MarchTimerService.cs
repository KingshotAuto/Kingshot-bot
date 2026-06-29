using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;

namespace Bot.Core.Services
{
    /// <summary>
    /// Service for detecting and managing active march timers from AutoHunt and AutoIntel tasks
    /// </summary>
    public class MarchTimerService
    {
        private readonly LogService _logger;
        private readonly UnifiedTemplateMatchingService _templateMatcher;
        private readonly OCRService _ocrService;
        
        // Cache for timer results to avoid excessive OCR operations
        private static readonly ConcurrentDictionary<int, (DateTime lastCheck, TimeSpan? remainingTime)> _timerCache = new();
        private static readonly TimeSpan CacheTimeout = TimeSpan.FromSeconds(30);
        
        // Template matching areas for march timers
        private static readonly Rectangle MarchTimerArea = new Rectangle(10, 10, 700, 200); // Top area where march timers appear
        private static readonly Rectangle TimerTextArea = new Rectangle(50, 50, 200, 30);   // Area around timer text
        
        // Timer detection patterns
        private static readonly Regex TimePattern = new Regex(@"(\d{1,2}):(\d{2}):(\d{2})", RegexOptions.Compiled);
        private static readonly Regex ShortTimePattern = new Regex(@"(\d{1,2}):(\d{2})", RegexOptions.Compiled);
        
        public MarchTimerService(LogService logger)
        {
            _logger = logger;
            _templateMatcher = new UnifiedTemplateMatchingService(logger);
            _ocrService = new OCRService(logger);
        }

        /// <summary>
        /// Checks if there are any active march timers that would prevent farming
        /// </summary>
        public async Task<MarchTimerResult> CheckActiveMarchTimersAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken = default, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Check cache first to avoid excessive operations
                if (_timerCache.TryGetValue(instanceNumber, out var cached) && 
                    DateTime.UtcNow - cached.lastCheck < CacheTimeout)
                {
                    logger.LogInfo($"[Instance {instanceNumber}] Using cached march timer result");
                    return new MarchTimerResult(cached.remainingTime.HasValue, cached.remainingTime);
                }

                // Ensure we're in MapView to check for march timers
                var locator = new LocatorService(logger, new AccountSettings { InstanceNumber = instanceNumber });
                
                // Try to ensure MapView - if this fails, we'll proceed anyway but results may be unreliable
                try
                {
                    await locator.EnsureViewAsync(ViewType.MapView, instanceNumber, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Instance {instanceNumber}] Could not ensure MapView for march timer check: {ex.Message}");
                }

                // Take screenshot and check for march timer indicators
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger, cancellationToken);
                if (screenshot == null)
                {
                    logger.LogWarning($"[Instance {instanceNumber}] Failed to capture screenshot for march timer check");
                    return new MarchTimerResult(false, null);
                }

                // Check for visual timer indicators (march icons, progress bars, etc.)
                var hasVisualTimers = await DetectVisualMarchTimersAsync(screenshot, instanceNumber, logger);
                
                // Check for timer text using OCR
                var timerText = await DetectTimerTextAsync(screenshot, instanceNumber, logger, cancellationToken);
                var remainingTime = ParseTimerText(timerText);

                bool hasActiveTimers = hasVisualTimers || remainingTime.HasValue;
                
                // Cache the result
                _timerCache.AddOrUpdate(instanceNumber, 
                    (DateTime.UtcNow, remainingTime),
                    (key, oldValue) => (DateTime.UtcNow, remainingTime));

                logger.LogInfo($"[Instance {instanceNumber}] March timer check: Active={hasActiveTimers}, RemainingTime={remainingTime?.ToString(@"hh\:mm\:ss") ?? "None"}");
                
                // Provide detailed GUI feedback about timer status
                if (hasActiveTimers && userNotifications != null)
                {
                    if (remainingTime.HasValue)
                    {
                        var timeText = remainingTime.Value.TotalHours >= 1 
                            ? $"{remainingTime.Value:hh\\:mm\\:ss}" 
                            : $"{remainingTime.Value:mm\\:ss}";
                        userNotifications.ShowStatus($"Active march timers detected - {timeText} remaining", NotificationType.Info);
                    }
                    else
                    {
                        userNotifications.ShowStatus("Active march timers detected - exact time unknown", NotificationType.Info);
                    }
                }
                
                return new MarchTimerResult(hasActiveTimers, remainingTime);
            }
            catch (Exception ex)
            {
                logger.LogError($"[Instance {instanceNumber}] Error checking march timers: {ex.Message}");
                return new MarchTimerResult(false, null);
            }
        }

        /// <summary>
        /// Gets the estimated time when all marches will return based on task settings
        /// </summary>
        public TimeSpan? GetEstimatedMarchReturnTime(AccountSettings account)
        {
            TimeSpan? maxReturnTime = null;

            // Check AutoHunt march times
            if (account.TaskSettings.TryGetValue("AutoHunt", out var autoHuntJson))
            {
                try
                {
                    var autoHuntSettings = System.Text.Json.JsonSerializer.Deserialize<AutoHuntSettings>(autoHuntJson);
                    if (autoHuntSettings?.LastMarchSentTime.HasValue == true)
                    {
                        var huntReturnTime = TimeSpan.FromMinutes(3); // Default AutoHunt march time
                        var timeSinceMarch = DateTime.UtcNow - autoHuntSettings.LastMarchSentTime.Value;
                        var huntRemainingTime = huntReturnTime - timeSinceMarch;
                        
                        if (huntRemainingTime > TimeSpan.Zero)
                        {
                            maxReturnTime = huntRemainingTime;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error parsing AutoHunt settings: {ex.Message}");
                }
            }

            // Check AutoIntel march times (similar logic)
            if (account.TaskSettings.TryGetValue("AutoIntel", out var autoIntelJson))
            {
                try
                {
                    // Add AutoIntel timer checking logic here when available
                    // For now, assume similar 3-minute march time
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error parsing AutoIntel settings: {ex.Message}");
                }
            }

            return maxReturnTime;
        }

        private async Task<byte[]?> TakeScreenshotAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Use the existing BaseTaskWithCommonPatterns screenshot method
                var connection = await ADBMigrationHelper.GetConnectionAsync(instanceNumber, logger, cancellationToken);
                if (connection != null)
                {
                    return await ADBMigrationHelper.TakeScreenshotAsync(connection, logger, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error taking screenshot: {ex.Message}");
            }
            return null;
        }

        private Task<bool> DetectVisualMarchTimersAsync(byte[] screenshot, int instanceNumber, LogService logger)
        {
            try
            {
                var templateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "march_timers");
                
                // Check for common march timer visual indicators
                var timerIndicators = new[] 
                {
                    "march_progress.png",     // March progress bar
                    "march_timer_icon.png",  // Timer icon
                    "troops_marching.png",   // Troops marching indicator
                    "return_timer.png"       // Return timer indicator
                };

                foreach (var indicator in timerIndicators)
                {
                    var templatePath = Path.Combine(templateFolder, indicator);
                    if (File.Exists(templatePath))
                    {
                        var result = _templateMatcher.MatchTemplate(
                            screenshot,
                            templatePath,
                            instanceNumber,
                            threshold: 0.7,
                            searchArea: MarchTimerArea
                        );
                        
                        if (result.found)
                        {
                            logger.LogInfo($"[Instance {instanceNumber}] Found march timer indicator: {indicator} (confidence: {result.confidence:F3})");
                            return Task.FromResult(true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error detecting visual march timers: {ex.Message}");
            }
            
            return Task.FromResult(false);
        }

        private Task<string> DetectTimerTextAsync(byte[] screenshot, int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                // Use OCR to detect timer text in the timer area
                var timerText = _ocrService.ExtractTextFromScreenArea(screenshot, TimerTextArea);
                logger.LogInfo($"[Instance {instanceNumber}] OCR detected timer text: '{timerText}'");
                return Task.FromResult(timerText ?? string.Empty);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error detecting timer text: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        private TimeSpan? ParseTimerText(string timerText)
        {
            if (string.IsNullOrWhiteSpace(timerText))
                return null;

            // Try to match HH:MM:SS format
            var match = TimePattern.Match(timerText);
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out var hours) &&
                    int.TryParse(match.Groups[2].Value, out var minutes) &&
                    int.TryParse(match.Groups[3].Value, out var seconds))
                {
                    return new TimeSpan(hours, minutes, seconds);
                }
            }

            // Try to match MM:SS format
            var shortMatch = ShortTimePattern.Match(timerText);
            if (shortMatch.Success)
            {
                if (int.TryParse(shortMatch.Groups[1].Value, out var minutes) &&
                    int.TryParse(shortMatch.Groups[2].Value, out var seconds))
                {
                    return new TimeSpan(0, minutes, seconds);
                }
            }

            return null;
        }

        /// <summary>
        /// Clears the timer cache for a specific instance
        /// </summary>
        public void ClearTimerCache(int instanceNumber)
        {
            _timerCache.TryRemove(instanceNumber, out _);
        }

        /// <summary>
        /// Clears all timer caches
        /// </summary>
        public void ClearAllTimerCaches()
        {
            _timerCache.Clear();
        }
    }

    /// <summary>
    /// Result of march timer detection
    /// </summary>
    public class MarchTimerResult
    {
        public bool HasActiveTimers { get; }
        public TimeSpan? RemainingTime { get; }

        public MarchTimerResult(bool hasActiveTimers, TimeSpan? remainingTime)
        {
            HasActiveTimers = hasActiveTimers;
            RemainingTime = remainingTime;
        }

        public string GetStatusMessage()
        {
            if (!HasActiveTimers)
                return "No active march timers detected";

            if (RemainingTime.HasValue)
                return $"March timers active - {RemainingTime.Value:hh\\:mm\\:ss} remaining";

            return "March timers detected but time unknown";
        }
    }
}