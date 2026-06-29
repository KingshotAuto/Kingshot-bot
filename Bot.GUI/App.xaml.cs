using System.Configuration;
using System.Data;
using Bot.GUI.Views;
using System.Windows;
using WPFMessageBox = System.Windows.MessageBox;
using WPFApplication = System.Windows.Application;
using Bot.Core.Logging;
using System;
using System.Reflection;
using Bot.Core.Services;
using Bot.Core.Utils;
using System.Windows.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Bot.Core.LDPlayer;
using Bot.Core.Config;
using System.Threading.Tasks;

namespace Bot.GUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WPFApplication
{
    private LogService? _logger;

    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();

    public App()
    {
        // Show a console window for log output when running a Debug build.
        #if DEBUG
        AllocConsole();
        Console.WriteLine("Application constructor called");
        #endif

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            InitializeLogger();

            // Start ADB health monitoring
            ADBConnectionManager.StartHealthMonitoring(new LogService());

            ShowMainWindow();
        }
        catch (Exception ex)
        {
            try
            {
                _logger?.LogError($"Critical startup error: {ex}");
                WPFMessageBox.Show($"A critical error occurred: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
            catch (Exception)
            {
                // If even showing the error message fails, just exit
                Shutdown();
            }
        }
    }

    private void InitializeLogger()
    {
        var logsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logsPath);
        _logger = new LogService();
        _logger.LogInfo("Logger initialized.");

        // Initialize ConfigurationManager with logger to fix dependency injection
        Bot.Core.Config.ConfigurationManager.Initialize(_logger);
        _logger.LogInfo("ConfigurationManager initialized with logger.");
    }

    private void ShowMainWindow()
    {
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var ex = (Exception)args.ExceptionObject;
        _logger?.LogError($"Unhandled exception: {ex}");
        WPFMessageBox.Show($"An unhandled error occurred: {ex.Message}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        _logger?.LogError($"Dispatcher unhandled exception: {args.Exception}");

        // Special handling for XAML parsing exceptions
        var exception = args.Exception;
        string errorDetails = $"Exception Type: {exception.GetType().FullName}\n";
        errorDetails += $"Message: {exception.Message}\n\n";

        if (exception.InnerException != null)
        {
            errorDetails += $"Inner Exception: {exception.InnerException.Message}\n\n";

            // Check for XAML line info
            if (exception.InnerException is System.Windows.Markup.XamlParseException xamlEx)
            {
                errorDetails += $"XAML Error Details:\n";
                errorDetails += $"Line: {xamlEx.LineNumber}\n";
                errorDetails += $"Position: {xamlEx.LinePosition}\n";
            }
        }

        errorDetails += $"Stack Trace:\n{exception.StackTrace}";

        // Write to file for easier debugging
        try
        {
            File.WriteAllText("xaml_error_details.txt", errorDetails);
            _logger?.LogError($"Error details written to xaml_error_details.txt");
        }
        catch { }

        WPFMessageBox.Show($"An unhandled UI error occurred:\n\n{exception.Message}\n\nDetails written to xaml_error_details.txt", "UI Error", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_logger != null)
            {
                _logger.LogInfo("Application shutting down...");
                Console.WriteLine("Application shutting down");
                _logger.Dispose();
            }

            // Stop ADB health monitoring
            ADBConnectionManager.StopHealthMonitoring();

            base.OnExit(e);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during shutdown: {ex.Message}");
        }
    }

    private void ShowErrorWindow(string errorText, LogService logger)
    {
        try
        {
            logger.LogError("Displaying error window for an unhandled exception.");
            Console.WriteLine("Displaying error window for an unhandled exception");
            var window = new Window
            {
                Title = "Unhandled Exception",
                Width = 700,
                Height = 400,
                Content = new System.Windows.Controls.TextBox
                {
                    Text = errorText,
                    IsReadOnly = true,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 13,
                    Margin = new Thickness(10),
                    AcceptsReturn = true
                }
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error showing error window: {ex.Message}");
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var exceptionLogger = new LogService();
            exceptionLogger.LogError("!!!!!!!!!!!!!!!!! UNHANDLED UI EXCEPTION !!!!!!!!!!!!!!!!!");
            exceptionLogger.LogError($"EXCEPTION TYPE: {e.Exception?.GetType().FullName}");
            exceptionLogger.LogError($"EXCEPTION MESSAGE: {e.Exception?.Message}");
            exceptionLogger.LogError($"FULL EXCEPTION DETAILS:\n{e.Exception}");
            exceptionLogger.LogError("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");

            Console.WriteLine($"Unhandled UI Exception: {e.Exception?.Message}");
            ShowErrorWindow($"Unhandled UI Exception:\n{e.Exception}", exceptionLogger);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling dispatcher exception: {ex.Message}");
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var exceptionLogger = new LogService();
            var exception = e.ExceptionObject as Exception;
            exceptionLogger.LogError("!!!!!!!!!!!!!!!!! UNHANDLED DOMAIN EXCEPTION !!!!!!!!!!!!!!!!!");
            exceptionLogger.LogError($"EXCEPTION TYPE: {exception?.GetType().FullName}");
            exceptionLogger.LogError($"EXCEPTION MESSAGE: {exception?.Message}");
            exceptionLogger.LogError($"IS TERMINATING: {e.IsTerminating}");
            exceptionLogger.LogError($"FULL EXCEPTION DETAILS:\n{e.ExceptionObject}");
            exceptionLogger.LogError("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");

            Console.WriteLine($"Unhandled Domain Exception: {exception?.Message}");
            if (exception != null)
            {
                ShowErrorWindow($"Unhandled Domain Exception:\n{exception}", exceptionLogger);
            }
            else
            {
                ShowErrorWindow($"Unhandled Domain Exception (Non-CLS compliant):\n{e.ExceptionObject}", exceptionLogger);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling domain exception: {ex.Message}");
        }
    }
}
