using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using Bot.Core.ImageDetection;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules.AutoHunt.Processors
{
    /// <summary>
    /// Base class for target processors that provides access to common patterns functionality
    /// </summary>
    public abstract class BaseTargetProcessor : ITargetProcessor
    {
        protected readonly UnifiedTemplateMatchingService _templateMatcher;
        protected readonly string _imageTemplateFolder;
        
        // Static state storage for session management
        protected static readonly ConcurrentDictionary<string, bool> SkipMapViewChecks = new();
        protected static readonly ConcurrentDictionary<string, HashSet<Rectangle>> PredictionFailedAreas = new();

        public abstract string TargetType { get; }
        public abstract bool RequiresMarch { get; }
        public abstract int GetPriority();

        protected BaseTargetProcessor(UnifiedTemplateMatchingService templateMatcher)
        {
            _templateMatcher = templateMatcher;
            _imageTemplateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images");
        }

        public abstract Task<TargetProcessResult> ProcessAsync(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId);

        public virtual bool CanProcess(HuntTarget target, AutoHuntSessionState sessionState)
        {
            return target.Type == TargetType;
        }

        // These methods will be provided by dependency injection in the actual implementation
        // For now, they're abstract so derived classes must handle the dependency injection
        protected abstract Task<byte[]?> TakeScreenshotAsync(int instanceNumber, LogService logger);
        protected abstract Task<bool> ClickAsync(int instanceNumber, LogService logger, Point point);
        protected abstract Task<bool> ClickRandomInRectAsync(int instanceNumber, LogService logger, Rectangle rect);
        protected abstract Task<bool> FindAndClickImageAsync(string imageName, int instanceNumber, LogService logger, double threshold = 0.6, Rectangle? searchArea = null);
        protected abstract Task<bool> WaitForImageAsync(string imageName, int instanceNumber, LogService logger, CancellationToken cancellationToken, int timeoutMs = 5000, double threshold = 0.6, Rectangle? searchArea = null);

        // Utility methods
        protected AutoHuntSettings GetAutoHuntSettings(AccountSettings account)
        {
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

        protected void SaveAutoHuntSettings(AccountSettings account, AutoHuntSettings settings)
        {
            var configManager = ConfigurationManager.Instance;
            var settingsJson = JsonSerializer.Serialize(settings);
            account.TaskSettings["AutoHunt"] = settingsJson;
            // Configuration is auto-saved via ConfigurationManager.Instance
        }

        protected async Task<string> ReadPredictionText(AccountSettings account, LogService logger)
        {
            // This would need OCR service - for now return empty
            return string.Empty;
        }

        protected async Task<StaminaCheckResult> CheckForStaminaLow(
            byte[]? screenshot,
            Rectangle staminaLowArea,
            AccountSettings account,
            LogService logger,
            bool autoClick = false,
            CancellationToken cancellationToken = default)
        {
            // This would need stamina management service
            return StaminaCheckResult.NoStaminaIssue;
        }

        protected async Task<bool> CheckMaxMarch(AccountSettings account, LogService logger, AutoHuntSettings settings)
        {
            // This would need march management service
            return false;
        }

        protected async Task<bool> IsTargetMarchingAsync(AccountSettings account, LogService logger)
        {
            // This would need hunt mode navigation service
            return false;
        }

        protected async Task HandleMarchingTarget(HuntTarget target, AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // This would need hunt mode navigation service
            await Task.Delay(1000, cancellationToken);
        }
    }
}