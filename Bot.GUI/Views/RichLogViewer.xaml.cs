using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.GUI.Views
{
    public partial class RichLogViewer : System.Windows.Controls.UserControl
    {
        private readonly Paragraph _logParagraph;
        private readonly Queue<string> _pendingLogs = new Queue<string>();
        private readonly DispatcherTimer _updateTimer;
        private readonly object _lockObject = new object();
        private const int MAX_LINES = 1000;
        private int _currentLineCount = 0;

        // Color scheme for different log levels
        private readonly Dictionary<string, LogStyle> _logStyles = new Dictionary<string, LogStyle>
        {
            ["INFO"] = new LogStyle { Color = Colors.LightBlue, FontSize = 12, FontWeight = FontWeights.Normal },
            ["WARNING"] = new LogStyle { Color = Colors.Orange, FontSize = 13, FontWeight = FontWeights.SemiBold },
            ["ERROR"] = new LogStyle { Color = Colors.Red, FontSize = 14, FontWeight = FontWeights.Bold },
            ["SUCCESS"] = new LogStyle { Color = Colors.LightGreen, FontSize = 13, FontWeight = FontWeights.SemiBold },
            ["DEBUG"] = new LogStyle { Color = Colors.Gray, FontSize = 11, FontWeight = FontWeights.Normal }
        };

        // Category-based styling
        private readonly Dictionary<string, LogStyle> _categoryStyles = new Dictionary<string, LogStyle>
        {
            ["TaskExecution"] = new LogStyle { Color = Colors.Cyan, FontSize = 13, FontWeight = FontWeights.SemiBold },
            ["ADB"] = new LogStyle { Color = Colors.DarkGray, FontSize = 11, FontWeight = FontWeights.Normal },
            ["Performance"] = new LogStyle { Color = Colors.Yellow, FontSize = 12, FontWeight = FontWeights.Normal }
        };

        public RichLogViewer()
        {
            InitializeComponent();
            
            // Get the paragraph from the FlowDocument
            _logParagraph = (Paragraph)LogRichTextBox.Document.Blocks.FirstBlock;
            
            // Set up a timer to batch log updates for better performance
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _updateTimer.Tick += UpdateLogs;
            _updateTimer.Start();
        }

        public void AppendLog(string logMessage)
        {
            lock (_lockObject)
            {
                // Debug: Check what we're receiving
                System.Diagnostics.Debug.WriteLine($"RichLogViewer.AppendLog received: [{logMessage}]");
                _pendingLogs.Enqueue(logMessage);
            }
        }

        public void Clear()
        {
            Dispatcher.BeginInvoke(() =>
            {
                _logParagraph.Inlines.Clear();
                _currentLineCount = 0;
            });
        }

        private void UpdateLogs(object? sender, EventArgs e)
        {
            var logsToProcess = new List<string>();
            
            lock (_lockObject)
            {
                while (_pendingLogs.Count > 0)
                {
                    logsToProcess.Add(_pendingLogs.Dequeue());
                }
            }

            if (logsToProcess.Count == 0) return;

            // Process logs on background thread, then update UI
            _ = Task.Run(() =>
            {
                var processedLogs = new List<(bool isClear, object? logEntry)>();
                
                foreach (var log in logsToProcess)
                {
                    if (log == "__CLEAR_LOGS__")
                    {
                        processedLogs.Add((true, null));
                        continue;
                    }

                    // Pre-process log entry on background thread
                    var logEntry = PreProcessLogEntry(log);
                    processedLogs.Add((false, logEntry));
                }

                // Update UI on UI thread using BeginInvoke (non-blocking)
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (isClear, logEntry) in processedLogs)
                    {
                        if (isClear)
                        {
                            Clear();
                            continue;
                        }

                        if (logEntry != null)
                        {
                            ProcessLogEntryOnUI(logEntry);
                        }
                    }

                    // Auto-scroll to bottom
                    LogScrollViewer.ScrollToEnd();
                });
            });
        }

        private class ProcessedLogEntry
        {
            public string? Level { get; set; }
            public string? Category { get; set; }
            public string? Timestamp { get; set; }
            public string? Message { get; set; }
            public bool IsSimple { get; set; }
        }

        private ProcessedLogEntry PreProcessLogEntry(string logMessage)
        {
            try
            {
                // Parse the log format: |LEVEL|CATEGORY|TIMESTAMP|MESSAGE
                var parts = logMessage.Split('|');
                if (parts.Length >= 5 && string.IsNullOrEmpty(parts[0])) // First part should be empty due to leading |
                {
                    return new ProcessedLogEntry
                    {
                        Level = parts[1],
                        Category = parts[2],
                        Timestamp = parts[3],
                        Message = string.Join("|", parts.Skip(4)),
                        IsSimple = false
                    };
                }
                else
                {
                    // Fallback for improperly formatted logs
                    return new ProcessedLogEntry
                    {
                        Message = logMessage,
                        IsSimple = true
                    };
                }
            }
            catch (Exception ex)
            {
                // If parsing fails, just add as simple text
                return new ProcessedLogEntry
                {
                    Message = $"[LOG ERROR] {logMessage} - {ex.Message}",
                    IsSimple = true
                };
            }
        }

        private void ProcessLogEntryOnUI(object logEntryObj)
        {
            if (logEntryObj is ProcessedLogEntry logEntry)
            {
                if (logEntry.IsSimple)
                {
                    AddSimpleLog(logEntry.Message ?? "");
                }
                else
                {
                    AddFormattedLog(logEntry.Level ?? "", logEntry.Category ?? "", 
                                  logEntry.Timestamp ?? "", logEntry.Message ?? "");
                }

                // Maintain max line count
                _currentLineCount++;
                if (_currentLineCount > MAX_LINES)
                {
                    TrimOldLogs();
                }
            }
        }

        private void ProcessLogEntry(string logMessage)
        {
            try
            {
                // Parse the log format: |LEVEL|CATEGORY|TIMESTAMP|MESSAGE
                var parts = logMessage.Split('|');
                if (parts.Length >= 5 && string.IsNullOrEmpty(parts[0])) // First part should be empty due to leading |
                {
                    var level = parts[1];
                    var category = parts[2];
                    var timestamp = parts[3];
                    var message = string.Join("|", parts.Skip(4)); // Handle messages with | in them

                    AddFormattedLog(level, category, timestamp, message);
                }
                else
                {
                    // Fallback for improperly formatted logs - likely direct AppendLog calls
                    AddSimpleLog(logMessage);
                }

                // Maintain max line count
                _currentLineCount++;
                if (_currentLineCount > MAX_LINES)
                {
                    TrimOldLogs();
                }
            }
            catch (Exception ex)
            {
                // If parsing fails, just add as simple text
                AddSimpleLog($"[LOG ERROR] {logMessage} - {ex.Message}");
            }
        }

        private void AddFormattedLog(string level, string category, string timestamp, string message)
        {
            // Timestamp
            var timestampRun = new Run($"[{timestamp}] ")
            {
                Foreground = new SolidColorBrush(Colors.DarkGray),
                FontSize = 11
            };
            _logParagraph.Inlines.Add(timestampRun);

            // Level indicator
            var levelStyle = GetLogStyle(level);
            var levelRun = new Run($"[{level}] ")
            {
                Foreground = new SolidColorBrush(levelStyle.Color),
                FontSize = levelStyle.FontSize,
                FontWeight = levelStyle.FontWeight
            };
            _logParagraph.Inlines.Add(levelRun);

            // Category (if not General)
            if (category != "General")
            {
                var categoryStyle = _categoryStyles.ContainsKey(category) 
                    ? _categoryStyles[category] 
                    : new LogStyle { Color = Colors.LightGray, FontSize = 11, FontWeight = FontWeights.Normal };
                
                var categoryRun = new Run($"[{category}] ")
                {
                    Foreground = new SolidColorBrush(categoryStyle.Color),
                    FontSize = categoryStyle.FontSize,
                    FontWeight = categoryStyle.FontWeight
                };
                _logParagraph.Inlines.Add(categoryRun);
            }

            // Message
            var messageStyle = DetermineMessageStyle(level, message);
            var messageRun = new Run(message + Environment.NewLine)
            {
                Foreground = new SolidColorBrush(messageStyle.Color),
                FontSize = messageStyle.FontSize,
                FontWeight = messageStyle.FontWeight
            };
            _logParagraph.Inlines.Add(messageRun);
        }

        private void AddSimpleLog(string message)
        {
            var run = new Run(message + Environment.NewLine)
            {
                Foreground = new SolidColorBrush(Colors.LightGray),
                FontSize = 12
            };
            _logParagraph.Inlines.Add(run);
        }

        private LogStyle GetLogStyle(string level)
        {
            return _logStyles.ContainsKey(level) 
                ? _logStyles[level] 
                : new LogStyle { Color = Colors.White, FontSize = 12, FontWeight = FontWeights.Normal };
        }

        private LogStyle DetermineMessageStyle(string level, string message)
        {
            // Check for success indicators
            if (message.Contains("✅") || message.Contains("SUCCESS") || message.Contains("completed successfully"))
            {
                return _logStyles["SUCCESS"];
            }

            // Check for error indicators
            if (message.Contains("❌") || message.Contains("FAILED") || message.Contains("Error"))
            {
                return _logStyles["ERROR"];
            }

            // Check for warning indicators
            if (message.Contains("⚠️") || message.Contains("WARNING"))
            {
                return _logStyles["WARNING"];
            }

            // Technical/debug messages
            if (IsDebugMessage(message))
            {
                return _logStyles["DEBUG"];
            }

            // Default to level style
            return GetLogStyle(level);
        }

        private bool IsDebugMessage(string message)
        {
            var debugKeywords = new[]
            {
                "template matching", "threshold", "confidence", "ADB command",
                "Screenshot", "Image matching", "Executing command", "Looking for",
                "Connecting to", "Starting screenshot", "Locator", "dimensions"
            };

            foreach (var keyword in debugKeywords)
            {
                if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private void TrimOldLogs()
        {
            // Remove the first 100 lines
            var inlines = _logParagraph.Inlines.Take(100).ToList();
            foreach (var inline in inlines)
            {
                _logParagraph.Inlines.Remove(inline);
            }
            _currentLineCount -= 100;
        }

        private class LogStyle
        {
            public System.Windows.Media.Color Color { get; set; }
            public double FontSize { get; set; }
            public FontWeight FontWeight { get; set; }
        }
    }
}