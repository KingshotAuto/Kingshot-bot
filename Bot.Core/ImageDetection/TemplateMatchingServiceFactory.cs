using System;
using Bot.Core.Logging;

namespace Bot.Core.ImageDetection
{
    /// <summary>
    /// Factory class for creating and managing template matching services.
    /// Provides centralized service creation and lifecycle management.
    /// </summary>
    public static class TemplateMatchingServiceFactory
    {
        private static readonly object _lock = new object();
        private static EnhancedGrayscaleTemplateMatchingService? _enhancedGrayscaleInstance;
        private static ColorTemplateMatchingService? _colorInstance;
        private static GrayscaleTemplateMatchingService? _legacyGrayscaleInstance;

        /// <summary>
        /// Gets or creates a singleton instance of the enhanced grayscale template matching service.
        /// This is the recommended service for most template matching operations.
        /// </summary>
        /// <param name="logger">Logger service</param>
        /// <returns>Enhanced grayscale template matching service</returns>
        public static EnhancedGrayscaleTemplateMatchingService GetEnhancedGrayscaleService(LogService logger)
        {
            if (_enhancedGrayscaleInstance == null)
            {
                lock (_lock)
                {
                    if (_enhancedGrayscaleInstance == null)
                    {
                        _enhancedGrayscaleInstance = new EnhancedGrayscaleTemplateMatchingService(logger);
                        
                        // Register cleanup on application exit
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeServices();
                        AppDomain.CurrentDomain.DomainUnload += (_, _) => DisposeServices();
                    }
                }
            }
            return _enhancedGrayscaleInstance;
        }

        /// <summary>
        /// Gets or creates a singleton instance of the color template matching service.
        /// Use this for UI elements that require color-based matching.
        /// </summary>
        /// <param name="logger">Logger service</param>
        /// <returns>Color template matching service</returns>
        public static ColorTemplateMatchingService GetColorService(LogService logger)
        {
            if (_colorInstance == null)
            {
                lock (_lock)
                {
                    if (_colorInstance == null)
                    {
                        _colorInstance = new ColorTemplateMatchingService(logger);
                        
                        // Register cleanup on application exit
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeServices();
                        AppDomain.CurrentDomain.DomainUnload += (_, _) => DisposeServices();
                    }
                }
            }
            return _colorInstance;
        }

        /// <summary>
        /// Gets or creates a singleton instance of the legacy grayscale template matching service.
        /// Use this only for backward compatibility if needed.
        /// </summary>
        /// <param name="logger">Logger service</param>
        /// <returns>Legacy grayscale template matching service</returns>
        public static GrayscaleTemplateMatchingService GetLegacyGrayscaleService(LogService logger)
        {
            if (_legacyGrayscaleInstance == null)
            {
                lock (_lock)
                {
                    if (_legacyGrayscaleInstance == null)
                    {
                        _legacyGrayscaleInstance = new GrayscaleTemplateMatchingService(logger);
                        
                        // Register cleanup on application exit
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeServices();
                        AppDomain.CurrentDomain.DomainUnload += (_, _) => DisposeServices();
                    }
                }
            }
            return _legacyGrayscaleInstance;
        }

        /// <summary>
        /// Disposes all service instances and releases resources.
        /// Called automatically on application shutdown.
        /// </summary>
        public static void DisposeServices()
        {
            lock (_lock)
            {
                try
                {
                    _enhancedGrayscaleInstance?.Dispose();
                    _enhancedGrayscaleInstance = null;
                }
                catch (Exception ex)
                {
                    // Log error but continue cleanup
                    Console.WriteLine($"Error disposing enhanced grayscale service: {ex.Message}");
                }

                try
                {
                    _colorInstance?.Dispose();
                    _colorInstance = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disposing color service: {ex.Message}");
                }

                try
                {
                    _legacyGrayscaleInstance?.Dispose();
                    _legacyGrayscaleInstance = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disposing legacy grayscale service: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets cache statistics from all active services.
        /// </summary>
        /// <returns>Cache statistics string</returns>
        public static string GetAllCacheStats()
        {
            var stats = "=== Template Matching Service Cache Statistics ===\n";

            if (_enhancedGrayscaleInstance != null)
            {
                stats += $"Enhanced Grayscale: {_enhancedGrayscaleInstance.GetCacheStats()}\n";
                stats += $"Cache Initialized: {_enhancedGrayscaleInstance.IsCacheInitialized}\n";
            }

            if (_colorInstance != null)
            {
                // ColorTemplateMatchingService doesn't have GetCacheStats, so we'll indicate it's active
                stats += "Color Service: Active\n";
            }

            if (_legacyGrayscaleInstance != null)
            {
                stats += "Legacy Grayscale: Active\n";
            }

            return stats;
        }

        /// <summary>
        /// Clears all caches across all services.
        /// Use carefully as this will impact performance until caches are rebuilt.
        /// </summary>
        public static void ClearAllCaches()
        {
            lock (_lock)
            {
                _enhancedGrayscaleInstance?.ClearCache();
                // Note: ColorTemplateMatchingService and GrayscaleTemplateMatchingService 
                // don't have public ClearCache methods, but their cleanup timers will handle it
            }
        }

        /// <summary>
        /// Preloads common templates across all services.
        /// Call this during application startup for better initial performance.
        /// </summary>
        /// <param name="logger">Logger service</param>
        public static async System.Threading.Tasks.Task PreloadCommonTemplatesAsync(LogService logger)
        {
            var enhancedService = GetEnhancedGrayscaleService(logger);
            
            // Preload the most commonly used templates
            var commonTemplates = new[]
            {
                GameTemplates.BUTTONS_BACK,
                GameTemplates.BUTTONS_CLOSE,
                GameTemplates.BUTTONS_CONFIRM,
                GameTemplates.BUTTONS_OK,
                GameTemplates.RECOVERY_HOME_BUTTON,
                GameTemplates.LOCATOR_MAP_BUTTON,
                GameTemplates.STARTUP_CONFIRM_WELCOME,
                GameTemplates.STARTUP_KINGSHOT_APP
            };

            var preloadTasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
            
            foreach (var template in commonTemplates)
            {
                if (template.Exists())
                {
                    preloadTasks.Add(enhancedService.PreloadTemplateAsync(template));
                }
            }

            if (preloadTasks.Count > 0)
            {
                logger.LogInfo($"Preloading {preloadTasks.Count} common templates...");
                await System.Threading.Tasks.Task.WhenAll(preloadTasks);
                logger.LogInfo("Template preloading completed");
            }
        }

        /// <summary>
        /// Validates that all required template files exist and logs missing ones.
        /// </summary>
        /// <param name="logger">Logger service</param>
        /// <returns>Number of missing templates</returns>
        public static int ValidateTemplateFiles(LogService logger)
        {
            var allTemplates = Enum.GetValues<GameTemplates>();
            var missingCount = 0;
            var totalCount = allTemplates.Length;

            logger.LogInfo($"Validating {totalCount} template files...");

            foreach (var template in allTemplates)
            {
                if (!template.Exists())
                {
                    logger.LogWarning($"Missing template file: {template} at {template.GetTemplatePath()}");
                    missingCount++;
                }
            }

            if (missingCount == 0)
            {
                logger.LogInfo("All template files validated successfully");
            }
            else
            {
                logger.LogWarning($"Validation complete: {missingCount} missing templates out of {totalCount}");
            }

            return missingCount;
        }
    }
}