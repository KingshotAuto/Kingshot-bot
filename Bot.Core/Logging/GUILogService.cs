using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Bot.Core.Logging
{
    public class GUILogService : LogService
    {
        private readonly Action<string>? _guiLogCallback;
        
        // Default constructor for global logs
        public GUILogService(Action<string>? guiLogCallback = null) : base()
        {
            _guiLogCallback = guiLogCallback;
        }
        
        // Constructor for per-instance logs
        public GUILogService(int instanceNumber, Action<string>? guiLogCallback = null) : base(instanceNumber)
        {
            _guiLogCallback = guiLogCallback;
        }
        
        // Constructor for custom log file path
        public GUILogService(string customLogPath, Action<string>? guiLogCallback = null) : base(customLogPath)
        {
            _guiLogCallback = guiLogCallback;
        }

        public new void Dispose()
        {
            base.Dispose();
        }
        
        public override void LogInfo(string message, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            // Call base implementation for console and file logging (developer logs)
            base.LogInfo(message, context, category, method);
            
            // Developer logs don't appear in GUI - only user notifications do
        }
        
        public override void LogWarning(string message, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            // Call base implementation for console and file logging (developer logs)
            base.LogWarning(message, context, category, method);
            
            // Developer logs don't appear in GUI - only user notifications do
        }
        
        public override void LogError(string message, Exception? exception = null, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            // Call base implementation for console and file logging (developer logs)
            base.LogError(message, exception, context, category, method);
            
            // Developer logs don't appear in GUI - only user notifications do
        }
        
        /// <summary>
        /// Send a user notification to the GUI (separate from developer logs)
        /// </summary>
        public void SendUserNotification(string message)
        {
            if (_guiLogCallback != null)
            {
                _guiLogCallback(message);
            }
        }
        
        /// <summary>
        /// Purges logs and notifies GUI to clear its display
        /// </summary>
        public override void PurgeLogs()
        {
            // Call base implementation to purge the log file
            base.PurgeLogs();
            
            // Notify GUI that logs have been purged
            if (_guiLogCallback != null)
            {
                _guiLogCallback("🧹 LOGS PURGED - Fresh session started");
                _guiLogCallback("=====================================");
            }
        }
        
        /// <summary>
        /// Formats log messages for GUI display with metadata
        /// </summary>
        private string FormatForGUI(string message, string level, string? category = null)
        {
            // Remove existing prefixes since we'll add our own formatting
            var cleanMessage = message;
            if (cleanMessage.StartsWith("[INFO] "))
                cleanMessage = cleanMessage.Substring(7);
            if (cleanMessage.StartsWith("[ERROR] "))
                cleanMessage = cleanMessage.Substring(8);
            if (cleanMessage.StartsWith("[WARNING] "))
                cleanMessage = cleanMessage.Substring(10);
            
            // Filter out verbose template matching details
            if (ShouldFilterVerboseMessage(cleanMessage))
                return null; // Don't send to GUI
            
            // Add timestamp
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            
            // Create formatted message with metadata for rich text parsing
            // Format: |LEVEL|CATEGORY|TIMESTAMP|MESSAGE
            var formattedCategory = string.IsNullOrEmpty(category) ? "General" : category;
            return $"|{level}|{formattedCategory}|{timestamp}|{cleanMessage}";
        }
        
        /// <summary>
        /// Determines if a message should be filtered out from GUI display due to being too verbose
        /// </summary>
        private bool ShouldFilterVerboseMessage(string message)
        {
            var verboseKeywords = new[]
            {
                "📊 Scale",
                "📏 Template size:",
                "Confidence: 0.000",
                "Scale 0.8",
                "Scale 0.9",
                "Scale 1.0",
                "Scale 1.1",
                "Scale 1.2",
                "template matching",
                "threshold",
                "Final result",
                "Starting template",
                "Looking for templates",
                "Template details"
            };
            
            foreach (var keyword in verboseKeywords)
            {
                if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }
    }
} 