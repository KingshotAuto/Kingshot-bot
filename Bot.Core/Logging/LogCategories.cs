namespace Bot.Core.Logging
{
    /// <summary>
    /// Standardized log categories for consistent logging across the application
    /// </summary>
    public static class LogCategories
    {
        /// <summary>
        /// System-level operations (startup, shutdown, configuration)
        /// </summary>
        public const string System = "System";
        
        /// <summary>
        /// Task execution, progress, and completion
        /// </summary>
        public const string TaskExecution = "Task";
        
        /// <summary>
        /// Android Debug Bridge operations and device communication
        /// </summary>
        public const string ADB = "ADB";
        
        /// <summary>
        /// Image detection, template matching, and computer vision
        /// </summary>
        public const string ImageDetection = "Image";
        
        /// <summary>
        /// Configuration loading, validation, and changes
        /// </summary>
        public const string Configuration = "Config";
        
        /// <summary>
        /// Instance management (start, stop, reboot)
        /// </summary>
        public const string Instance = "Instance";
        
        /// <summary>
        /// License validation and activation
        /// </summary>
        public const string License = "License";
        
        /// <summary>
        /// Performance metrics and timing
        /// </summary>
        public const string Performance = "Perf";
        
        /// <summary>
        /// User actions and interactions
        /// </summary>
        public const string UserAction = "User";
        
        /// <summary>
        /// Bot automation cycles and orchestration
        /// </summary>
        public const string Automation = "Auto";
        
        /// <summary>
        /// Network operations and connectivity
        /// </summary>
        public const string Network = "Network";
        
        /// <summary>
        /// Security operations and validation
        /// </summary>
        public const string Security = "Security";
    }
}