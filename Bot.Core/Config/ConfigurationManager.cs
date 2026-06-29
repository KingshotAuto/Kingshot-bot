using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Bot.Core.Models;
using Bot.Core.Logging;

namespace Bot.Core.Config
{
    public class ConfigurationManager : IConfigurationManager
    {
        private static readonly object _lock = new();
        private static ConfigurationManager? _instance;
        private readonly string _configPath;
        private BotConfig _currentConfig;
        private LogService? _logger;
        private bool _configLoadedSuccessfully;
        
        // TEMPLATE MANAGEMENT: Predefined configuration templates using actual TaskType values
        private readonly Dictionary<string, ConfigTemplate> _configTemplates = new()
        {
            ["farming_focused"] = new ConfigTemplate
            {
                Name = "Farming Focused",
                Description = "Optimized for resource farming",
                DefaultTasks = new[] { TaskType.Farming, TaskType.AutoHunt, TaskType.AutoHeal },
                MaxConcurrentInstances = 3,
                TaskDelayMs = 1000
            },
            ["combat_focused"] = new ConfigTemplate
            {
                Name = "Combat Focused", 
                Description = "Optimized for PvP and combat",
                DefaultTasks = new[] { TaskType.TroopTraining, TaskType.AutoHunt, TaskType.AutoHeal },
                MaxConcurrentInstances = 2,
                TaskDelayMs = 2000
            },
            ["alliance_focused"] = new ConfigTemplate
            {
                Name = "Alliance Focused",
                Description = "Optimized for alliance activities",
                DefaultTasks = new[] { TaskType.AutoAllianceHelp, TaskType.ClaimMail, TaskType.ConquestCollect },
                MaxConcurrentInstances = 4,
                TaskDelayMs = 500
            }
        };

        public static ConfigurationManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ConfigurationManager();
                    }
                }
                
                // Warn if Initialize hasn't been called yet
                if (_instance._logger == null)
                {
                    System.Diagnostics.Debug.WriteLine("Warning: ConfigurationManager.Instance accessed before Initialize(LogService) was called. Logging will be unavailable.");
                }
                
                return _instance;
            }
        }
        
        public static void Initialize(LogService logger)
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new ConfigurationManager();
                }
                _instance.SetLogger(logger);
            }
        }

        private ConfigurationManager()
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "configs", "config.json");
            var (config, loadSuccess) = LoadConfigSafely();
            _currentConfig = config;
            _configLoadedSuccessfully = loadSuccess;
        }
        
        private void SetLogger(LogService logger)
        {
            _logger = logger;
            _logger?.LogInfo($"Configuration Manager initialized with {_configTemplates.Count} templates");
            
            if (!_configLoadedSuccessfully)
            {
                _logger?.LogWarning("Configuration failed to load during initialization - using fallback defaults");
            }
        }

        public BotConfig GetConfig()
        {
            return _currentConfig;
        }

        public void SaveConfig(BotConfig config)
        {
            try
            {
                var directory = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var jsonString = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(_configPath, jsonString);
                _currentConfig = config;
                _configLoadedSuccessfully = true; // Mark as successfully loaded after save
                _logger?.LogInfo("Configuration saved successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error saving configuration: {ex.Message}");
                throw;
            }
        }

        public void ReloadConfig()
        {
            var (config, loadSuccess) = LoadConfigSafely();
            _currentConfig = config;
            _configLoadedSuccessfully = loadSuccess;
            _logger?.LogInfo($"Configuration reloaded - Success: {loadSuccess}");
        }

        private (BotConfig config, bool success) LoadConfigSafely()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger?.LogWarning("Configuration file not found, will create default configuration on first save");
                    return (CreateDefaultConfig(), false); // Don't auto-save, just return default
                }

                var jsonString = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<BotConfig>(jsonString);
                if (config == null)
                {
                    throw new InvalidOperationException("Failed to deserialize configuration");
                }

                // Validate and fix problematic values that can cause memory leaks
                ValidateAndFixConfig(config);

                _logger?.LogInfo("Configuration loaded successfully");
                return (config, true);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error loading configuration: {ex.Message}");
                _logger?.LogWarning("Using fallback default configuration - original config file preserved");
                return (CreateDefaultConfig(), false); // Return default but mark as failed
            }
        }

        private BotConfig CreateDefaultConfig()
        {
            return new BotConfig
            {
                CycleManagement = new CycleManagementConfig
                {
                    MinWaitTimeBetweenCyclesMinutes = 20,
                    MaxWaitTimeBetweenCyclesMinutes = 40,
                    ShutdownEmulatorsAfterCycle = false,
                    MaxCycles = 0,
                    MaxTroopTrainWaitMinutes = 30
                }
            };
        }

        /// <summary>
        /// Validates and fixes configuration values that can cause system issues
        /// </summary>
        private void ValidateAndFixConfig(BotConfig config)
        {
            var cycleConfig = config.CycleManagement;
            bool configChanged = false;

            // Fix zero wait times that cause memory leaks and GUI freezing
            if (cycleConfig.MinWaitTimeBetweenCyclesMinutes < 1)
            {
                _logger?.LogWarning($"MinWaitTimeBetweenCyclesMinutes was {cycleConfig.MinWaitTimeBetweenCyclesMinutes}, setting to minimum value of 1 to prevent memory leaks");
                cycleConfig.MinWaitTimeBetweenCyclesMinutes = 1;
                configChanged = true;
            }

            // Ensure max is at least as large as min
            if (cycleConfig.MaxWaitTimeBetweenCyclesMinutes < cycleConfig.MinWaitTimeBetweenCyclesMinutes)
            {
                _logger?.LogWarning($"MaxWaitTimeBetweenCyclesMinutes was {cycleConfig.MaxWaitTimeBetweenCyclesMinutes}, adjusting to match MinWaitTimeBetweenCyclesMinutes ({cycleConfig.MinWaitTimeBetweenCyclesMinutes})");
                cycleConfig.MaxWaitTimeBetweenCyclesMinutes = cycleConfig.MinWaitTimeBetweenCyclesMinutes;
                configChanged = true;
            }

            // Auto-save corrected configuration
            if (configChanged)
            {
                _logger?.LogInfo("Configuration was automatically corrected to prevent system issues. Changes will be saved.");
                try
                {
                    SaveConfig(config);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to save corrected configuration: {ex.Message}");
                }
            }
        }

        // TEMPLATE APPLICATION: Create config from template
        public BotConfig CreateConfigFromTemplate(string templateName, 
                                                 IEnumerable<(string AccountName, int InstanceNumber)> accounts)
        {
            if (!_configTemplates.TryGetValue(templateName, out var template))
            {
                throw new ArgumentException($"Unknown template: {templateName}");
            }

            var config = new BotConfig
            {
                TotalRunningInstances = template.MaxConcurrentInstances
            };

            foreach (var (accountName, instanceNumber) in accounts)
            {
                var enabledTasks = new System.Collections.ObjectModel.ObservableCollection<TaskType>(template.DefaultTasks);
                config.Accounts.Add(new AccountSettings
                {
                    AccountName = accountName,
                    InstanceNumber = instanceNumber,
                    EnabledTasks = enabledTasks
                });
            }

            _logger?.LogInfo($"Created config from template: {templateName} with {config.Accounts.Count} accounts");
            return config;
        }

        // VALIDATION with detailed reporting
        public ConfigValidationResult ValidateConfig(BotConfig config)
        {
            var result = new ConfigValidationResult();
            var issues = new List<string>();

            // Check for duplicate instance numbers
            var instanceNumbers = config.Accounts.Select(a => a.InstanceNumber).ToList();
            var duplicates = instanceNumbers.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key);
            
            foreach (var duplicate in duplicates)
            {
                issues.Add($"Duplicate instance number: {duplicate}");
            }

            // Check for empty account names
            var emptyNames = config.Accounts.Where(a => string.IsNullOrWhiteSpace(a.AccountName)).ToList();
            if (emptyNames.Any())
            {
                issues.Add($"Found {emptyNames.Count} accounts with empty names");
            }

            // Check for valid instance numbers
            var invalidInstances = config.Accounts.Where(a => a.InstanceNumber < 0).ToList();
            if (invalidInstances.Any())
            {
                issues.Add($"Found {invalidInstances.Count} accounts with invalid instance numbers");
            }

            result.IsValid = !issues.Any();
            result.Issues = issues;

            _logger?.LogInfo($"Config validation completed: {(result.IsValid ? "VALID" : "INVALID")}");

            if (!result.IsValid)
            {
                _logger?.LogError($"Config validation failed with {issues.Count} issues: {string.Join(", ", issues)}");
            }

            return result;
        }

        public IEnumerable<string> GetAvailableTemplates() => _configTemplates.Keys;
        
        public ConfigTemplate? GetTemplate(string templateName) => _configTemplates.GetValueOrDefault(templateName);

        // ... additional methods for backup, restore, migration, etc.
    }

    public class ConfigTemplate
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public TaskType[] DefaultTasks { get; set; } = Array.Empty<TaskType>();
        public int MaxConcurrentInstances { get; set; } = 1;
        public int TaskDelayMs { get; set; } = 1000;
    }

    public class ConfigValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Issues { get; set; } = new();
    }
} 