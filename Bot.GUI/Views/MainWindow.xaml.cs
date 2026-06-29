using System;
using System.Windows;
using System.Windows.Controls;
using Bot.GUI.ViewModels;
using WPFMessageBox = System.Windows.MessageBox;
using WPFApplication = System.Windows.Application;
using WPFColor = System.Windows.Media.Color;
using System.Windows.Media;
using System.Diagnostics;
using System.Security.Principal;
using System.Reflection;
using System.Threading;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Bot.Core.Utils;
using System.Threading.Tasks;
using Bot.GUI.Utils;
using System.Configuration;

namespace Bot.GUI.Views
{
    public partial class MainWindow : Window
    {
        // TODO: set this to the project's public GitHub repository before publishing.
        private const string ProjectUrl = "https://github.com/KingshotAuto/Kingshot-bot";

        public MainViewModel ViewModel { get; } = null!;

        public string Version
        {
            get
            {
                // Get version from entry assembly to ensure consistency with update service
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version != null)
                {
                    // Only use major.minor.build (skip revision)
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
                return "1.0.0";
            }
        }

        public MainWindow()
        {
            // Get the current UI culture for localization
            var currentCulture = Thread.CurrentThread.CurrentUICulture;
            
            // Check if running as administrator
            if (!IsRunningAsAdministrator())
            {
                // Localized messages based on culture
                string title = GetLocalizedString(
                    "Administrator Rights Required",
                    "管理者権限が必要です", // Japanese
                    "需要管理员权限", // Simplified Chinese
                    "需要管理員權限", // Traditional Chinese
                    "Administratorrechte erforderlich", // German
                    "Droits d'administrateur requis" // French
                );

                string message = GetLocalizedString(
                    "This application requires administrator privileges to function properly. Please run as administrator.",
                    "このアプリケーションは管理者権限で実行する必要があります。",
                    "此应用程序需要管理员权限才能正常运行。请以管理员身份运行。",
                    "此應用程序需要管理員權限才能正常運行。請以管理員身份運行。",
                    "Diese Anwendung benötigt Administratorrechte, um ordnungsgemäß zu funktionieren. Bitte als Administrator ausführen.",
                    "Cette application nécessite des privilèges administrateur pour fonctionner correctement. Veuillez exécuter en tant qu'administrateur."
                );

                WPFMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                WPFApplication.Current.Shutdown();
                return;
            }

            try
            {
                ViewModel = MainViewModel.Instance;
                
                // Debug: Check if resources are available before InitializeComponent
                Console.WriteLine("=== Checking Resources Before InitializeComponent ===");
                Console.WriteLine("App Resources:");
                foreach (var key in WPFApplication.Current.Resources.Keys)
                {
                    Console.WriteLine($"  App Resource: {key}");
                }
                
                try
                {
                    InitializeComponent();
                }
                catch (Exception initEx)
                {
                    Console.WriteLine($"InitializeComponent failed: {initEx}");
                    if (initEx.InnerException != null)
                    {
                        Console.WriteLine($"Inner Exception: {initEx.InnerException}");
                    }
                    throw;
                }
                
                DataContext = this;

                // Set up the ConfigEditorView DataContext after window loads
                this.Loaded += MainWindow_Loaded;

                // Ensure proper cleanup when window closes
                this.Closing += MainWindow_Closing;
                
                // Initialize English language banner visibility
                InitializeLanguageBannerVisibility();
                
                // Start UI thread monitoring in debug mode
                #if DEBUG
                UIThreadMonitor.StartMonitoring();
                #endif
                
                // Bind the logs to the TextBox with optimized formatting
                ViewModel.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(ViewModel.CurrentLogMessage))
                    {
                        var msg = ViewModel.CurrentLogMessage;
                        if (!string.IsNullOrEmpty(msg))
                        {
                            // Process log message on background thread, then update UI
                            _ = Task.Run(() =>
                            {
                                var processingThreadId = Thread.CurrentThread.ManagedThreadId;
                                Debug.WriteLine($"UI Log Processing: Background thread {processingThreadId}");
                                string formattedMsg;
                                if (msg == "__CLEAR_LOGS__")
                                {
                                    formattedMsg = "__CLEAR_LOGS__";
                                }
                                else
                                {
                                    // Parse and format the message for better readability on background thread
                                    formattedMsg = FormatLogMessage(msg);
                                }
                                
                                // Now update UI on UI thread using BeginInvoke (non-blocking)
                                Dispatcher.BeginInvoke(() =>
                                {
                                    var uiThreadId = Thread.CurrentThread.ManagedThreadId;
                                    Debug.WriteLine($"UI Log Update: UI thread {uiThreadId}");
                                    if (formattedMsg == "__CLEAR_LOGS__")
                                    {
                                        LogTextBox.Text = "[LOGS CLEARED]\n";
                                    }
                                    else
                                    {
                                        LogTextBox.Text += formattedMsg + "\n";
                                        LogTextBox.ScrollToEnd();
                                        
                                        // Keep only last 1000 lines
                                        var lines = LogTextBox.Text.Split('\n');
                                        if (lines.Length > 1000)
                                        {
                                            LogTextBox.Text = string.Join("\n", lines.Skip(lines.Length - 900));
                                        }
                                    }
                                });
                            });
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                string errorTitle = GetLocalizedString(
                    "Initialization Error",
                    "初期化エラー",
                    "初始化错误",
                    "初始化錯誤",
                    "Initialisierungsfehler",
                    "Erreur d'initialisation"
                );

                WPFMessageBox.Show($"Error: {ex.Message}\n\nDetails: {ex.ToString()}", 
                    errorTitle,
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error
                );
                WPFApplication.Current.Shutdown();
            }
        }

        private string GetLocalizedString(string english, string japanese, string simplifiedChinese, 
            string traditionalChinese, string german, string french)
        {
            var culture = Thread.CurrentThread.CurrentUICulture;
            return culture.TwoLetterISOLanguageName.ToLower() switch
            {
                "ja" => japanese,
                "zh" => culture.Name.ToLower() == "zh-cn" ? simplifiedChinese : traditionalChinese,
                "de" => german,
                "fr" => french,
                _ => english
            };
        }

        private bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                // If there's any error checking admin rights, assume we're not admin
                return false;
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                Console.WriteLine("MainWindow closing - disposing ViewModel...");
                ViewModel?.Dispose();
                Console.WriteLine("ViewModel disposed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing ViewModel during close: {ex.Message}");
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // UI operations must happen on UI thread
            try
            {
                // Set the DataContext for ConfigEditorView
                if (ViewModel != null)
                {
                    ConfigEditorView.DataContext = ViewModel.ConfigVM;
                }
                
                // Initialize LDPlayer path display
                UpdateCurrentLDPlayerPathDisplay();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in MainWindow_Loaded UI setup: {ex}");
            }
        }

        private void BotTab_Click(object sender, RoutedEventArgs e)
        {
            ShowTab("Bot");
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            ShowTab("Settings");
        }

        private void OtherSettingsTab_Click(object sender, RoutedEventArgs e)
        {
            ShowTab("OtherSettings");
        }

        private void HelpTab_Click(object sender, RoutedEventArgs e)
        {
            ShowTab("Help");
        }

        private void LogsTab_Click(object sender, RoutedEventArgs e)
        {
            ShowTab("Logs");
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            // Stop all operations before closing
            try
            {
                ViewModel?.StopCommand.Execute(null);
                ViewModel?.Dispose();
            }
            catch
            {
                // Ignore errors during shutdown
            }
            
            WPFApplication.Current.Shutdown();
        }

        private void OpenSupportWebsite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ProjectUrl + "/issues",
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                WPFMessageBox.Show($"Unable to open website: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void JoinDiscord_Click(object sender, RoutedEventArgs e)
        {
            // Discord invite link would go here - using support website for now
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ProjectUrl + "/discussions",
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                WPFMessageBox.Show($"Unable to open Discord: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenSetupVideo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ProjectUrl + "#readme",
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                WPFMessageBox.Show($"Unable to open video: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowTab(string tabName)
        {
            // Hide all tabs
            BotTabContent.Visibility = Visibility.Collapsed;
            LogsTabContent.Visibility = Visibility.Collapsed;
            SettingsTabContent.Visibility = Visibility.Collapsed;
            OtherSettingsTabContent.Visibility = Visibility.Collapsed;
            HelpTabContent.Visibility = Visibility.Collapsed;

            // Reset all button styles to inactive
            BotTabButton.Style = (Style)FindResource("SidebarButtonStyle");
            LogsTabButton.Style = (Style)FindResource("SidebarButtonStyle");
            SettingsTabButton.Style = (Style)FindResource("SidebarButtonStyle");
            OtherSettingsTabButton.Style = (Style)FindResource("SidebarButtonStyle");
            HelpTabButton.Style = (Style)FindResource("SidebarButtonStyle");

            // Show selected tab and activate button
            switch (tabName)
            {
                case "Bot":
                    BotTabContent.Visibility = Visibility.Visible;
                    BotTabButton.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    break;
                case "Logs":
                    LogsTabContent.Visibility = Visibility.Visible;
                    LogsTabButton.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    break;
                case "Settings":
                    SettingsTabContent.Visibility = Visibility.Visible;
                    SettingsTabButton.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    break;
                case "OtherSettings":
                    OtherSettingsTabContent.Visibility = Visibility.Visible;
                    OtherSettingsTabButton.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    break;
                case "Help":
                    HelpTabContent.Visibility = Visibility.Visible;
                    HelpTabButton.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    break;
            }
        }
        
        // LDPlayer Path Management Methods
        private void UpdateCurrentLDPlayerPathDisplay()
        {
            try
            {
                var ldPlayerPath = LDPlayerHelper.GetLDPlayerConsolePath();
                var directoryPath = Path.GetDirectoryName(ldPlayerPath);
                CurrentLDPlayerPathText.Text = directoryPath ?? "Not found";
                CurrentLDPlayerPathText.Foreground = new SolidColorBrush(WPFColor.FromRgb(76, 175, 80)); // Green
            }
            catch (Exception ex)
            {
                CurrentLDPlayerPathText.Text = $"Auto-detection failed: {ex.Message}";
                CurrentLDPlayerPathText.Foreground = new SolidColorBrush(WPFColor.FromRgb(244, 67, 54)); // Red
            }
        }
        
        private void BrowseLDPlayerPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog();
                dialog.Description = "Select LDPlayer Installation Folder";
                dialog.ShowNewFolderButton = false;
                
                // Try to set initial directory to a common location
                var commonPath = @"C:\LDPlayer";
                if (Directory.Exists(commonPath))
                {
                    dialog.SelectedPath = commonPath;
                }
                
                var result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
                {
                    ManualLDPlayerPathTextBox.Text = dialog.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Error opening folder browser: {ex.Message}", isError: true);
            }
        }
        
        private void SetLDPlayerPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = ManualLDPlayerPathTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(path))
                {
                    ShowStatus("Please enter or browse for a path first.", isError: true);
                    return;
                }
                
                if (!Directory.Exists(path))
                {
                    ShowStatus($"Directory does not exist: {path}", isError: true);
                    return;
                }
                
                // Test if the path contains required LDPlayer executables
                var ldConsole = Path.Combine(path, "ldconsole.exe");
                var dnConsole = Path.Combine(path, "dnconsole.exe");
                
                if (!File.Exists(ldConsole) || !File.Exists(dnConsole))
                {
                    ShowStatus($"Invalid LDPlayer directory. Missing required executables (ldconsole.exe or dnconsole.exe).", isError: true);
                    return;
                }
                
                // Set the manual path
                LDPlayerHelper.SetManualInstallPath(path);
                
                // Update the display
                UpdateCurrentLDPlayerPathDisplay();
                
                // Clear the manual input
                ManualLDPlayerPathTextBox.Text = "";
                
                ShowStatus($"✅ LDPlayer path set successfully: {path}", isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error setting LDPlayer path: {ex.Message}", isError: true);
            }
        }
        
        private void TestLDPlayerPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = string.IsNullOrEmpty(ManualLDPlayerPathTextBox.Text?.Trim()) 
                    ? null 
                    : ManualLDPlayerPathTextBox.Text.Trim();
                
                if (!string.IsNullOrEmpty(path))
                {
                    // Test the manually entered path
                    if (!Directory.Exists(path))
                    {
                        ShowStatus($"❌ Directory does not exist: {path}", isError: true);
                        return;
                    }
                    
                    var ldConsole = Path.Combine(path, "ldconsole.exe");
                    var dnConsole = Path.Combine(path, "dnconsole.exe");
                    
                    if (!File.Exists(ldConsole) || !File.Exists(dnConsole))
                    {
                        ShowStatus($"❌ Invalid LDPlayer directory. Missing required executables.", isError: true);
                        return;
                    }
                    
                    ShowStatus($"✅ Path validation successful! Found valid LDPlayer installation.", isError: false);
                }
                else
                {
                    // Test current auto-detected path
                    var ldPlayerPath = LDPlayerHelper.GetLDPlayerConsolePath();
                    var dnPlayerPath = LDPlayerHelper.GetDNPlayerConsolePath();
                    var adbPath = LDPlayerHelper.GetADBPath();
                    
                    ShowStatus($"✅ Auto-detection successful!\n" +
                              $"LDConsole: {File.Exists(ldPlayerPath)}\n" +
                              $"DNConsole: {File.Exists(dnPlayerPath)}\n" +
                              $"ADB: {File.Exists(adbPath)}", isError: false);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"❌ Test failed: {ex.Message}", isError: true);
            }
        }
        
        private void ResetLDPlayerPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Clear the path cache to force re-detection
                LDPlayerHelper.ClearPathCache();
                
                // Clear the manual input
                ManualLDPlayerPathTextBox.Text = "";
                
                // Update the display with fresh auto-detection
                UpdateCurrentLDPlayerPathDisplay();
                
                ShowStatus("✅ LDPlayer path reset. Auto-detection will be used.", isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error resetting LDPlayer path: {ex.Message}", isError: true);
            }
        }
        
        private void ShowStatus(string message, bool isError)
        {
            LDPlayerPathStatusText.Text = message;
            LDPlayerPathStatusText.Foreground = isError 
                ? new SolidColorBrush(WPFColor.FromRgb(244, 67, 54)) // Red
                : new SolidColorBrush(WPFColor.FromRgb(76, 175, 80)); // Green
            
            LDPlayerPathStatusBorder.Visibility = Visibility.Visible;
            
            // Auto-hide status after 5 seconds
            var timer = new System.Threading.Timer(_ =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    LDPlayerPathStatusBorder.Visibility = Visibility.Collapsed;
                });
            }, null, 5000, System.Threading.Timeout.Infinite);
        }
        
        private string FormatLogMessage(string logMessage)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Parse the log format: |LEVEL|CATEGORY|TIMESTAMP|MESSAGE
                var parts = logMessage.Split('|');
                if (parts.Length >= 5 && string.IsNullOrEmpty(parts[0])) // First part should be empty due to leading |
                {
                    var level = parts[1];
                    var category = parts[2];
                    var timestamp = parts[3];
                    var message = string.Join("|", parts.Skip(4));

                    // Create a formatted string with visual indicators
                    var levelIndicator = level switch
                    {
                        "ERROR" => "❌",
                        "WARNING" => "⚠️",
                        "INFO" => "ℹ️",
                        _ => "•"
                    };

                    var categoryPart = category != "General" ? $"[{category}] " : "";
                    return $"[{timestamp}] {levelIndicator} {categoryPart}{message}";
                }
                // Handle USER notifications format: |USER|NotificationType|timestamp|message
                else if (parts.Length >= 5 && parts[1] == "USER")
                {
                    var notificationType = parts[2];
                    var timestamp = parts[3];
                    var message = string.Join("|", parts.Skip(4));
                    
                    var levelIndicator = notificationType switch
                    {
                        "Error" => "❌",
                        "Warning" => "⚠️",
                        "Success" => "✅",
                        "Info" => "ℹ️",
                        "Progress" => "⏳",
                        _ => "•"
                    };
                    
                    return $"[{timestamp}] {levelIndicator} {message}";
                }
                else
                {
                    // Fallback for non-formatted messages - add timestamp
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    return $"[{timestamp}] • {logMessage}";
                }
            }
            catch
            {
                // Fallback on any error
                return logMessage;
            }
            finally
            {
                stopwatch.Stop();
                // Log performance warning if log formatting takes more than 10ms
                if (stopwatch.ElapsedMilliseconds > 10)
                {
                    Debug.WriteLine($"UI Performance Warning: Log formatting took {stopwatch.ElapsedMilliseconds}ms");
                }
            }
        }


        // English Language Banner Methods
        private void InitializeLanguageBannerVisibility()
        {
            try
            {
                // Check if user has previously dismissed the language banner
                var hideLanguageBanner = GetUserSetting("HideLanguageBanner", "false");
                if (hideLanguageBanner.ToLower() == "true")
                {
                    EnglishLanguageBanner.Visibility = Visibility.Collapsed;
                }
                else
                {
                    EnglishLanguageBanner.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing language banner visibility: {ex.Message}");
                // Default to showing the banner if there's any error
                EnglishLanguageBanner.Visibility = Visibility.Visible;
            }
        }

        private void CloseLanguageBanner_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Hide the banner
                EnglishLanguageBanner.Visibility = Visibility.Collapsed;
                
                // If "Don't show again" is checked, save the setting
                if (DontShowLanguageBannerCheckBox.IsChecked == true)
                {
                    SaveUserSetting("HideLanguageBanner", "true");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing language banner: {ex.Message}");
            }
        }

        private void DontShowLanguageBanner_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save the "don't show again" preference immediately when checked
                if (DontShowLanguageBannerCheckBox.IsChecked == true)
                {
                    SaveUserSetting("HideLanguageBanner", "true");
                }
                else
                {
                    SaveUserSetting("HideLanguageBanner", "false");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving language banner preference: {ex.Message}");
            }
        }

        private string GetUserSetting(string key, string defaultValue)
        {
            try
            {
                // Use a simple file-based approach for storing user preferences
                var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                                                "KingshotAuto", "user_settings.txt");
                
                if (!File.Exists(settingsPath))
                    return defaultValue;

                var lines = File.ReadAllLines(settingsPath);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Trim() == key)
                    {
                        return parts[1].Trim();
                    }
                }
                
                return defaultValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading user setting '{key}': {ex.Message}");
                return defaultValue;
            }
        }

        private void SaveUserSetting(string key, string value)
        {
            try
            {
                var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KingshotAuto");
                var settingsPath = Path.Combine(settingsDir, "user_settings.txt");
                
                // Create directory if it doesn't exist
                if (!Directory.Exists(settingsDir))
                {
                    Directory.CreateDirectory(settingsDir);
                }

                // Read existing settings
                var settings = new Dictionary<string, string>();
                if (File.Exists(settingsPath))
                {
                    var lines = File.ReadAllLines(settingsPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            settings[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }

                // Update or add the setting
                settings[key] = value;

                // Write all settings back
                var settingsLines = settings.Select(kvp => $"{kvp.Key}={kvp.Value}").ToArray();
                File.WriteAllLines(settingsPath, settingsLines);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving user setting '{key}': {ex.Message}");
            }
        }
    }
} 