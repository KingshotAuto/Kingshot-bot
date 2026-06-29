using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using Bot.Core.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Rectangle = System.Drawing.Rectangle;
using System.Collections.Generic;

namespace Bot.Core.ImageDetection
{
    public class ColorTemplateMatchingService : IDisposable
    {
        private readonly LogService _logger;
        private readonly ConcurrentDictionary<string, (Mat BGR, Mat HSV, DateTime LastUsed, int RefCount)> _templateCache;
        private bool _disposed;
        private const int MAX_CACHE_SIZE = 50;
        private const int CACHE_CLEANUP_INTERVAL_MS = 60000;
        private readonly System.Threading.Timer _cleanupTimer;
        private readonly object _cacheLock = new object();
        
        public static readonly double[] StandardScales = { 0.8, 0.9, 1.0, 1.1, 1.2 };
        private const double HSV_WEIGHT = 0.7;
        private const double BGR_WEIGHT = 0.3;

        public ColorTemplateMatchingService(LogService logger)
        {
            _logger = logger;
            _templateCache = new ConcurrentDictionary<string, (Mat BGR, Mat HSV, DateTime LastUsed, int RefCount)>();
            _cleanupTimer = new System.Threading.Timer(CleanupCaches, null, CACHE_CLEANUP_INTERVAL_MS, CACHE_CLEANUP_INTERVAL_MS);
        }

        private void CleanupCaches(object? state)
        {
            try
            {
                lock (_cacheLock)
                {
                    while (_templateCache.Count > MAX_CACHE_SIZE)
                    {
                        var oldestKey = _templateCache
                            .Where(kvp => kvp.Value.RefCount == 0)
                            .OrderBy(kvp => kvp.Value.LastUsed)
                            .Select(kvp => kvp.Key)
                            .FirstOrDefault();
                        
                        if (!string.IsNullOrEmpty(oldestKey) && _templateCache.TryRemove(oldestKey, out var oldValue))
                        {
                            oldValue.BGR?.Dispose();
                            oldValue.HSV?.Dispose();
                        }
                        else
                        {
                            break;
                        }
                    }

                    var cutoff = DateTime.UtcNow.AddHours(-1);
                    
                    foreach (var kvp in _templateCache.Where(kvp => kvp.Value.LastUsed < cutoff && kvp.Value.RefCount == 0))
                    {
                        if (_templateCache.TryRemove(kvp.Key, out var oldValue))
                        {
                            oldValue.BGR?.Dispose();
                            oldValue.HSV?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during color template cache cleanup: {ex.Message}");
            }
        }

        private (Mat BGR, Mat HSV) GetOrLoadTemplate(string templatePath)
        {
            var now = DateTime.UtcNow;
            
            lock (_cacheLock)
            {
                var value = _templateCache.AddOrUpdate(
                    templatePath,
                    _ =>
                    {
                        var templateBGR = Cv2.ImRead(templatePath, ImreadModes.Color);
                        if (templateBGR.Empty())
                        {
                            throw new InvalidOperationException($"Could not load template file: {templatePath}");
                        }

                        var templateHSV = new Mat();
                        Cv2.CvtColor(templateBGR, templateHSV, ColorConversionCodes.BGR2HSV);
                        return (templateBGR, templateHSV, now, 1);
                    },
                    (_, old) =>
                    {
                        return (old.BGR, old.HSV, now, old.RefCount + 1);
                    });
                
                return (value.BGR, value.HSV);
            }
        }

        public (bool found, Rectangle matchRect, double confidence) MatchTemplate(
            byte[] screenshot,
            string templatePath,
            double threshold = 0.55,
            double[]? scales = null,
            bool verboseLogging = false,
            Rectangle? searchArea = null)
        {
            scales ??= StandardScales;

            if (!File.Exists(templatePath))
            {
                _logger.LogError($"Template not found: {templatePath}");
                return (false, Rectangle.Empty, 0);
            }

            try
            {
                using var ms = new MemoryStream(screenshot);
                using var bmp = new Bitmap(ms);
                using var sourceMat = BitmapConverter.ToMat(bmp);

                Mat screenshotBGR;
                if (sourceMat.Channels() == 4)
                {
                    screenshotBGR = new Mat();
                    Cv2.CvtColor(sourceMat, screenshotBGR, ColorConversionCodes.RGBA2BGR);
                }
                else
                {
                    screenshotBGR = sourceMat.Clone();
                }

                try
                {
                    if (searchArea.HasValue)
                    {
                        var rect = searchArea.Value;
                        var ocvRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                        if (ocvRect.Right > screenshotBGR.Width || ocvRect.Bottom > screenshotBGR.Height || ocvRect.X < 0 || ocvRect.Y < 0)
                        {
                            _logger.LogWarning($"Invalid search area: {rect}. Screenshot size: {screenshotBGR.Width}x{screenshotBGR.Height}. Using full image.");
                            return ExecuteColorMatching(screenshotBGR, templatePath, threshold, scales, verboseLogging);
                        }
                        using var croppedSource = new Mat(screenshotBGR, ocvRect);
                        var result = ExecuteColorMatching(croppedSource, templatePath, threshold, scales, verboseLogging);
                        if (result.found)
                        {
                            return (true,
                                new Rectangle(result.matchRect.X + rect.X, result.matchRect.Y + rect.Y,
                                    result.matchRect.Width, result.matchRect.Height),
                                result.confidence);
                        }
                        return (false, Rectangle.Empty, result.confidence);
                    }

                    return ExecuteColorMatching(screenshotBGR, templatePath, threshold, scales, verboseLogging);
                }
                finally
                {
                    screenshotBGR?.Dispose();
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogError($"Screenshot data for {Path.GetFileName(templatePath)} is corrupted and cannot be processed. Error: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Color template matching failed for {templatePath}: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
        }

        private (bool found, Rectangle matchRect, double confidence) ExecuteColorMatching(
            Mat screenshotBGR, string templatePath, double threshold, double[] scales, bool verboseLogging)
        {
            if (screenshotBGR.Empty())
            {
                _logger.LogError("Screenshot image is empty");
                return (false, Rectangle.Empty, 0);
            }

            var (templateBGR, templateHSV) = GetOrLoadTemplate(templatePath);

            try
            {
                return FindBestColorMatch(screenshotBGR, templateBGR, templateHSV, threshold, scales, verboseLogging);
            }
            finally
            {
                lock (_cacheLock)
                {
                    if (_templateCache.TryGetValue(templatePath, out var cached))
                    {
                        _templateCache[templatePath] = (cached.BGR, cached.HSV, cached.LastUsed, Math.Max(0, cached.RefCount - 1));
                    }
                }
            }
        }

        private (bool found, Rectangle matchRect, double confidence) FindBestColorMatch(
            Mat screenshotBGR, Mat templateBGR, Mat templateHSV, double threshold, double[] scales, bool verboseLogging)
        {
            double bestConfidence = -1;
            Rectangle bestRect = Rectangle.Empty;

            using var screenshotHSV = new Mat();
            Cv2.CvtColor(screenshotBGR, screenshotHSV, ColorConversionCodes.BGR2HSV);

            Mat[] screenshotBGRChannels = null;
            Mat[] templateBGRChannels = null;
            Mat[] screenshotHSVChannels = null;
            Mat[] templateHSVChannels = null;

            try
            {
                screenshotBGRChannels = screenshotBGR.Split();
                templateBGRChannels = templateBGR.Split();
                screenshotHSVChannels = screenshotHSV.Split();
                templateHSVChannels = templateHSV.Split();

                foreach (var scale in StandardScales)
                {
                    int newWidth = (int)(templateBGR.Width * scale);
                    int newHeight = (int)(templateBGR.Height * scale);

                    if (newWidth < 1 || newHeight < 1 || newWidth > screenshotBGR.Width || newHeight > screenshotBGR.Height)
                    {
                        continue;
                    }

                    try
                    {
                        // Match in BGR space (per channel)
                        double bgrConfidence = 0;
                        Point bgrMaxLoc = new Point();
                        
                        for (int i = 0; i < 3; i++)
                        {
                            using var scaledTemplateBGR = new Mat();
                            Cv2.Resize(templateBGRChannels[i], scaledTemplateBGR, new Size(newWidth, newHeight));
                            
                            using var resultBGR = new Mat();
                            Cv2.MatchTemplate(screenshotBGRChannels[i], scaledTemplateBGR, resultBGR, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(resultBGR, out _, out double maxVal, out _, out Point maxLoc);
                            
                            if (i == 0) // Use Blue channel for location
                            {
                                bgrMaxLoc = maxLoc;
                            }
                            bgrConfidence += maxVal;
                        }
                        bgrConfidence /= 3.0; // Average BGR confidence

                        // Match in HSV space with saturation filtering
                        double hsvConfidence = 0;
                        Point hsvMaxLoc = new Point();
                        
                        double hueConfidence = 0;
                        double saturationConfidence = 0;
                        
                        // Check if template has sufficient saturation (not grey)
                        using (var templateSaturations = new Mat())
                        {
                            Cv2.Resize(templateHSVChannels[1], templateSaturations, new Size(newWidth, newHeight));
                            var templateSatMean = Cv2.Mean(templateSaturations);
                            double templateAvgSaturation = templateSatMean.Val0 / 255.0;
                            
                            if (templateAvgSaturation < 0.35) // Template is too grey
                            {
                                hsvConfidence = 0;
                            }
                            else
                            {
                                // Hue channel matching
                                using (var scaledTemplateH = new Mat())
                                {
                                    Cv2.Resize(templateHSVChannels[0], scaledTemplateH, new Size(newWidth, newHeight));
                                    using var resultH = new Mat();
                                    Cv2.MatchTemplate(screenshotHSVChannels[0], scaledTemplateH, resultH, TemplateMatchModes.CCoeffNormed);
                                    Cv2.MinMaxLoc(resultH, out _, out hueConfidence, out _, out hsvMaxLoc);
                                }
                                
                                // Saturation channel matching
                                using (var scaledTemplateS = new Mat())
                                {
                                    Cv2.Resize(templateHSVChannels[1], scaledTemplateS, new Size(newWidth, newHeight));
                                    using var resultS = new Mat();
                                    Cv2.MatchTemplate(screenshotHSVChannels[1], scaledTemplateS, resultS, TemplateMatchModes.CCoeffNormed);
                                    Cv2.MinMaxLoc(resultS, out _, out saturationConfidence, out _, out _);
                                }
                                
                                // Check saturation of detected area in screenshot
                                var detectedRect = new Rect(hsvMaxLoc.X, hsvMaxLoc.Y, newWidth, newHeight);
                                if (detectedRect.X + detectedRect.Width <= screenshotHSVChannels[1].Width && 
                                    detectedRect.Y + detectedRect.Height <= screenshotHSVChannels[1].Height)
                                {
                                    using var detectedSaturationArea = screenshotHSVChannels[1][detectedRect];
                                    var detectedSatMean = Cv2.Mean(detectedSaturationArea);
                                    double detectedAvgSaturation = detectedSatMean.Val0 / 255.0;
                                    
                                    if (detectedAvgSaturation < 0.3)
                                    {
                                        hsvConfidence = 0;
                                    }
                                    else
                                    {
                                        double preliminaryHsvConfidence = (0.9 * hueConfidence) + (0.1 * saturationConfidence);
                                        
                                        // Validate confidence consistency
                                        double confidenceGap = Math.Abs(bgrConfidence - preliminaryHsvConfidence);
                                        if (confidenceGap > 0.3 && bgrConfidence > preliminaryHsvConfidence)
                                        {
                                            hsvConfidence = preliminaryHsvConfidence * 0.5;
                                        }
                                        else
                                        {
                                            hsvConfidence = preliminaryHsvConfidence;
                                        }
                                    }
                                }
                                else
                                {
                                    hsvConfidence = (0.9 * hueConfidence) + (0.1 * saturationConfidence);
                                }
                            }
                        }

                        // Weighted combination of confidences
                        double combinedConfidence = (HSV_WEIGHT * hsvConfidence) + (BGR_WEIGHT * bgrConfidence);
                        Point finalMaxLoc = (hsvConfidence > bgrConfidence) ? hsvMaxLoc : bgrMaxLoc;

                        if (combinedConfidence > bestConfidence)
                        {
                            bestConfidence = combinedConfidence;
                            bestRect = new Rectangle(finalMaxLoc.X, finalMaxLoc.Y, newWidth, newHeight);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error in color matching scale {scale:F2}: {ex.Message}");
                    }
                }

                return (bestConfidence >= threshold, bestRect, bestConfidence);
            }
            finally
            {
                // Dispose of all channel Mats
                if (screenshotBGRChannels != null)
                {
                    foreach (var mat in screenshotBGRChannels)
                    {
                        mat?.Dispose();
                    }
                }
                if (templateBGRChannels != null)
                {
                    foreach (var mat in templateBGRChannels)
                    {
                        mat?.Dispose();
                    }
                }
                if (screenshotHSVChannels != null)
                {
                    foreach (var mat in screenshotHSVChannels)
                    {
                        mat?.Dispose();
                    }
                }
                if (templateHSVChannels != null)
                {
                    foreach (var mat in templateHSVChannels)
                    {
                        mat?.Dispose();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer.Dispose();
                
                foreach (var (BGR, HSV, _, _) in _templateCache.Values)
                {
                    BGR?.Dispose();
                    HSV?.Dispose();
                }
                _templateCache.Clear();
                _disposed = true;
            }
        }
    }
}