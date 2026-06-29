using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WPFApplication = System.Windows.Application;

namespace Bot.GUI.Utils
{
    /// <summary>
    /// Monitors UI thread performance and detects potential blocking operations
    /// </summary>
    public static class UIThreadMonitor
    {
        private static readonly Stopwatch _uiThreadStopwatch = new();
        private static DispatcherTimer? _monitorTimer;
        private static readonly object _lockObject = new();
        private static bool _isMonitoring = false;
        
        public static void StartMonitoring()
        {
            lock (_lockObject)
            {
                if (_isMonitoring) return;
                
                _uiThreadStopwatch.Start();
                _monitorTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                
                _monitorTimer.Tick += (s, e) =>
                {
                    _uiThreadStopwatch.Restart();
                    
                    // Check if UI thread is responsive
                    WPFApplication.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        var elapsed = _uiThreadStopwatch.ElapsedMilliseconds;
                        if (elapsed > 50) // More than 50ms delay indicates UI thread blocking
                        {
                            Debug.WriteLine($"UI Thread Performance Warning: {elapsed}ms delay detected");
                            Debug.WriteLine($"Current Thread: {Thread.CurrentThread.ManagedThreadId}");
                            Debug.WriteLine($"Is UI Thread: {Thread.CurrentThread.ManagedThreadId == WPFApplication.Current.Dispatcher.Thread.ManagedThreadId}");
                        }
                    }));
                };
                
                _monitorTimer.Start();
                _isMonitoring = true;
                
                Debug.WriteLine("UI Thread Monitor started");
            }
        }
        
        public static void StopMonitoring()
        {
            lock (_lockObject)
            {
                if (!_isMonitoring) return;
                
                _monitorTimer?.Stop();
                _monitorTimer = null;
                _uiThreadStopwatch.Stop();
                _isMonitoring = false;
                
                Debug.WriteLine("UI Thread Monitor stopped");
            }
        }
        
        public static void LogOperation(string operationName, Action operation)
        {
            var stopwatch = Stopwatch.StartNew();
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var isUIThread = threadId == WPFApplication.Current.Dispatcher.Thread.ManagedThreadId;
            
            try
            {
                operation();
            }
            finally
            {
                stopwatch.Stop();
                
                if (isUIThread && stopwatch.ElapsedMilliseconds > 16) // More than 16ms on UI thread affects 60fps
                {
                    Debug.WriteLine($"UI Thread Operation Warning: '{operationName}' took {stopwatch.ElapsedMilliseconds}ms on UI thread {threadId}");
                }
                else if (stopwatch.ElapsedMilliseconds > 100) // Long operations regardless of thread
                {
                    Debug.WriteLine($"Long Operation: '{operationName}' took {stopwatch.ElapsedMilliseconds}ms on thread {threadId} (UI: {isUIThread})");
                }
            }
        }
    }
}