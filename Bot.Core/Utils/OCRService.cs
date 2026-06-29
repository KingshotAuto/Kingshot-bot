using System;
using System.Drawing;
using System.IO;
using Bot.Core.Logging;
using Tesseract;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

// Disambiguate conflicting types by using aliases.
using CvSize = OpenCvSharp.Size;
using SysImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Bot.Core.Utils
{
    /// <summary>
    /// Holds configuration settings for the OCRService.
    /// </summary>
    public class OCRConfiguration
    {
        public float ScaleFactor { get; set; } = 1.0f;
        public int MedianBlurKernelSize { get; set; } = 0;
        public double AdaptiveC { get; set; } = 2;
        public string? CharacterWhitelist { get; set; }
        public PageSegMode? PageSegMode { get; set; }
    }

    /// <summary>
    /// Provides OCR capabilities using Tesseract and image preprocessing with OpenCVSharp.
    /// </summary>
    public class OCRService : IDisposable
    {
        private readonly LogService _logger;
        private readonly OCRConfiguration _config;
        private TesseractEngine? _engine;
        private bool _disposed = false;
        private static readonly string _tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        private static readonly string _requiredLanguageFile = Path.Combine(_tessdataPath, "eng.traineddata");

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string dllToLoad);

        public OCRService(LogService logger, OCRConfiguration? config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new OCRConfiguration();
            
            ValidateTessdata();
            
            if (_config.ScaleFactor <= 0)
                throw new ArgumentException("ScaleFactor must be positive", nameof(config));
            
            InitializeTesseract();
        }

        private void ValidateTessdata()
        {
            // Check tessdata directory
            if (!Directory.Exists(_tessdataPath))
            {
                _logger.LogError($"Tesseract tessdata folder not found at: {_tessdataPath}");
                throw new DirectoryNotFoundException($"Tesseract 'tessdata' directory not found at {_tessdataPath}. Please ensure the application is installed correctly.");
            }

            // Check required language file
            if (!File.Exists(_requiredLanguageFile))
            {
                _logger.LogError($"Required English language file not found at: {_requiredLanguageFile}");
                throw new FileNotFoundException($"Required Tesseract language file 'eng.traineddata' not found at {_requiredLanguageFile}. Please ensure the application is installed correctly.");
            }
        }

        private void TryPreloadNativeDlls(string baseDir, string x64Dir, string x86Dir)
        {
            var is64Bit = Environment.Is64BitProcess;
            var dllNames = new[] { "leptonica-1.82.0.dll", "tesseract50.dll" };
            
            // Try multiple locations
            var searchPaths = new List<string>();
            
            if (is64Bit)
            {
                searchPaths.Add(x64Dir);
                searchPaths.Add(baseDir);
            }
            else
            {
                searchPaths.Add(x86Dir);
                searchPaths.Add(baseDir);
            }

            foreach (var dllName in dllNames)
            {
                bool loaded = false;
                foreach (var path in searchPaths)
                {
                    var dllPath = Path.Combine(path, dllName);
                    if (File.Exists(dllPath))
                    {
                        var handle = LoadLibrary(dllPath);
                        if (handle != IntPtr.Zero)
                        {
                            loaded = true;
                            break;
                        }
                        else
                        {
                            var error = Marshal.GetLastWin32Error();
                            _logger.LogWarning($"Failed to preload {dllName} from {dllPath}. Error code: {error}");
                        }
                    }
                }
                
                if (!loaded)
                {
                    _logger.LogWarning($"Could not preload {dllName} from any location. Tesseract will try to find it on its own.");
                }
            }
        }

        private void InitializeTesseract()
        {
            try
            {
                // Set the PATH environment variable to include the application directory and the x64 subdirectory
                var baseDir = AppContext.BaseDirectory;
                var x64Dir = Path.Combine(baseDir, "x64");
                var x86Dir = Path.Combine(baseDir, "x86");
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                
                // Try to preload the DLLs to ensure they're available
                TryPreloadNativeDlls(baseDir, x64Dir, x86Dir);
                
                var pathsToAdd = new List<string>();
                if (Directory.Exists(x64Dir) && !currentPath.Contains(x64Dir))
                {
                    pathsToAdd.Add(x64Dir);
                }
                if (Directory.Exists(x86Dir) && !currentPath.Contains(x86Dir))
                {
                    pathsToAdd.Add(x86Dir);
                }
                if (!currentPath.Contains(baseDir))
                {
                    pathsToAdd.Add(baseDir);
                }

                if (pathsToAdd.Any())
                {
                    var newPath = string.Join(";", pathsToAdd) + ";" + currentPath;
                    Environment.SetEnvironmentVariable("PATH", newPath);
                    _logger.LogInfo($"Updated process PATH to: {newPath}");
                }

                try
                {
                    _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                }
                catch (DllNotFoundException dllEx)
                {
                    _logger.LogError($"DLL not found: {dllEx.Message}");
                    _logger.LogError($"This usually means a required DLL is missing or cannot be found in the search path");
                    throw;
                }
                catch (BadImageFormatException imgEx)
                {
                    _logger.LogError($"Bad image format (wrong architecture?): {imgEx.Message}");
                    _logger.LogError($"This usually means the DLL architecture (32/64-bit) doesn't match the application");
                    throw;
                }
                
                if (_config.PageSegMode.HasValue)
                {
                    _engine.DefaultPageSegMode = _config.PageSegMode.Value;
                }

                if (!string.IsNullOrEmpty(_config.CharacterWhitelist))
                {
                    _engine.SetVariable("tessedit_char_whitelist", _config.CharacterWhitelist);
                }
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException();
                _logger.LogError($"Failed to initialize Tesseract OCR engine: {baseEx.Message}");
                _logger.LogError($"Stack trace: {baseEx.StackTrace}");
                
                if (ex is DllNotFoundException || ex is BadImageFormatException)
                {
                    _logger.LogError("This appears to be a DLL loading issue. Please ensure all required DLLs are present and match the correct architecture.");
                }
                
                throw new InvalidOperationException($"Failed to initialize Tesseract OCR engine. Please ensure all dependencies are properly installed at {_tessdataPath}", baseEx);
            }
        }

        public string ExtractTextFromBitmap(Bitmap bmp)
        {
            if (_engine == null)
            {
                _logger.LogError("Tesseract engine not initialized");
                return string.Empty;
            }

            try
            {
                using var processedBitmap = PreprocessImage(bmp);

                // Debug image saving removed to reduce log spam

                using var ms = new MemoryStream();
                processedBitmap.Save(ms, SysImageFormat.Bmp);
                ms.Position = 0;
                
                using var img = Pix.LoadFromMemory(ms.ToArray());
                using var page = _engine.Process(img);
                string text = page.GetText();
                
                _logger.LogInfo($"OCR extracted text: '{text.Trim()}'");
                return text.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during OCR text extraction: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return string.Empty;
            }
        }

        public string ExtractTextFromScreenArea(byte[] screenshotData, Rectangle area)
        {
            try
            {
                using var ms = new MemoryStream(screenshotData);
                using var fullBitmap = new Bitmap(ms);
                using var croppedBitmap = CropBitmap(fullBitmap, area);
                return ExtractTextFromBitmap(croppedBitmap);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error extracting text from screen area {area}: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return string.Empty;
            }
        }

        /// <summary>
        /// A robust preprocessing pipeline specifically tuned for in-game text.
        /// </summary>
        private Bitmap PreprocessImage(Bitmap bmp)
        {
            try
            {
                using var sourceMat = BitmapConverter.ToMat(bmp);
                
                // 1. Convert to Grayscale
                using var gray = new Mat();
                Cv2.CvtColor(sourceMat, gray, ColorConversionCodes.BGR2GRAY);

                // 2. Scale the image. This is crucial for small text.
                using var scaled = new Mat();
                 if (_config.ScaleFactor > 1.0f)
                {
                    Cv2.Resize(gray, scaled, new CvSize(0, 0), _config.ScaleFactor, _config.ScaleFactor, InterpolationFlags.Cubic);
                }
                else
                {
                    gray.CopyTo(scaled);
                }

                // 3. Invert the image. Tesseract often works best with black text on a white background.
                // This is especially important for text that has a glow or is light-colored.
                using var inverted = new Mat();
                Cv2.BitwiseNot(scaled, inverted);

                // 4. Apply Otsu's thresholding. This is a smart thresholding method that automatically
                // determines the best value to separate the text from the background.
                using var thresholded = new Mat();
                Cv2.Threshold(inverted, thresholded, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                // 5. (Optional but recommended) Dilate the text to make it thicker and repair any breaks
                // in the character strokes that may have occurred during scaling or thresholding.
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new CvSize(2, 2));
                using var dilated = new Mat();
                Cv2.Dilate(thresholded, dilated, kernel);

                return BitmapConverter.ToBitmap(dilated);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error preprocessing image: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return new Bitmap(bmp); // Return copy of original on error
            }
        }

        private Bitmap CropBitmap(Bitmap source, Rectangle cropArea)
        {
            try
            {
                var validCropArea = Rectangle.Intersect(cropArea, new Rectangle(0, 0, source.Width, source.Height));
                
                if (validCropArea.IsEmpty || validCropArea.Width <= 0 || validCropArea.Height <= 0)
                {
                    _logger.LogError($"Crop area {cropArea} is invalid or outside image bounds ({source.Width}x{source.Height})");
                    return new Bitmap(1, 1);
                }

                return source.Clone(validCropArea, source.PixelFormat);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error cropping bitmap: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return new Bitmap(1, 1);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _engine?.Dispose();
                _engine = null;
                _disposed = true;
            }
        }

        ~OCRService()
        {
            Dispose(false);
        }
    }
}
