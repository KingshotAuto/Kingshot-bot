using System;

namespace Bot.Core.Services
{
    /// <summary>
    /// Represents the type of user notification
    /// </summary>
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error,
        Progress
    }

    /// <summary>
    /// Service for displaying user-friendly notifications separate from technical logs
    /// </summary>
    public interface IUserNotificationService
    {
        /// <summary>
        /// Show a status message to the user
        /// </summary>
        void ShowStatus(string message, NotificationType type = NotificationType.Info);
        
        /// <summary>
        /// Show progress for an operation
        /// </summary>
        void ShowProgress(string operation, int percentage, string? details = null);
        
        /// <summary>
        /// Show an error message with context
        /// </summary>
        void ShowError(string message, bool isRecoverable = false, string? troubleshootingHint = null);
        
        /// <summary>
        /// Show a success message
        /// </summary>
        void ShowSuccess(string message);
        
        /// <summary>
        /// Show a warning message
        /// </summary>
        void ShowWarning(string message, string? suggestion = null);
        
        /// <summary>
        /// Clear all notifications
        /// </summary>
        void Clear();
    }
}