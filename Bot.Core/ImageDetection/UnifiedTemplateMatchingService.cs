using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using Bot.Core.Logging;
using Rectangle = System.Drawing.Rectangle;

namespace Bot.Core.ImageDetection
{
    public class UnifiedTemplateMatchingService : IDisposable
    {
        private readonly LogService _logger;
        private readonly GrayscaleTemplateMatchingService _grayscaleService;
        private readonly ColorTemplateMatchingService _colorService;
        private bool _disposed;

        public static readonly double[] StandardScales = { 0.8, 0.9, 1.0, 1.1, 1.2 };

        // List of templates that should use color matching
        private static readonly HashSet<string> ColorMatchingTemplates = new HashSet<string> 
        { 
            "stamina.png",
            "compass.png",
            "vip-rewards.png",
            "vip-rewards2.png",
            "victory-banner.png",
            "defeat-banner.png"
        };

        public UnifiedTemplateMatchingService(LogService logger)
        {
            _logger = logger;
            _grayscaleService = new GrayscaleTemplateMatchingService(logger);
            _colorService = new ColorTemplateMatchingService(logger);
        }

        private bool ShouldUseColorMatching(string templatePath)
        {
            string fileName = Path.GetFileName(templatePath);
            return ColorMatchingTemplates.Contains(fileName);
        }

        public (bool found, Rectangle matchRect, double confidence) MatchTemplate(
            byte[] screenshot,
            string templatePath,
            int instanceNumber,
            double threshold = 0.6,
            double[]? scales = null,
            bool verboseLogging = false,
            Rectangle? searchArea = null,
            bool isUIElement = false)
        {
            bool useColorMatching = ShouldUseColorMatching(templatePath) || isUIElement;
            
            if (useColorMatching)
            {
                // Use lower threshold for UI elements and color templates
                double colorThreshold = threshold < 0.6 ? threshold : 0.55;
                return _colorService.MatchTemplate(screenshot, templatePath, colorThreshold, scales, verboseLogging, searchArea);
            }
            else
            {
                return _grayscaleService.MatchTemplate(screenshot, templatePath, threshold, scales, verboseLogging, searchArea);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _grayscaleService?.Dispose();
                _colorService?.Dispose();
                _disposed = true;
            }
        }
    }
}