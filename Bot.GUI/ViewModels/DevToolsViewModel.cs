using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WPFClipboard = System.Windows.Clipboard;
using WPFSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WPFOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using Microsoft.Win32;

namespace Bot.GUI.ViewModels
{
    public class DevToolsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private readonly LogService _logger;
        private readonly UnifiedTemplateMatchingService _templateMatcher;
        private readonly ADBController _adbController;
        private BitmapImage? _currentScreenshot;
        private System.Windows.Point? _firstPoint;
        private System.Windows.Point? _secondPoint;
        private Rectangle _selectedArea;
        private bool _autoCopyCoordinates;
        private bool _autoSaveTemplate;
        private string _defaultSaveLocation;
        private double _lastMatchConfidence;
        private string _statusMessage = "";
        private bool _isRectangleVisible;

        public ICommand TakeScreenshotCommand { get; }
        public ICommand LoadTemplateCommand { get; }
        public ICommand TestTemplateCommand { get; }
        public ICommand SelectSaveLocationCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        public BitmapImage? CurrentScreenshot
        {
            get => _currentScreenshot;
            set
            {
                _currentScreenshot = value;
                OnPropertyChanged();
            }
        }

        public bool AutoCopyCoordinates
        {
            get => _autoCopyCoordinates;
            set
            {
                _autoCopyCoordinates = value;
                OnPropertyChanged();
            }
        }

        public bool AutoSaveTemplate
        {
            get => _autoSaveTemplate;
            set
            {
                _autoSaveTemplate = value;
                OnPropertyChanged();
            }
        }

        public string DefaultSaveLocation
        {
            get => _defaultSaveLocation;
            set
            {
                _defaultSaveLocation = value;
                OnPropertyChanged();
            }
        }

        public double LastMatchConfidence
        {
            get => _lastMatchConfidence;
            set
            {
                _lastMatchConfidence = value;
                OnPropertyChanged();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public bool IsRectangleVisible
        {
            get => _isRectangleVisible;
            set
            {
                _isRectangleVisible = value;
                OnPropertyChanged();
            }
        }

        public System.Windows.Point? FirstPoint
        {
            get => _firstPoint;
            set
            {
                _firstPoint = value;
                OnPropertyChanged();
                UpdateRectangleVisibility();
            }
        }

        public System.Windows.Point? SecondPoint
        {
            get => _secondPoint;
            set
            {
                _secondPoint = value;
                OnPropertyChanged();
                UpdateRectangleVisibility();
                if (_secondPoint.HasValue && _firstPoint.HasValue)
                {
                    HandleRectangleComplete();
                }
            }
        }

        public Rectangle SelectedArea
        {
            get => _selectedArea;
            set
            {
                _selectedArea = value;
                OnPropertyChanged();
            }
        }

        public DevToolsViewModel(LogService logger, ADBController adbController)
        {
            _logger = logger;
            _adbController = adbController;
            _templateMatcher = new UnifiedTemplateMatchingService(logger);
            _defaultSaveLocation = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");

            TakeScreenshotCommand = new RelayCommand(async _ => await TakeScreenshot());
            LoadTemplateCommand = new RelayCommand(async _ => await LoadTemplate());
            TestTemplateCommand = new RelayCommand(async _ => await TestTemplate());
            SelectSaveLocationCommand = new RelayCommand(_ => SelectSaveLocation());
            ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        }

        private async Task TakeScreenshot()
        {
            try
            {
                StatusMessage = "Taking screenshot...";
                var screenshotBytes = await _adbController.TakeScreenshotAsync();
                using var ms = new MemoryStream(screenshotBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze(); // Important for cross-thread operations
                CurrentScreenshot = bitmap;
                ClearSelection();
                StatusMessage = "Screenshot taken successfully";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to take screenshot: {ex.Message}");
                StatusMessage = "Failed to take screenshot";
            }
        }

        private void UpdateRectangleVisibility()
        {
            IsRectangleVisible = FirstPoint.HasValue;
            if (FirstPoint.HasValue && SecondPoint.HasValue)
            {
                var x = Math.Min(FirstPoint.Value.X, SecondPoint.Value.X);
                var y = Math.Min(FirstPoint.Value.Y, SecondPoint.Value.Y);
                var width = Math.Abs(SecondPoint.Value.X - FirstPoint.Value.X);
                var height = Math.Abs(SecondPoint.Value.Y - FirstPoint.Value.Y);
                SelectedArea = new Rectangle((int)x, (int)y, (int)width, (int)height);
            }
        }

        private void HandleRectangleComplete()
        {
            if (AutoCopyCoordinates)
            {
                var coordsText = $"X: {SelectedArea.X}, Y: {SelectedArea.Y}, Width: {SelectedArea.Width}, Height: {SelectedArea.Height}";
                WPFClipboard.SetText(coordsText);
                StatusMessage = "Coordinates copied to clipboard";
            }

            if (AutoSaveTemplate && CurrentScreenshot != null)
            {
                SaveTemplateImage();
            }
        }

        private void SaveTemplateImage()
        {
            try
            {
                var saveDialog = new WPFSaveFileDialog
                {
                    InitialDirectory = DefaultSaveLocation,
                    Filter = "PNG Image|*.png",
                    DefaultExt = ".png"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    using var bitmap = new Bitmap(SelectedArea.Width, SelectedArea.Height);
                    using var g = Graphics.FromImage(bitmap);
                    using var screenBmp = BitmapSourceToBitmap(CurrentScreenshot);
                    g.DrawImage(screenBmp, new Rectangle(0, 0, SelectedArea.Width, SelectedArea.Height),
                        SelectedArea, GraphicsUnit.Pixel);
                    bitmap.Save(saveDialog.FileName);
                    StatusMessage = "Template saved successfully";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save template: {ex.Message}");
                StatusMessage = "Failed to save template";
            }
        }

        private void SelectSaveLocation()
        {
            var dialog = new WPFSaveFileDialog
            {
                Title = "Select Save Location",
                FileName = "template.png", // Default filename
                DefaultExt = ".png",
                Filter = "PNG Image|*.png",
                InitialDirectory = DefaultSaveLocation,
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() == true)
            {
                DefaultSaveLocation = System.IO.Path.GetDirectoryName(dialog.FileName) ?? DefaultSaveLocation;
            }
        }

        private void ClearSelection()
        {
            FirstPoint = null;
            SecondPoint = null;
            IsRectangleVisible = false;
        }

        private async Task LoadTemplate()
        {
            var openDialog = new WPFOpenFileDialog
            {
                Filter = "PNG Image|*.png",
                InitialDirectory = DefaultSaveLocation
            };

            if (openDialog.ShowDialog() == true)
            {
                await TestTemplateMatch(openDialog.FileName);
            }
        }

        private async Task TestTemplate()
        {
            var openDialog = new WPFOpenFileDialog
            {
                Filter = "PNG Image|*.png",
                InitialDirectory = DefaultSaveLocation
            };

            if (openDialog.ShowDialog() == true)
            {
                await TestTemplateMatch(openDialog.FileName);
            }
        }

        private async Task TestTemplateMatch(string templatePath)
        {
            try
            {
                StatusMessage = "Testing template match...";
                var screenshotBytes = await _adbController.TakeScreenshotAsync();
                var result = _templateMatcher.MatchTemplate(screenshotBytes, templatePath, 0, 0.6);
                LastMatchConfidence = result.confidence;
                StatusMessage = result.found 
                    ? $"Template found! Confidence: {result.confidence:P2}" 
                    : $"Template not found. Best confidence: {result.confidence:P2}";

                // Update the screenshot and draw the match rectangle if found
                using var ms = new MemoryStream(screenshotBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                CurrentScreenshot = bitmap;

                if (result.found)
                {
                    FirstPoint = new System.Windows.Point(result.matchRect.X, result.matchRect.Y);
                    SecondPoint = new System.Windows.Point(
                        result.matchRect.X + result.matchRect.Width,
                        result.matchRect.Y + result.matchRect.Height);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to test template: {ex.Message}");
                StatusMessage = "Failed to test template";
            }
        }

        private static Bitmap BitmapSourceToBitmap(BitmapSource? source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            
            var bitmap = new Bitmap(
                source.PixelWidth,
                source.PixelHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            
            source.CopyPixels(
                Int32Rect.Empty,
                data.Scan0,
                data.Height * data.Stride,
                data.Stride);
            
            bitmap.UnlockBits(data);
            return bitmap;
        }
    }
} 