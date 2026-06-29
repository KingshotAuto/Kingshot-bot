using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bot.GUI.Views
{
    public partial class ColoredLogViewer : System.Windows.Controls.UserControl
    {
        private int _lineCount = 0;
        private const int MAX_LINES = 1000;

        public ColoredLogViewer()
        {
            InitializeComponent();
        }

        public void AppendLog(string logMessage)
        {
            if (Dispatcher.CheckAccess())
            {
                ProcessLogMessage(logMessage);
            }
            else
            {
                Dispatcher.BeginInvoke(() => ProcessLogMessage(logMessage));
            }
        }

        public void Clear()
        {
            if (Dispatcher.CheckAccess())
            {
                LogParagraph.Inlines.Clear();
                _lineCount = 0;
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    LogParagraph.Inlines.Clear();
                    _lineCount = 0;
                });
            }
        }

        private void ProcessLogMessage(string logMessage)
        {
            try
            {
                if (logMessage == "__CLEAR_LOGS__")
                {
                    Clear();
                    return;
                }

                // Parse the log format: |LEVEL|CATEGORY|TIMESTAMP|MESSAGE
                var parts = logMessage.Split('|');
                if (parts.Length >= 5 && string.IsNullOrEmpty(parts[0])) // First part should be empty due to leading |
                {
                    var level = parts[1];
                    var category = parts[2];
                    var timestamp = parts[3];
                    var message = string.Join("|", parts.Skip(4));

                    AddFormattedLog(level, category, timestamp, message);
                }
                else
                {
                    // Fallback for non-formatted messages
                    AddSimpleLog(logMessage);
                }

                // Maintain line count
                _lineCount++;
                if (_lineCount > MAX_LINES)
                {
                    TrimOldLines();
                }

                // Auto-scroll to bottom
                LogScrollViewer.ScrollToEnd();
            }
            catch (Exception ex)
            {
                // Fallback on any error
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
            LogParagraph.Inlines.Add(timestampRun);

            // Level with color
            var levelColor = GetLevelColor(level);
            var levelRun = new Run($"[{level}] ")
            {
                Foreground = new SolidColorBrush(levelColor),
                FontWeight = level == "ERROR" ? FontWeights.Bold : FontWeights.Normal,
                FontSize = level == "ERROR" ? 13 : 12
            };
            LogParagraph.Inlines.Add(levelRun);

            // Category (if not General)
            if (category != "General")
            {
                var categoryRun = new Run($"[{category}] ")
                {
                    Foreground = new SolidColorBrush(Colors.Cyan),
                    FontSize = 11
                };
                LogParagraph.Inlines.Add(categoryRun);
            }

            // Message with appropriate color
            var messageColor = GetMessageColor(level, message);
            var messageRun = new Run(message + Environment.NewLine)
            {
                Foreground = new SolidColorBrush(messageColor),
                FontSize = 12
            };
            LogParagraph.Inlines.Add(messageRun);
        }

        private void AddSimpleLog(string message)
        {
            var run = new Run(message + Environment.NewLine)
            {
                Foreground = new SolidColorBrush(Colors.LightGray),
                FontSize = 12
            };
            LogParagraph.Inlines.Add(run);
        }

        private System.Windows.Media.Color GetLevelColor(string level)
        {
            return level switch
            {
                "ERROR" => Colors.Red,
                "WARNING" => Colors.Orange,
                "INFO" => Colors.LightBlue,
                _ => Colors.White
            };
        }

        private System.Windows.Media.Color GetMessageColor(string level, string message)
        {
            // Success indicators
            if (message.Contains("✅") || message.Contains("SUCCESS") || message.Contains("completed successfully"))
                return Colors.LightGreen;

            // Error indicators
            if (message.Contains("❌") || message.Contains("FAILED") || message.Contains("Error"))
                return Colors.Red;

            // Warning indicators
            if (message.Contains("⚠️") || message.Contains("WARNING"))
                return Colors.Orange;

            // Use level color as fallback
            return GetLevelColor(level);
        }

        private void TrimOldLines()
        {
            // Remove the first 100 lines
            var inlinesToRemove = LogParagraph.Inlines.Take(100).ToList();
            foreach (var inline in inlinesToRemove)
            {
                LogParagraph.Inlines.Remove(inline);
            }
            _lineCount -= 100;
        }
    }
}