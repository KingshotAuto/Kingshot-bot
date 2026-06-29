using System;
using Bot.Core.Logging;

namespace Bot.Core.Services
{
    /// <summary>
    /// Implementation of user notification service that formats user-friendly messages
    /// </summary>
    public class UserNotificationService : IUserNotificationService
    {
        private readonly LogService _logger;
        private readonly GUILogService _guiLogger;

        public UserNotificationService(LogService logger, GUILogService guiLogger)
        {
            _logger = logger;
            _guiLogger = guiLogger;
        }

        public void ShowStatus(string message, NotificationType type = NotificationType.Info)
        {
            var formattedMessage = FormatMessage(message, type);
            
            // Send to GUI for users
            _guiLogger.SendUserNotification(formattedMessage);
            
            // Log to file for developers
            LogToFile(message, type);
        }

        public void ShowProgress(string operation, int percentage, string? details = null)
        {
            var message = details != null 
                ? $"{operation}: {percentage}% - {details}"
                : $"{operation}: {percentage}%";
                
            var formattedMessage = FormatMessage(message, NotificationType.Progress);
            
            // Send to GUI for users
            _guiLogger.SendUserNotification(formattedMessage);
            
            // Log to file for developers
            _logger.LogInfo($"Progress: {message}", category: LogCategories.UserAction);
        }

        public void ShowError(string message, bool isRecoverable = false, string? troubleshootingHint = null)
        {
            var fullMessage = message;
            if (troubleshootingHint != null)
            {
                fullMessage += $" {troubleshootingHint}";
            }
            if (isRecoverable)
            {
                fullMessage += " The operation will be retried.";
            }
            
            var formattedMessage = FormatMessage(fullMessage, NotificationType.Error);
            
            // Send to GUI for users
            _guiLogger.SendUserNotification(formattedMessage);
            
            // Log to file for developers
            _logger.LogError($"User Error: {message}", category: LogCategories.UserAction);
        }

        public void ShowSuccess(string message)
        {
            var formattedMessage = FormatMessage(message, NotificationType.Success);
            
            // Send to GUI for users
            _guiLogger.SendUserNotification(formattedMessage);
            
            // Log to file for developers
            _logger.LogInfo($"User Success: {message}", category: LogCategories.UserAction);
        }

        public void ShowWarning(string message, string? suggestion = null)
        {
            var fullMessage = suggestion != null ? $"{message} {suggestion}" : message;
            var formattedMessage = FormatMessage(fullMessage, NotificationType.Warning);
            
            // Send to GUI for users
            _guiLogger.SendUserNotification(formattedMessage);
            
            // Log to file for developers
            _logger.LogWarning($"User Warning: {message}", category: LogCategories.UserAction);
        }

        public void Clear()
        {
            _guiLogger.SendUserNotification("__CLEAR_NOTIFICATIONS__");
        }

        private string FormatMessage(string message, NotificationType type)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var icon = GetIconForType(type);
            return $"|USER|{type}|{timestamp}|{icon} {message}";
        }

        private string GetIconForType(NotificationType type)
        {
            return type switch
            {
                NotificationType.Success => "✅",
                NotificationType.Error => "❌",
                NotificationType.Warning => "⚠️",
                NotificationType.Progress => "⏳",
                NotificationType.Info => "ℹ️",
                _ => "•"
            };
        }

        private void LogToFile(string message, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.Error:
                    _logger.LogError($"User Notification: {message}", category: LogCategories.UserAction);
                    break;
                case NotificationType.Warning:
                    _logger.LogWarning($"User Notification: {message}", category: LogCategories.UserAction);
                    break;
                default:
                    _logger.LogInfo($"User Notification: {message}", category: LogCategories.UserAction);
                    break;
            }
        }
    }
}