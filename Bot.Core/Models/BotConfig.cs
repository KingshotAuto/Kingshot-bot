using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bot.Core.Logging;
using System.Linq;

namespace Bot.Core.Models
{
    public class BotConfig
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        
        [JsonPropertyName("totalRunningInstances")]
        public int TotalRunningInstances { get; set; } = 2; // Changed default to safer value

        [JsonPropertyName("maxConcurrentInstances")]
        public int MaxConcurrentInstances { get; set; } = 3; // Hard limit for safety

        [JsonPropertyName("instanceStartupDelayMs")]
        public int InstanceStartupDelayMs { get; set; } = 3000; // Progressive delay between instances

        [JsonPropertyName("enableResourceThrottling")]
        public bool EnableResourceThrottling { get; set; } = true; // Enable throttling by default

        [JsonPropertyName("accounts")]
        public List<AccountSettings> Accounts { get; set; }

        [JsonPropertyName("cycleManagement")]
        public CycleManagementConfig CycleManagement { get; set; } = new();

        [JsonPropertyName("performance")]
        public PerformanceConfig Performance { get; set; } = new();


        // Constructor to create default configuration with sample accounts
        public BotConfig()
        {
            Accounts = new List<AccountSettings>();
            // Don't create default accounts here - let the app decide when to add them
        }

        private void CreateDefaultAccounts()
        {
            // Create 3 sample accounts with common task configurations
            var defaultTasks = new[]
            {
                TaskType.Farming,
                TaskType.AutoHunt,
                TaskType.AutoHeal,
                TaskType.ClaimMail,
                TaskType.CollectVip
            };

            var sampleAccounts = new[]
            {
                new { Name = "Account 1", Instance = 0 },
                new { Name = "Account 2", Instance = 1 },
                new { Name = "Account 3", Instance = 2 }
            };

            foreach (var sample in sampleAccounts)
            {
                var account = new AccountSettings
                {
                    AccountName = sample.Name,
                    InstanceNumber = sample.Instance,
                    IsEnabled = true
                };

                // Add default enabled tasks
                foreach (var task in defaultTasks)
                {
                    account.EnabledTasks.Add(task);
                }

                // Add basic farming targets
                account.FarmingTargets.Add(new FarmingTarget
                {
                    ResourceType = ResourceType.Bread,
                    Level = 6
                });
                
                account.FarmingTargets.Add(new FarmingTarget
                {
                    ResourceType = ResourceType.Wood,
                    Level = 6
                });

                Accounts.Add(account);
            }
        }


        // General settings
        public bool AutoRestart { get; set; } = true;
        public string LDPlayerPath { get; set; } = @"C:\LDPlayer\LDPlayer9";
        public string DNPlayerPath { get; set; } = @"C:\LDPlayer\LDPlayer9";
        public bool UseDNPlayer { get; set; } = false;

        // Logging settings
        public string LogLevel { get; set; } = "Info";
        public bool SaveDebugVisuals { get; set; } = true;
        public string ScreenshotPath { get; set; } = "logs/screenshots";
        
        // Retry settings
        public int MaxRetries { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;

        public Dictionary<string, string> CustomSettings { get; set; } = new();



        public static BotConfig LoadFromFile(string filePath, LogService logger)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    logger.LogWarning($"Config file not found at {filePath}, creating default config");
                    var defaultConfig = new BotConfig();
                    defaultConfig.SaveToFile(filePath, logger);
                    return defaultConfig;
                }

                string json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<BotConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (config == null)
                {
                    logger.LogError("Failed to deserialize config file");
                    return new BotConfig();
                }

                // Ensure Accounts list is initialized
                if (config.Accounts == null)
                {
                    config.Accounts = new List<AccountSettings>();
                }


                return config;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error loading config: {ex.Message}");
                return new BotConfig();
            }
        }

        public void SaveToFile(string filePath, LogService logger)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? ".";
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(this, _jsonOptions);
                File.WriteAllText(filePath, json);
                logger.LogInfo($"Config saved to {filePath}");
            }
            catch (Exception ex)
            {
                logger.LogError($"Error saving config: {ex.Message}");
            }
        }

        public void Validate(LogService logger)
        {
            if (Accounts == null || Accounts.Count == 0)
            {
                logger.LogWarning("No accounts configured");
                return;
            }

            // Validate and enforce safety limits for concurrent instances
            if (TotalRunningInstances < 1)
            {
                logger.LogWarning("Total running instances must be at least 1");
                TotalRunningInstances = 1;
            }
            else if (TotalRunningInstances > MaxConcurrentInstances)
            {
                logger.LogWarning($"Total running instances ({TotalRunningInstances}) exceeds safety limit. Capping at {MaxConcurrentInstances}");
                TotalRunningInstances = MaxConcurrentInstances;
            }

            // Validate MaxConcurrentInstances
            if (MaxConcurrentInstances < 1)
            {
                logger.LogWarning("Max concurrent instances must be at least 1");
                MaxConcurrentInstances = 1;
            }
            else if (MaxConcurrentInstances > 5)
            {
                logger.LogWarning("Max concurrent instances exceeds recommended limit of 5. Performance may be impacted.");
                MaxConcurrentInstances = 5;
            }

            // Validate startup delay
            if (InstanceStartupDelayMs < 1000)
            {
                logger.LogWarning("Instance startup delay too low. Setting to minimum 1 second for stability.");
                InstanceStartupDelayMs = 1000;
            }
            else if (InstanceStartupDelayMs > 10000)
            {
                logger.LogWarning("Instance startup delay very high. Consider reducing for better performance.");
            }

            if (MaxRetries < 0)
            {
                logger.LogWarning("Max retries cannot be negative");
                MaxRetries = 0;
            }

            foreach (var account in Accounts)
            {
                if (string.IsNullOrEmpty(account.AccountName))
                {
                    logger.LogWarning($"Account name is empty for instance {account.InstanceNumber}");
                }

                if (account.InstanceNumber < 0)
                {
                    logger.LogWarning($"Invalid instance number {account.InstanceNumber} for account {account.AccountName}");
                }

                if (account.EnabledTasks == null || account.EnabledTasks.Count == 0)
                {
                    logger.LogWarning($"No tasks configured for account {account.AccountName}");
                }
            }
        }

        public AccountSettings? GetAccountByInstanceNumber(int instanceNumber)
        {
            return Accounts.FirstOrDefault(a => a.InstanceNumber == instanceNumber);
        }
    }

    public class CycleManagementConfig
    {
        private int _minWaitTimeBetweenCyclesMinutes = 20;
        private int _maxWaitTimeBetweenCyclesMinutes = 40;
        
        [JsonPropertyName("minWaitTimeBetweenCyclesMinutes")]
        public int MinWaitTimeBetweenCyclesMinutes 
        { 
            get => _minWaitTimeBetweenCyclesMinutes;
            set => _minWaitTimeBetweenCyclesMinutes = Math.Max(1, value); // Minimum 1 minute to prevent memory leaks
        }
        
        [JsonPropertyName("maxWaitTimeBetweenCyclesMinutes")]
        public int MaxWaitTimeBetweenCyclesMinutes 
        { 
            get => _maxWaitTimeBetweenCyclesMinutes;
            set => _maxWaitTimeBetweenCyclesMinutes = Math.Max(MinWaitTimeBetweenCyclesMinutes, value); // Ensure max >= min
        }
        
        [JsonPropertyName("shutdownEmulatorsAfterCycle")]
        public bool ShutdownEmulatorsAfterCycle { get; set; } = true;
        
        [JsonPropertyName("maxCycles")]
        public int MaxCycles { get; set; } = 0; // 0 = infinite cycles
        
        [JsonPropertyName("maxTroopTrainWaitMinutes")]
        public int MaxTroopTrainWaitMinutes { get; set; } = 5; // Max time to wait for troop training re-run
        
        [JsonPropertyName("persistentAccountWaitMinutes")]
        public int PersistentAccountWaitMinutes { get; set; } = 0; // Wait time between task cycles for persistent accounts (0 = no wait)
    }

    /// <summary>
    /// Performance tuning configuration - allows customization of delays and limits
    /// </summary>
    public class PerformanceConfig
    {
        // Click/interaction delays (ms)
        [JsonPropertyName("afterClickDelayMs")]
        public int AfterClickDelayMs { get; set; } = 1000;

        [JsonPropertyName("betweenRetriesDelayMs")]
        public int BetweenRetriesDelayMs { get; set; } = 1500;

        [JsonPropertyName("betweenErrorRetriesDelayMs")]
        public int BetweenErrorRetriesDelayMs { get; set; } = 2500;

        [JsonPropertyName("betweenTasksDelayMs")]
        public int BetweenTasksDelayMs { get; set; } = 2000;

        // Instance startup settings
        [JsonPropertyName("instanceStartupBatchSize")]
        public int InstanceStartupBatchSize { get; set; } = 2;

        [JsonPropertyName("instanceStartupBatchDelayMs")]
        public int InstanceStartupBatchDelayMs { get; set; } = 1500;

        // Screenshot settings
        [JsonPropertyName("screenshotCacheTimeoutMs")]
        public int ScreenshotCacheTimeoutMs { get; set; } = 500;

        [JsonPropertyName("maxConcurrentScreenshots")]
        public int MaxConcurrentScreenshots { get; set; } = 6;

        // Status cache settings
        [JsonPropertyName("statusCacheTtlSeconds")]
        public int StatusCacheTtlSeconds { get; set; } = 2;
    }

} 