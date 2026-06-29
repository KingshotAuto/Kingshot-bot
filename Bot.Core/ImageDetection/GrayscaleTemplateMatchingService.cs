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
using System.Threading.Tasks;

namespace Bot.Core.ImageDetection
{
    public class GrayscaleTemplateMatchingService : IDisposable
    {
        private readonly LogService _logger;
        private readonly ConcurrentDictionary<string, (Mat Template, DateTime LastUsed, int RefCount)> _templateCache;
        private readonly ConcurrentDictionary<(string, double), (Mat Mat, DateTime LastUsed, int RefCount)> _scaledTemplateCache;
        private bool _disposed;
        private const int MAX_CACHE_SIZE = 50;
        private const int CACHE_CLEANUP_INTERVAL_MS = 60000;
        private readonly System.Threading.Timer _cleanupTimer;
        private readonly object _cacheLock = new object();
        
        public static readonly double[] StandardScales = { 0.8, 0.9, 1.0, 1.1, 1.2 };
        private const int PARALLEL_THRESHOLD = 1000;

        public GrayscaleTemplateMatchingService(LogService logger)
        {
            _logger = logger;
            _templateCache = new ConcurrentDictionary<string, (Mat Template, DateTime LastUsed, int RefCount)>();
            _scaledTemplateCache = new ConcurrentDictionary<(string, double), (Mat Mat, DateTime LastUsed, int RefCount)>();
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
                            oldValue.Template?.Dispose();
                        }
                        else
                        {
                            break;
                        }
                    }

                    while (_scaledTemplateCache.Count > MAX_CACHE_SIZE)
                    {
                        var oldestKey = _scaledTemplateCache
                            .Where(kvp => kvp.Value.RefCount == 0)
                            .OrderBy(kvp => kvp.Value.LastUsed)
                            .Select(kvp => kvp.Key)
                            .FirstOrDefault();
                        
                        if (!oldestKey.Equals(default((string, double))) && _scaledTemplateCache.TryRemove(oldestKey, out var oldValue))
                        {
                            oldValue.Mat?.Dispose();
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
                            oldValue.Template?.Dispose();
                        }
                    }

                    foreach (var kvp in _scaledTemplateCache.Where(kvp => kvp.Value.LastUsed < cutoff && kvp.Value.RefCount == 0))
                    {
                        if (_scaledTemplateCache.TryRemove(kvp.Key, out var oldValue))
                        {
                            oldValue.Mat?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during grayscale template cache cleanup: {ex.Message}");
            }
        }

        private Mat GetOrLoadTemplate(string templatePath)
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

                        var templateGray = new Mat();
                        Cv2.CvtColor(templateBGR, templateGray, ColorConversionCodes.BGR2GRAY);
                        templateBGR.Dispose();
                        return (templateGray, now, 1);
                    },
                    (_, old) =>
                    {
                        return (old.Template, now, old.RefCount + 1);
                    });
                
                return value.Template;
            }
        }

        private Mat GetOrCreateScaledTemplate(string templatePath, Mat originalTemplate, double scale)
        {
            var now = DateTime.UtcNow;
            
            lock (_cacheLock)
            {
                var value = _scaledTemplateCache.AddOrUpdate(
                    (templatePath, scale),
                    _ =>
                    {
                        var scaledTemplate = new Mat();
                        int newWidth = (int)(originalTemplate.Width * scale);
                        int newHeight = (int)(originalTemplate.Height * scale);
                        Cv2.Resize(originalTemplate, scaledTemplate, new Size(newWidth, newHeight));
                        return (scaledTemplate, now, 1);
                    },
                    (_, old) =>
                    {
                        return (old.Mat, now, old.RefCount + 1);
                    });
                
                return value.Mat;
            }
        }

        public (bool found, Rectangle matchRect, double confidence) MatchTemplate(
            byte[] screenshot,
            string templatePath,
            double threshold = 0.6,
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

                Mat screenshotGray;
                if (sourceMat.Channels() == 4)
                {
                    var temp = new Mat();
                    Cv2.CvtColor(sourceMat, temp, ColorConversionCodes.RGBA2BGR);
                    screenshotGray = new Mat();
                    Cv2.CvtColor(temp, screenshotGray, ColorConversionCodes.BGR2GRAY);
                    temp.Dispose();
                }
                else if (sourceMat.Channels() == 3)
                {
                    screenshotGray = new Mat();
                    Cv2.CvtColor(sourceMat, screenshotGray, ColorConversionCodes.BGR2GRAY);
                }
                else
                {
                    screenshotGray = sourceMat.Clone();
                }

                try
                {
                    if (searchArea.HasValue)
                    {
                        var rect = searchArea.Value;
                        var ocvRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                        if (ocvRect.Right > screenshotGray.Width || ocvRect.Bottom > screenshotGray.Height || ocvRect.X < 0 || ocvRect.Y < 0)
                        {
                            _logger.LogInfo($"[DEBUG] Invalid search area: {rect}. Screenshot size: {screenshotGray.Width}x{screenshotGray.Height}. Using full image.");
                            return ExecuteGrayscaleMatching(screenshotGray, templatePath, threshold, scales, verboseLogging);
                        }
                        using var croppedSource = new Mat(screenshotGray, ocvRect);
                        var result = ExecuteGrayscaleMatching(croppedSource, templatePath, threshold, scales, verboseLogging);
                        if (result.found)
                        {
                            return (true,
                                new Rectangle(result.matchRect.X + rect.X, result.matchRect.Y + rect.Y,
                                    result.matchRect.Width, result.matchRect.Height),
                                result.confidence);
                        }
                        return (false, Rectangle.Empty, result.confidence);
                    }

                    return ExecuteGrayscaleMatching(screenshotGray, templatePath, threshold, scales, verboseLogging);
                }
                finally
                {
                    screenshotGray?.Dispose();
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogError($"Screenshot data for {Path.GetFileName(templatePath)} is corrupted and cannot be processed. Error: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Grayscale template matching failed for {templatePath}: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
        }

        private (bool found, Rectangle matchRect, double confidence) ExecuteGrayscaleMatching(
            Mat screenshotGray, string templatePath, double threshold, double[] scales, bool verboseLogging)
        {
            if (screenshotGray.Empty())
            {
                _logger.LogError("Screenshot image is empty");
                return (false, Rectangle.Empty, 0);
            }

            var templateGray = GetOrLoadTemplate(templatePath);

            try
            {
                return FindBestMatch(screenshotGray, templateGray, templatePath, threshold, scales, verboseLogging);
            }
            finally
            {
                lock (_cacheLock)
                {
                    if (_templateCache.TryGetValue(templatePath, out var cached))
                    {
                        _templateCache[templatePath] = (cached.Template, cached.LastUsed, Math.Max(0, cached.RefCount - 1));
                    }
                }
            }
        }

        private (bool found, Rectangle matchRect, double confidence) FindBestMatch(
            Mat screenshotImg, Mat template, string templatePath, double threshold, double[] scales, bool verboseLogging)
        {
            double bestConfidence = -1;
            Rectangle bestRect = Rectangle.Empty;
            bool useParallel = screenshotImg.Width * screenshotImg.Height > PARALLEL_THRESHOLD;

            var validScales = scales.Where(scale =>
            {
                int newWidth = (int)(template.Width * scale);
                int newHeight = (int)(template.Height * scale);
                return newWidth >= 1 && newHeight >= 1 && 
                       newWidth <= screenshotImg.Width && newHeight <= screenshotImg.Height;
            }).ToArray();

            var results = new ConcurrentBag<(double confidence, Rectangle rect, double scale)>();
            var matsToDispose = new ConcurrentBag<Mat>();

            Action<double> processScale = scale =>
            {
                Mat? scaledTemplate = null;
                Mat? result = null;

                try
                {
                    scaledTemplate = GetOrCreateScaledTemplate(templatePath, template, scale);
                    result = new Mat();
                    matsToDispose.Add(result);

                    Cv2.MatchTemplate(screenshotImg, scaledTemplate, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);

                    results.Add((maxVal, new Rectangle(maxLoc.X, maxLoc.Y, scaledTemplate.Width, scaledTemplate.Height), scale));
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error in grayscale scale {scale:F2}: {ex.Message}");
                }
            };

            try
            {
                if (useParallel)
                {
                    Parallel.ForEach(validScales, processScale);
                }
                else
                {
                    foreach (var scale in validScales)
                    {
                        processScale(scale);
                    }
                }

                if (results.Any())
                {
                    var bestResult = results.MaxBy(r => r.confidence);
                    bestConfidence = bestResult.confidence;
                    bestRect = bestResult.rect;
                }

                return (bestConfidence >= threshold, bestRect, bestConfidence);
            }
            finally
            {
                foreach (var mat in matsToDispose)
                {
                    try
                    {
                        mat?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error disposing Mat in grayscale matching: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer.Dispose();
                
                foreach (var (Template, _, _) in _templateCache.Values)
                {
                    Template?.Dispose();
                }
                foreach (var (Mat, _, _) in _scaledTemplateCache.Values)
                {
                    Mat?.Dispose();
                }
                _templateCache.Clear();
                _scaledTemplateCache.Clear();
                _disposed = true;
            }
        }
    }
}