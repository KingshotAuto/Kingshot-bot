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
using System.Collections.Generic;

namespace Bot.Core.ImageDetection
{
    /// <summary>
    /// Enhanced grayscale template matching service with improved caching, memory management, and performance.
    /// Based on best practices from wosbot-master implementation.
    /// </summary>
    public class EnhancedGrayscaleTemplateMatchingService : IDisposable
    {
        private readonly LogService _logger;
        
        // Byte-level cache for raw template data
        private readonly ConcurrentDictionary<string, byte[]> _templateBytesCache;
        
        // Mat cache for processed templates
        private readonly ConcurrentDictionary<string, (Mat Template, DateTime LastUsed, int RefCount)> _templateCache;
        
        // Scaled template cache
        private readonly ConcurrentDictionary<(string, double), (Mat Mat, DateTime LastUsed, int RefCount)> _scaledTemplateCache;
        
        // Dedicated thread pool for OpenCV operations (similar to wosbot-master's ForkJoinPool)
        private readonly TaskScheduler _openCvTaskScheduler;
        private readonly CancellationTokenSource _cancellationTokenSource;
        
        private bool _disposed;
        private const int MAX_CACHE_SIZE = 100; // Increased from 50
        private const int CACHE_CLEANUP_INTERVAL_MS = 60000;
        private readonly System.Threading.Timer _cleanupTimer;
        private readonly object _cacheLock = new object();
        
        // State of cache initialization
        private volatile bool _cacheInitialized = false;
        
        public static readonly double[] StandardScales = { 0.8, 0.9, 1.0, 1.1, 1.2 };
        private const int PARALLEL_THRESHOLD = 1000;

        public EnhancedGrayscaleTemplateMatchingService(LogService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _templateBytesCache = new ConcurrentDictionary<string, byte[]>();
            _templateCache = new ConcurrentDictionary<string, (Mat Template, DateTime LastUsed, int RefCount)>();
            _scaledTemplateCache = new ConcurrentDictionary<(string, double), (Mat Mat, DateTime LastUsed, int RefCount)>();
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Create dedicated task scheduler for OpenCV operations
            var openCvThreads = Math.Min(Environment.ProcessorCount, 4);
            _openCvTaskScheduler = new LimitedConcurrencyLevelTaskScheduler(openCvThreads);
            
            _cleanupTimer = new System.Threading.Timer(CleanupCaches, null, CACHE_CLEANUP_INTERVAL_MS, CACHE_CLEANUP_INTERVAL_MS);

            // Background preloading of common templates
            _ = Task.Run(InitializeTemplateCacheAsync, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Initializes template cache by preloading commonly used templates in background
        /// </summary>
        private async Task InitializeTemplateCacheAsync()
        {
            if (_cacheInitialized) return;

            try
            {
                _logger.LogInfo("Initializing template cache...");

                // Get all GameTemplates and preload the most common ones
                var commonTemplates = new[]
                {
                    GameTemplates.BUTTONS_BACK,
                    GameTemplates.BUTTONS_CLOSE,
                    GameTemplates.BUTTONS_CONFIRM,
                    GameTemplates.BUTTONS_OK,
                    GameTemplates.RECOVERY_HOME_BUTTON,
                    GameTemplates.LOCATOR_MAP_BUTTON
                };

                var preloadTasks = commonTemplates
                    .Where(template => template.Exists())
                    .Select(template => PreloadTemplateAsync(template))
                    .ToArray();

                await Task.WhenAll(preloadTasks);

                _cacheInitialized = true;
                _logger.LogInfo($"Template cache initialized with {_templateCache.Count} templates");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error initializing template cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Preloads a specific template in background
        /// </summary>
        public async Task PreloadTemplateAsync(GameTemplates template)
        {
            if (!template.Exists()) return;

            var templatePath = template.GetTemplatePath();
            await Task.Factory.StartNew(() =>
            {
                try
                {
                    LoadTemplateOptimized(templatePath);
                    _logger.LogInfo($"Template {template} preloaded successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error preloading template {template}: {ex.Message}");
                }
            }, _cancellationTokenSource.Token, TaskCreationOptions.None, _openCvTaskScheduler);
        }

        /// <summary>
        /// Enhanced template loading with byte-level caching (inspired by wosbot-master)
        /// </summary>
        private Mat LoadTemplateOptimized(string templatePath)
        {
            // Try to get from Mat cache first
            if (_templateCache.TryGetValue(templatePath, out var cachedTemplate) && !cachedTemplate.Template.IsDisposed)
            {
                return cachedTemplate.Template.Clone(); // Return copy for thread safety
            }

            try
            {
                // Load bytes from cache or from file
                var templateBytes = _templateBytesCache.GetOrAdd(templatePath, path =>
                {
                    if (!File.Exists(path))
                    {
                        _logger.LogError($"Template file not found: {path}");
                        return null;
                    }

                    try
                    {
                        return File.ReadAllBytes(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error reading template bytes for: {path}: {ex.Message}");
                        return null;
                    }
                });

                if (templateBytes == null)
                {
                    return new Mat(); // Return empty Mat
                }

                // Decode template from bytes
                var templateBGR = Cv2.ImDecode(templateBytes, ImreadModes.Color);
                if (templateBGR.Empty())
                {
                    _logger.LogError($"Could not decode template: {templatePath}");
                    return new Mat();
                }

                // Convert to grayscale
                var templateGray = new Mat();
                Cv2.CvtColor(templateBGR, templateGray, ColorConversionCodes.BGR2GRAY);
                templateBGR.Dispose();

                // Store in Mat cache
                lock (_cacheLock)
                {
                    var now = DateTime.UtcNow;
                    _templateCache.AddOrUpdate(
                        templatePath,
                        (templateGray.Clone(), now, 1),
                        (_, old) =>
                        {
                            old.Template?.Dispose(); // Dispose old Mat
                            return (templateGray.Clone(), now, old.RefCount + 1);
                        });
                }

                return templateGray;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception loading template: {templatePath}: {ex.Message}");
                return new Mat();
            }
        }

        private void CleanupCaches(object? state)
        {
            if (_disposed) return;

            try
            {
                lock (_cacheLock)
                {
                    // Clean up template cache
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

                    // Clean up scaled template cache
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

                    // Remove old unused entries
                    var cutoff = DateTime.UtcNow.AddHours(-1);
                    
                    var expiredTemplateKeys = _templateCache
                        .Where(kvp => kvp.Value.LastUsed < cutoff && kvp.Value.RefCount == 0)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var key in expiredTemplateKeys)
                    {
                        if (_templateCache.TryRemove(key, out var oldValue))
                        {
                            oldValue.Template?.Dispose();
                        }
                    }

                    var expiredScaledKeys = _scaledTemplateCache
                        .Where(kvp => kvp.Value.LastUsed < cutoff && kvp.Value.RefCount == 0)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredScaledKeys)
                    {
                        if (_scaledTemplateCache.TryRemove(key, out var oldValue))
                        {
                            oldValue.Mat?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during enhanced cache cleanup: {ex.Message}");
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

        /// <summary>
        /// Enhanced template matching with enum support
        /// </summary>
        public (bool found, Rectangle matchRect, double confidence) MatchTemplate(
            byte[] screenshot,
            GameTemplates template,
            double threshold = 0.6,
            double[]? scales = null,
            bool verboseLogging = false,
            Rectangle? searchArea = null)
        {
            if (!template.Exists())
            {
                _logger.LogError($"Template {template} does not exist");
                return (false, Rectangle.Empty, 0);
            }

            return MatchTemplate(screenshot, template.GetTemplatePath(), threshold, scales, verboseLogging, searchArea);
        }

        /// <summary>
        /// Original string-based template matching (maintained for backward compatibility)
        /// </summary>
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

            Mat screenshotGray = null;
            Mat sourceMat = null;

            try
            {
                using var ms = new MemoryStream(screenshot);
                using var bmp = new Bitmap(ms);
                sourceMat = BitmapConverter.ToMat(bmp);

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

                if (searchArea.HasValue)
                {
                    var rect = searchArea.Value;
                    var ocvRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                    if (ocvRect.Right > screenshotGray.Width || ocvRect.Bottom > screenshotGray.Height || ocvRect.X < 0 || ocvRect.Y < 0)
                    {
                        _logger.LogWarning($"Invalid search area: {rect}. Screenshot size: {screenshotGray.Width}x{screenshotGray.Height}. Using full image.");
                        return ExecuteEnhancedMatching(screenshotGray, templatePath, threshold, scales, verboseLogging);
                    }
                    using var croppedSource = new Mat(screenshotGray, ocvRect);
                    var result = ExecuteEnhancedMatching(croppedSource, templatePath, threshold, scales, verboseLogging);
                    if (result.found)
                    {
                        return (true,
                            new Rectangle(result.matchRect.X + rect.X, result.matchRect.Y + rect.Y,
                                result.matchRect.Width, result.matchRect.Height),
                            result.confidence);
                    }
                    return (false, Rectangle.Empty, result.confidence);
                }

                return ExecuteEnhancedMatching(screenshotGray, templatePath, threshold, scales, verboseLogging);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError($"Screenshot data for {Path.GetFileName(templatePath)} is corrupted: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Enhanced template matching failed for {templatePath}: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
            finally
            {
                // Explicit cleanup - inspired by wosbot-master's finally blocks
                screenshotGray?.Dispose();
                sourceMat?.Dispose();
            }
        }

        private (bool found, Rectangle matchRect, double confidence) ExecuteEnhancedMatching(
            Mat screenshotGray, string templatePath, double threshold, double[] scales, bool verboseLogging)
        {
            if (screenshotGray.Empty())
            {
                _logger.LogError("Screenshot image is empty");
                return (false, Rectangle.Empty, 0);
            }

            Mat templateGray = null;
            
            try
            {
                templateGray = LoadTemplateOptimized(templatePath);
                if (templateGray.Empty())
                {
                    return (false, Rectangle.Empty, 0);
                }

                return FindBestMatchEnhanced(screenshotGray, templateGray, templatePath, threshold, scales, verboseLogging);
            }
            finally
            {
                // Update reference count
                lock (_cacheLock)
                {
                    if (_templateCache.TryGetValue(templatePath, out var cached))
                    {
                        _templateCache[templatePath] = (cached.Template, cached.LastUsed, Math.Max(0, cached.RefCount - 1));
                    }
                }
                
                // Don't dispose templateGray here as it's managed by the cache
            }
        }

        private (bool found, Rectangle matchRect, double confidence) FindBestMatchEnhanced(
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
                Mat scaledTemplate = null;
                Mat result = null;

                try
                {
                    scaledTemplate = GetOrCreateScaledTemplate(templatePath, template, scale);
                    result = new Mat();
                    matsToDispose.Add(result);

                    Cv2.MatchTemplate(screenshotImg, scaledTemplate, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);

                    results.Add((maxVal, new Rectangle(maxLoc.X, maxLoc.Y, scaledTemplate.Width, scaledTemplate.Height), scale));

                    if (verboseLogging)
                    {
                        _logger.LogInfo($"Scale {scale:F2}: confidence {maxVal:F4}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error in enhanced scale {scale:F2}: {ex.Message}");
                }
            };

            try
            {
                if (useParallel)
                {
                    // Use custom task scheduler for OpenCV operations
                    var tasks = validScales.Select(scale => 
                        Task.Factory.StartNew(() => processScale(scale), 
                            _cancellationTokenSource.Token,
                            TaskCreationOptions.None,
                            _openCvTaskScheduler)
                    ).ToArray();
                    
                    Task.WaitAll(tasks, _cancellationTokenSource.Token);
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

                    if (verboseLogging)
                    {
                        _logger.LogInfo($"Best match at scale {bestResult.scale:F2}: confidence {bestConfidence:F4}");
                    }
                }

                return (bestConfidence >= threshold, bestRect, bestConfidence);
            }
            finally
            {
                // Enhanced cleanup - dispose all Mats explicitly
                foreach (var mat in matsToDispose)
                {
                    try
                    {
                        mat?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error disposing Mat in enhanced matching: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Async template matching for better performance
        /// </summary>
        public async Task<(bool found, Rectangle matchRect, double confidence)> MatchTemplateAsync(
            byte[] screenshot,
            GameTemplates template,
            double threshold = 0.6,
            double[]? scales = null,
            bool verboseLogging = false,
            Rectangle? searchArea = null)
        {
            return await Task.Factory.StartNew(() =>
                MatchTemplate(screenshot, template, threshold, scales, verboseLogging, searchArea),
                _cancellationTokenSource.Token,
                TaskCreationOptions.None,
                _openCvTaskScheduler);
        }

        /// <summary>
        /// Multiple template matching with intelligent suppression (inspired by wosbot-master)
        /// </summary>
        public List<(bool found, Rectangle matchRect, double confidence)> SearchTemplateMultiple(
            byte[] screenshot,
            GameTemplates template,
            double threshold = 0.6,
            int maxResults = 0,
            double[]? scales = null,
            Rectangle? searchArea = null)
        {
            if (!template.Exists())
            {
                _logger.LogError($"Template {template} does not exist");
                return new List<(bool, Rectangle, double)>();
            }

            return SearchTemplateMultiple(screenshot, template.GetTemplatePath(), threshold, maxResults, scales, searchArea);
        }

        /// <summary>
        /// Multiple template matching with string path
        /// </summary>
        public List<(bool found, Rectangle matchRect, double confidence)> SearchTemplateMultiple(
            byte[] screenshot,
            string templatePath,
            double threshold = 0.6,
            int maxResults = 0,
            double[]? scales = null,
            Rectangle? searchArea = null)
        {
            scales ??= StandardScales;
            var results = new List<(bool found, Rectangle matchRect, double confidence)>();

            if (!File.Exists(templatePath))
            {
                _logger.LogError($"Template not found: {templatePath}");
                return results;
            }

            Mat screenshotGray = null;
            Mat sourceMat = null;

            try
            {
                using var ms = new MemoryStream(screenshot);
                using var bmp = new Bitmap(ms);
                sourceMat = BitmapConverter.ToMat(bmp);

                // Convert to grayscale
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

                // Handle search area
                if (searchArea.HasValue)
                {
                    var rect = searchArea.Value;
                    var ocvRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                    if (ocvRect.Right <= screenshotGray.Width && ocvRect.Bottom <= screenshotGray.Height && ocvRect.X >= 0 && ocvRect.Y >= 0)
                    {
                        using var croppedSource = new Mat(screenshotGray, ocvRect);
                        var croppedResults = ExecuteMultipleMatching(croppedSource, templatePath, threshold, maxResults, scales);
                        
                        // Adjust coordinates back to original image
                        foreach (var result in croppedResults)
                        {
                            if (result.found)
                            {
                                var adjustedRect = new Rectangle(
                                    result.matchRect.X + rect.X,
                                    result.matchRect.Y + rect.Y,
                                    result.matchRect.Width,
                                    result.matchRect.Height);
                                results.Add((true, adjustedRect, result.confidence));
                            }
                        }
                        return results;
                    }
                }

                return ExecuteMultipleMatching(screenshotGray, templatePath, threshold, maxResults, scales);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Multiple template matching failed for {templatePath}: {ex.Message}");
                return results;
            }
            finally
            {
                screenshotGray?.Dispose();
                sourceMat?.Dispose();
            }
        }

        private List<(bool found, Rectangle matchRect, double confidence)> ExecuteMultipleMatching(
            Mat screenshotGray, string templatePath, double threshold, int maxResults, double[] scales)
        {
            var results = new List<(bool found, Rectangle matchRect, double confidence)>();
            
            if (screenshotGray.Empty())
            {
                return results;
            }

            Mat templateGray = null;
            
            try
            {
                templateGray = LoadTemplateOptimized(templatePath);
                if (templateGray.Empty())
                {
                    return results;
                }

                return FindMultipleMatches(screenshotGray, templateGray, templatePath, threshold, maxResults, scales);
            }
            finally
            {
                // Update reference count
                lock (_cacheLock)
                {
                    if (_templateCache.TryGetValue(templatePath, out var cached))
                    {
                        _templateCache[templatePath] = (cached.Template, cached.LastUsed, Math.Max(0, cached.RefCount - 1));
                    }
                }
            }
        }

        private List<(bool found, Rectangle matchRect, double confidence)> FindMultipleMatches(
            Mat screenshotImg, Mat template, string templatePath, double threshold, int maxResults, double[] scales)
        {
            var allResults = new List<(double confidence, Rectangle rect, double scale)>();

            // Process all scales and collect results
            foreach (var scale in scales)
            {
                int newWidth = (int)(template.Width * scale);
                int newHeight = (int)(template.Height * scale);

                if (newWidth < 1 || newHeight < 1 || newWidth > screenshotImg.Width || newHeight > screenshotImg.Height)
                {
                    continue;
                }

                Mat scaledTemplate = null;
                Mat matchResult = null;

                try
                {
                    scaledTemplate = GetOrCreateScaledTemplate(templatePath, template, scale);
                    
                    int resultCols = screenshotImg.Width - scaledTemplate.Width + 1;
                    int resultRows = screenshotImg.Height - scaledTemplate.Height + 1;
                    
                    if (resultCols <= 0 || resultRows <= 0)
                    {
                        continue;
                    }

                    matchResult = new Mat(resultRows, resultCols, MatType.CV_32FC1);
                    Cv2.MatchTemplate(screenshotImg, scaledTemplate, matchResult, TemplateMatchModes.CCoeffNormed);

                    // Find all matches above threshold using intelligent suppression
                    var scaleResults = FindAllMatchesWithSuppression(matchResult, threshold, scaledTemplate.Width, scaledTemplate.Height, maxResults);
                    
                    foreach (var (confidence, location) in scaleResults)
                    {
                        allResults.Add((confidence, new Rectangle(location.X, location.Y, scaledTemplate.Width, scaledTemplate.Height), scale));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error in multiple matching scale {scale:F2}: {ex.Message}");
                }
                finally
                {
                    matchResult?.Dispose();
                }
            }

            // Sort by confidence and return top results
            var sortedResults = allResults
                .OrderByDescending(r => r.confidence)
                .Take(maxResults > 0 ? maxResults : allResults.Count)
                .Select(r => (true, r.rect, r.confidence))
                .ToList();

            return sortedResults;
        }

        /// <summary>
        /// Finds all matches with intelligent non-maximum suppression (inspired by wosbot-master)
        /// </summary>
        private List<(double confidence, Point location)> FindAllMatchesWithSuppression(
            Mat matchResult, double threshold, int templateWidth, int templateHeight, int maxResults)
        {
            var matches = new List<(double confidence, Point location)>();
            var resultCopy = matchResult.Clone();

            try
            {
                int halfTemplateWidth = templateWidth / 2;
                int halfTemplateHeight = templateHeight / 2;

                while (matches.Count < maxResults || maxResults <= 0)
                {
                    Cv2.MinMaxLoc(resultCopy, out _, out double maxVal, out _, out Point maxLoc);

                    if (maxVal < threshold)
                    {
                        break; // No more matches above threshold
                    }

                    matches.Add((maxVal, maxLoc));

                    // Suppression area - zero out the area around the found match
                    int suppressX = Math.Max(0, maxLoc.X - halfTemplateWidth);
                    int suppressY = Math.Max(0, maxLoc.Y - halfTemplateHeight);
                    int suppressWidth = Math.Min(templateWidth, resultCopy.Width - suppressX);
                    int suppressHeight = Math.Min(templateHeight, resultCopy.Height - suppressY);

                    if (suppressWidth > 0 && suppressHeight > 0)
                    {
                        var suppressRect = new Rect(suppressX, suppressY, suppressWidth, suppressHeight);
                        using var suppressArea = new Mat(resultCopy, suppressRect);
                        suppressArea.SetTo(Scalar.All(0));
                    }
                }
            }
            finally
            {
                resultCopy?.Dispose();
            }

            return matches;
        }

        /// <summary>
        /// Async multiple template matching
        /// </summary>
        public async Task<List<(bool found, Rectangle matchRect, double confidence)>> SearchTemplateMultipleAsync(
            byte[] screenshot,
            GameTemplates template,
            double threshold = 0.6,
            int maxResults = 0,
            double[]? scales = null,
            Rectangle? searchArea = null)
        {
            return await Task.Factory.StartNew(() =>
                SearchTemplateMultiple(screenshot, template, threshold, maxResults, scales, searchArea),
                _cancellationTokenSource.Token,
                TaskCreationOptions.None,
                _openCvTaskScheduler);
        }

        /// <summary>
        /// Get cache statistics (for debugging and monitoring)
        /// </summary>
        public string GetCacheStats()
        {
            return $"Templates: {_templateCache.Count}, Scaled: {_scaledTemplateCache.Count}, Bytes: {_templateBytesCache.Count}, Initialized: {_cacheInitialized}";
        }

        /// <summary>
        /// Check if cache is fully initialized
        /// </summary>
        public bool IsCacheInitialized => _cacheInitialized;

        /// <summary>
        /// Clear all caches manually
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
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
                _templateBytesCache.Clear();
                _cacheInitialized = false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _cancellationTokenSource?.Cancel();
                _cleanupTimer?.Dispose();
                
                ClearCache();
                
                _cancellationTokenSource?.Dispose();
            }
        }
    }

    /// <summary>
    /// Custom task scheduler to limit concurrency for OpenCV operations
    /// Similar to wosbot-master's ForkJoinPool approach
    /// </summary>
    internal class LimitedConcurrencyLevelTaskScheduler : TaskScheduler
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly List<Task> _tasks = new();

        public LimitedConcurrencyLevelTaskScheduler(int maximumConcurrencyLevel)
        {
            if (maximumConcurrencyLevel < 1) throw new ArgumentOutOfRangeException(nameof(maximumConcurrencyLevel));
            MaximumConcurrencyLevel = maximumConcurrencyLevel;
            _semaphore = new SemaphoreSlim(maximumConcurrencyLevel, maximumConcurrencyLevel);
        }

        protected sealed override void QueueTask(Task task)
        {
            lock (_tasks) _tasks.Add(task);

            Task.Run(async () =>
            {
                await _semaphore.WaitAsync();
                try
                {
                    TryExecuteTask(task);
                }
                finally
                {
                    _semaphore.Release();
                    lock (_tasks) _tasks.Remove(task);
                }
            });
        }

        protected sealed override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return false;
        }

        protected sealed override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_tasks) return _tasks.ToArray();
        }

        public sealed override int MaximumConcurrencyLevel { get; }
    }
}