using System;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.Utils;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing;
using Tesseract;

namespace Bot.Core.Tasks.Modules
{
    public class AutoTechnologyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoTechnology;
        public override string Name => "Auto Technology";

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting Auto Technology task");

                // Get settings
                var settings = GetTaskSettings<AutoTechnologySettings>(account);
                
                // First, look for and click menu-arrow.png
                var menuArrowFound = await FindAndClickImageAsync("menu-arrow.png", account.InstanceNumber, logger, 
                    threshold: 0.7);
                
                if (!menuArrowFound)
                {
                    logger.LogError($"[{account.AccountName}] Could not find menu-arrow.png");
                    return TaskExecutionDetails.Failed("Could not find menu arrow");
                }

                logger.LogInfo($"[{account.AccountName}] Clicked menu arrow, checking for idle status");
                await Task.Delay(1000, cancellationToken);

                // Use OCR to read text at coordinates 196,817 245,845 looking for "idle"
                var ocrText = await ReadTextFromSpecificRegionAsync(account.InstanceNumber, logger, 
                    new Rectangle(196, 817, 49, 28));

                if (string.IsNullOrEmpty(ocrText) || !ocrText.ToLower().Contains("idle"))
                {
                    logger.LogInfo($"[{account.AccountName}] 'Idle' not detected (OCR result: '{ocrText}'), clicking 467,551 and moving to next module");
                    
                    // Click 467,551 if idle is not detected
                    await ClickAsync(account.InstanceNumber, logger, new Point(467, 551));
                    await Task.Delay(500, cancellationToken);
                    
                    return new TaskExecutionDetails(true, message: "Technology not idle, skipped to next module");
                }

                logger.LogInfo($"[{account.AccountName}] 'Idle' detected, proceeding with technology research");
                
                // Click 407,818 since idle was detected
                await ClickAsync(account.InstanceNumber, logger, new Point(407, 818));
                await Task.Delay(500, cancellationToken);

                // Look for research-icon.png and click it (up to 2 seconds)
                var researchIconFound = false;
                var attempts = 0;
                while (!researchIconFound && attempts < 4)
                {
                    researchIconFound = await FindAndClickImageAsync("research-icon.png", account.InstanceNumber, logger, 
                        threshold: 0.7);
                    if (!researchIconFound)
                    {
                        await Task.Delay(500, cancellationToken);
                        attempts++;
                    }
                }

                if (!researchIconFound)
                {
                    logger.LogError($"[{account.AccountName}] Could not find research-icon.png within 2 seconds");
                    return TaskExecutionDetails.Failed("Could not find research icon");
                }

                logger.LogInfo($"[{account.AccountName}] Found and clicked research icon");
                await Task.Delay(1000, cancellationToken);

                // Click on the appropriate research type based on user settings
                Point researchTypeLocation;
                string researchTypeName;

                switch (settings.ResearchType)
                {
                    case ResearchType.Growth:
                        researchTypeLocation = new Point(130, 116);
                        researchTypeName = "Growth";
                        break;
                    case ResearchType.Economy:
                        researchTypeLocation = new Point(361, 123);
                        researchTypeName = "Economy";
                        break;
                    case ResearchType.Battle:
                        researchTypeLocation = new Point(585, 120);
                        researchTypeName = "Battle";
                        break;
                    default:
                        logger.LogError($"[{account.AccountName}] Invalid research type: {settings.ResearchType}");
                        return TaskExecutionDetails.Failed("Invalid research type configured");
                }

                logger.LogInfo($"[{account.AccountName}] Clicking {researchTypeName} research type at {researchTypeLocation}");
                await ClickAsync(account.InstanceNumber, logger, researchTypeLocation);
                await Task.Delay(1000, cancellationToken);

                // Use improved OCR to detect "x/x" pattern and find the highest one on screen
                logger.LogInfo($"[{account.AccountName}] Scanning for x/x progress patterns using improved OCR");
                
                var progressPattern = await FindProgressPatternWithImprovedOCRAsync(account.InstanceNumber, logger);
                
                if (progressPattern.HasValue)
                {
                    logger.LogInfo($"[{account.AccountName}] Found progress pattern, clicking at {progressPattern.Value}");
                    await ClickAsync(account.InstanceNumber, logger, progressPattern.Value);
                    await Task.Delay(1000, cancellationToken);
                    
                    return new TaskExecutionDetails(true, message: $"Successfully started {researchTypeName} research");
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] Could not find any x/x progress pattern on screen");
                    return TaskExecutionDetails.Failed("Could not find research progress pattern");
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] Auto Technology task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Auto Technology task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Auto Technology failed: {ex.Message}");
            }
        }

        private async Task<Point?> FindProgressPatternWithImprovedOCRAsync(int instanceNumber, LogService logger)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError("Could not take screenshot for progress pattern detection");
                    return null;
                }

                using var ms = new System.IO.MemoryStream(screenshot);
                using var bitmap = new Bitmap(ms);
                
                // Create optimized OCR configuration specifically for x/x patterns
                var ocrConfig = new OCRConfiguration
                {
                    ScaleFactor = 5, // Higher scale for better recognition
                    AdaptiveC = 8, // Better contrast adaptation
                    MedianBlurKernelSize = 3, // Reduce noise
                    CharacterWhitelist = "0123456789/", // Only numbers and slash
                    PageSegMode = PageSegMode.SingleWord // Better for short patterns
                };

                var regex = new Regex(@"\b\d+/\d+\b", RegexOptions.IgnoreCase);
                Point? highestMatch = null;
                int highestY = int.MaxValue;

                logger.LogInfo($"Starting improved OCR scan for x/x patterns...");

                // Scan in larger, more targeted regions
                for (int y = 100; y < Math.Min(bitmap.Height - 100, 700); y += 60) // Larger steps
                {
                    for (int x = 50; x < Math.Min(bitmap.Width - 150, 650); x += 80) // Larger regions
                    {
                        var region = new Rectangle(x, y, 120, 50); // Larger OCR regions
                        
                        try
                        {
                            using var ocr = new OCRService(logger, ocrConfig);
                            
                            // Convert region to byte array for OCR
                            using var regionMs = new System.IO.MemoryStream();
                            using (var regionBitmap = bitmap.Clone(region, bitmap.PixelFormat))
                            {
                                regionBitmap.Save(regionMs, System.Drawing.Imaging.ImageFormat.Png);
                            }
                            var regionBytes = regionMs.ToArray();
                            
                            string ocrText = ocr.ExtractTextFromScreenArea(regionBytes, new Rectangle(0, 0, region.Width, region.Height));
                            
                            if (!string.IsNullOrEmpty(ocrText))
                            {
                                // Clean up the OCR text
                                string originalText = ocrText;
                                ocrText = ocrText.Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "");
                                
                                // Debug logging - show all non-empty OCR results
                                if (ocrText.Length > 0)
                                {
                                    logger.LogInfo($"OCR extracted text: '{ocrText}' (original: '{originalText}') at {region}");
                                }
                                
                                if (regex.IsMatch(ocrText))
                                {
                                    logger.LogInfo($"✅ REGEX MATCH! Found progress pattern '{ocrText}' at region {region}");
                                    
                                    // Check if this is higher on screen (lower Y value)
                                    if (y < highestY)
                                    {
                                        highestY = y;
                                        highestMatch = new Point(x + region.Width / 2, y + region.Height / 2);
                                        logger.LogInfo($"🎯 New highest progress pattern at Y={y}: {ocrText}");
                                    }
                                }
                                else if (ocrText.Length > 0)
                                {
                                    // Enhanced debugging for pattern analysis
                                    bool hasSlash = ocrText.Contains("/");
                                    bool hasDigit = ocrText.Any(char.IsDigit);
                                    bool startsWithDigit = char.IsDigit(ocrText[0]);
                                    
                                    logger.LogInfo($"📊 OCR Analysis: '{ocrText}' | HasSlash: {hasSlash} | HasDigit: {hasDigit} | StartsWithDigit: {startsWithDigit}");
                                    
                                    // Try some pattern variations to see what might work
                                    var relaxedPatterns = new[]
                                    {
                                        @"\d+/\d+",        // Basic x/x
                                        @"\d+\/\d+",       // Escaped slash
                                        @"\d+.\d+",        // Any character between digits
                                        @"\d+[\s/]\d+",    // Space or slash between digits
                                    };
                                    
                                    foreach (var pattern in relaxedPatterns)
                                    {
                                        if (Regex.IsMatch(ocrText, pattern))
                                        {
                                            logger.LogInfo($"🔍 Alternative pattern '{pattern}' matches: {ocrText}");
                                        }
                                    }
                                    
                                    if (hasSlash || (hasDigit && ocrText.Length >= 3))
                                    {
                                        logger.LogInfo($"🔍 Potential pattern candidate: '{ocrText}' at {region}");
                                    }
                                }
                            }
                            else
                            {
                                // Log empty results occasionally for debugging scan coverage
                                if ((x + y) % 240 == 0) // Log every few empty regions
                                {
                                    logger.LogInfo($"📋 Empty OCR result at {region}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"OCR error at region {region}: {ex.Message}");
                        }
                    }
                }

                if (highestMatch.HasValue)
                {
                    logger.LogInfo($"Selected highest progress pattern at {highestMatch.Value}");
                    return highestMatch;
                }
                else
                {
                    logger.LogWarning("No x/x progress patterns found with improved OCR, trying adjacent region detection...");
                    
                    // Try a different approach - look for adjacent regions with numbers
                    var adjacentMatch = await FindAdjacentNumberPatternsAsync(instanceNumber, logger);
                    
                    if (adjacentMatch.HasValue)
                    {
                        logger.LogInfo($"Found adjacent number pattern at {adjacentMatch.Value}");
                        return adjacentMatch;
                    }
                    
                    logger.LogWarning("No progress patterns found with any method");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in improved OCR progress pattern detection: {ex.Message}");
                return null;
            }
        }

        private async Task<Point?> FindAdjacentNumberPatternsAsync(int instanceNumber, LogService logger)
        {
            try
            {
                logger.LogInfo("🔍 Trying adjacent number pattern detection...");
                
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) return null;

                using var ms = new System.IO.MemoryStream(screenshot);
                using var bitmap = new Bitmap(ms);

                var numberOcrConfig = new OCRConfiguration
                {
                    ScaleFactor = 4,
                    AdaptiveC = 7,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "0123456789", // Only numbers
                    PageSegMode = PageSegMode.SingleChar
                };

                var slashOcrConfig = new OCRConfiguration
                {
                    ScaleFactor = 4,
                    AdaptiveC = 7,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "/", // Only slash
                    PageSegMode = PageSegMode.SingleChar
                };

                // Look for patterns where we have: [number] [slash] [number] in nearby regions
                for (int y = 100; y < Math.Min(bitmap.Height - 50, 600); y += 40)
                {
                    for (int x = 50; x < Math.Min(bitmap.Width - 150, 600); x += 60)
                    {
                        try
                        {
                            // Check three adjacent small regions: [num][/][num]
                            var leftRegion = new Rectangle(x, y, 30, 25);
                            var middleRegion = new Rectangle(x + 25, y, 20, 25);
                            var rightRegion = new Rectangle(x + 40, y, 30, 25);

                            string leftText = "", middleText = "", rightText = "";

                            // OCR left region (number)
                            using (var leftOcr = new OCRService(logger, numberOcrConfig))
                            {
                                using var leftMs = new System.IO.MemoryStream();
                                using (var leftBitmap = bitmap.Clone(leftRegion, bitmap.PixelFormat))
                                {
                                    leftBitmap.Save(leftMs, System.Drawing.Imaging.ImageFormat.Png);
                                }
                                leftText = leftOcr.ExtractTextFromScreenArea(leftMs.ToArray(), 
                                    new Rectangle(0, 0, leftRegion.Width, leftRegion.Height)).Trim();
                            }

                            // OCR middle region (slash)
                            using (var middleOcr = new OCRService(logger, slashOcrConfig))
                            {
                                using var middleMs = new System.IO.MemoryStream();
                                using (var middleBitmap = bitmap.Clone(middleRegion, bitmap.PixelFormat))
                                {
                                    middleBitmap.Save(middleMs, System.Drawing.Imaging.ImageFormat.Png);
                                }
                                middleText = middleOcr.ExtractTextFromScreenArea(middleMs.ToArray(), 
                                    new Rectangle(0, 0, middleRegion.Width, middleRegion.Height)).Trim();
                            }

                            // OCR right region (number)
                            using (var rightOcr = new OCRService(logger, numberOcrConfig))
                            {
                                using var rightMs = new System.IO.MemoryStream();
                                using (var rightBitmap = bitmap.Clone(rightRegion, bitmap.PixelFormat))
                                {
                                    rightBitmap.Save(rightMs, System.Drawing.Imaging.ImageFormat.Png);
                                }
                                rightText = rightOcr.ExtractTextFromScreenArea(rightMs.ToArray(), 
                                    new Rectangle(0, 0, rightRegion.Width, rightRegion.Height)).Trim();
                            }

                            // Check if we found a pattern
                            bool hasLeftNum = !string.IsNullOrEmpty(leftText) && leftText.All(char.IsDigit);
                            bool hasSlash = middleText.Contains("/");
                            bool hasRightNum = !string.IsNullOrEmpty(rightText) && rightText.All(char.IsDigit);

                            if (hasLeftNum || hasSlash || hasRightNum)
                            {
                                logger.LogInfo($"🔍 Adjacent regions at Y={y}: Left='{leftText}' Middle='{middleText}' Right='{rightText}'");
                                logger.LogInfo($"    Analysis: LeftNum={hasLeftNum} Slash={hasSlash} RightNum={hasRightNum}");
                            }

                            if (hasLeftNum && hasSlash && hasRightNum)
                            {
                                string fullPattern = $"{leftText}/{rightText}";
                                logger.LogInfo($"🎯 Found adjacent pattern: {fullPattern} at Y={y}");
                                return new Point(x + 35, y + 12); // Center of the pattern
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"Error in adjacent pattern detection at {x},{y}: {ex.Message}");
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in adjacent pattern detection: {ex.Message}");
                return null;
            }
        }

        private async Task<string> ReadTextFromSpecificRegionAsync(int instanceNumber, LogService logger, Rectangle region)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) return string.Empty;

                using var ms = new System.IO.MemoryStream(screenshot);
                using var bitmap = new Bitmap(ms);
                
                // OCR configuration optimized for "idle" text detection
                var ocrConfig = new OCRConfiguration
                {
                    ScaleFactor = 4,
                    AdaptiveC = 6,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", // Only letters for "idle"
                    PageSegMode = PageSegMode.SingleWord
                };

                using var ocr = new OCRService(logger, ocrConfig);
                
                // Convert region to byte array for OCR
                using var regionMs = new System.IO.MemoryStream();
                using (var regionBitmap = bitmap.Clone(region, bitmap.PixelFormat))
                {
                    regionBitmap.Save(regionMs, System.Drawing.Imaging.ImageFormat.Png);
                }
                var regionBytes = regionMs.ToArray();
                
                string ocrText = ocr.ExtractTextFromScreenArea(regionBytes, new Rectangle(0, 0, region.Width, region.Height));
                
                return ocrText?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error reading text from region: {ex.Message}");
                return string.Empty;
            }
        }

        private AutoTechnologySettings GetTaskSettings<T>(AccountSettings account) where T : new()
        {
            if (!account.TaskSettings.TryGetValue("AutoTechnology", out var settingsJson))
            {
                return new AutoTechnologySettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AutoTechnologySettings>(settingsJson) ?? new AutoTechnologySettings();
            }
            catch
            {
                return new AutoTechnologySettings();
            }
        }
    }

    public enum ResearchType
    {
        Growth,
        Economy,
        Battle
    }

    public class AutoTechnologySettings
    {
        public ResearchType ResearchType { get; set; } = ResearchType.Growth;
        public bool IsEnabled { get; set; } = true;
    }
}