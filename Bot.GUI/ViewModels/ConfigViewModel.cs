using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Bot.Core.Models;
using Bot.Core.Config;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using Bot.Core.Logging;
using System.Globalization;
using System.Windows.Data;
using System.Windows;
using WPFApplication = System.Windows.Application;
using System.Windows.Controls;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bot.Core.Services;
using Bot.GUI.Views;

namespace Bot.GUI.ViewModels
{
    public class ConfigViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<AccountSettings> Accounts { get; } = new();
        private static readonly HashSet<TaskType> SystemTasks = new()
        {
            TaskType.Startup,
            TaskType.Recovery,
            TaskType.AccountDetection
        };
        
        private static readonly HashSet<TaskType> DisabledTasks = new()
        {
            TaskType.AutoTechnology // Disabled as it's not ready for customers
        };
        
        public ObservableCollection<TaskType> AllTaskTypes { get; } = new(System.Enum.GetValues(typeof(TaskType))
            .Cast<TaskType>()
            .Where(t => !SystemTasks.Contains(t) && !DisabledTasks.Contains(t))
            .OrderBy(t => t.ToString())); // Sort alphabetically
        
        // New Property for ResourceType Enum Values
        public IEnumerable<ResourceType> AllResourceTypes { get; } = System.Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>();

        // General Settings
        private int _totalRunningInstances = 1;
        public int TotalRunningInstances
        {
            get => _totalRunningInstances;
            set { 
                if (_totalRunningInstances != value)
                {
                    _totalRunningInstances = value; 
                    OnPropertyChanged(); 
                    SaveConfigIfNotApplying(); // Auto-save
                }
            }
        }

        // Cycle Management Settings (from BotConfig.CycleManagement)
        private int _minWaitTimeBetweenCyclesMinutes = 20;
        public int MinWaitTimeBetweenCyclesMinutes
        {
            get => _minWaitTimeBetweenCyclesMinutes;
            set { 
                var validatedValue = Math.Max(1, value); // Minimum 1 minute to prevent memory leaks
                if (_minWaitTimeBetweenCyclesMinutes != validatedValue)
                {
                    _minWaitTimeBetweenCyclesMinutes = validatedValue; 
                    
                    // Ensure max is at least as large as min
                    if (_maxWaitTimeBetweenCyclesMinutes < validatedValue)
                    {
                        _maxWaitTimeBetweenCyclesMinutes = validatedValue;
                        OnPropertyChanged(nameof(MaxWaitTimeBetweenCyclesMinutes));
                    }
                    
                    OnPropertyChanged(); 
                    SaveConfigIfNotApplying(); // Auto-save
                }
            }
        }

        private int _maxWaitTimeBetweenCyclesMinutes = 40;
        public int MaxWaitTimeBetweenCyclesMinutes
        {
            get => _maxWaitTimeBetweenCyclesMinutes;
            set { 
                var validatedValue = Math.Max(MinWaitTimeBetweenCyclesMinutes, value); // Ensure max >= min
                if (_maxWaitTimeBetweenCyclesMinutes != validatedValue)
                {
                    _maxWaitTimeBetweenCyclesMinutes = validatedValue; 
                    OnPropertyChanged(); 
                    SaveConfigIfNotApplying(); // Auto-save
                }
            }
        }

        // Always shutdown emulators after each cycle - hardcoded to true
        public bool ShutdownEmulatorsAfterCycle => true;

        private int _maxCycles = 0;
        public int MaxCycles
        {
            get => _maxCycles;
            set { 
                if (_maxCycles != value)
                {
                    _maxCycles = value; 
                    OnPropertyChanged(); 
                    SaveConfigIfNotApplying(); // Auto-save
                }
            }
        }

        private int _maxTroopTrainWaitMinutes = 5;
        public int MaxTroopTrainWaitMinutes
        {
            get => _maxTroopTrainWaitMinutes;
            set { 
                if (_maxTroopTrainWaitMinutes != value)
                {
                    _maxTroopTrainWaitMinutes = value; 
                    OnPropertyChanged(); 
                    SaveConfigIfNotApplying(); // Auto-save
                }
            }
        }

        private int _persistentAccountWaitMinutes = 0;
        public int PersistentAccountWaitMinutes
        {
            get => _persistentAccountWaitMinutes;
            set { 
                if (_persistentAccountWaitMinutes != value)
                {
                    _persistentAccountWaitMinutes = value; 
                    OnPropertyChanged(); 
                    SaveConfigIfNotApplying(); // Auto-save
                }
            }
        }

        public ICommand AddAccountCommand { get; }
        public ICommand RemoveAccountCommand { get; }
        public ICommand AccountReorderedCommand { get; }
        public ICommand AddFarmingTargetCommand { get; }
        public ICommand RemoveFarmingTargetCommand { get; }
        public ICommand ToggleTaskEnabledCommand { get; }
        public ICommand ApplyFarmingTargetsToAllCommand { get; }
        public ICommand ApplyEnabledTasksToAllCommand { get; }
        public ICommand ApplyAutoShieldSettingsToAllCommand { get; }
        public ICommand ApplyAutoBuildSettingsToAllCommand { get; }
        public ICommand ApplyAutoHuntSettingsToAllCommand { get; }
        public ICommand ApplyAutoRallySettingsToAllCommand { get; }
        public ICommand ApplyTroopTrainingSettingsToAllCommand { get; }
        public ICommand ApplyAllianceTechnologySettingsToAllCommand { get; }
        public ICommand ApplyConquestCollectSettingsToAllCommand { get; }

        private AccountSettings? _selectedAccount;
        public AccountSettings? SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (_selectedAccount != value)
                {
                    _logger.LogInfo($"SelectedAccount changing from {_selectedAccount?.AccountName ?? "null"} to {value?.AccountName ?? "null"}");
                    
                    // Unsubscribe from old account's PropertyChanged event
                    if (_selectedAccount != null)
                    {
                        _selectedAccount.PropertyChanged -= SelectedAccount_PropertyChanged;
                    }
                    
                    // Unsubscribe from old account's EnabledTasks CollectionChanged if it exists
                    if (_selectedAccount?.EnabledTasks is ObservableCollection<TaskType> oldEnabledTasks) {
                        oldEnabledTasks.CollectionChanged -= SelectedAccount_EnabledTasks_CollectionChanged;
                    }

                    _selectedAccount = value;
                    OnPropertyChanged();

                    // Subscribe to new account's PropertyChanged event
                    if (_selectedAccount != null)
                    {
                        _selectedAccount.PropertyChanged += SelectedAccount_PropertyChanged;
                    }

                    UpdateSelectedAccountFarmingTargets();
                    
                    // Raise CanExecuteChanged for all commands that depend on SelectedAccount
                    ((RelayCommand)RemoveAccountCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AddFarmingTargetCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)RemoveFarmingTargetCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyFarmingTargetsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyEnabledTasksToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoShieldSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoBuildSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoHuntSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoRallySettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyTroopTrainingSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAllianceTechnologySettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyConquestCollectSettingsToAllCommand).RaiseCanExecuteChanged();

                    // Subscribe to new account's EnabledTasks CollectionChanged
                    if (_selectedAccount?.EnabledTasks is ObservableCollection<TaskType> newEnabledTasks) {
                        newEnabledTasks.CollectionChanged += SelectedAccount_EnabledTasks_CollectionChanged;
                    }
                    
                    // Force UI updates for all properties that depend on SelectedAccount
                    OnPropertyChanged(nameof(SelectedAccountFarmingTargets));
                    OnPropertyChanged(nameof(BreadTargetEnabled));
                    OnPropertyChanged(nameof(BreadTargetLevel));
                    OnPropertyChanged(nameof(WoodTargetEnabled));
                    OnPropertyChanged(nameof(WoodTargetLevel));
                    OnPropertyChanged(nameof(StoneTargetEnabled));
                    OnPropertyChanged(nameof(StoneTargetLevel));
                    OnPropertyChanged(nameof(IronTargetEnabled));
                    OnPropertyChanged(nameof(IronTargetLevel));
                    OnPropertyChanged(nameof(AllTaskTypes));
                    
                    // Force the account to notify its properties
                    if (_selectedAccount != null)
                    {
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.EnabledTasks));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.AccountName));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.InstanceNumber));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.FarmingTargets));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.AutoHuntSettings));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.AutoRallySettings));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.AutoShieldSettings));
                        _selectedAccount.OnPropertyChanged(nameof(AccountSettings.AutoBuildSettings));
                    }
                    
                    // Force complete refresh of task bindings
                    RefreshTaskList();
                    
                    // Note: SelectedAccount change is just UI state, not a data configuration change
                    // ConfigurationChanged event is intentionally NOT called here
                }
            }
        }

        private void SelectedAccount_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Skip auto-save if we're applying configuration to avoid circular loops
            if (_isApplyingConfig || _globalConfigReloading) return;

            // If the account name, instance number, do not shutdown setting, or nested settings change, save the config
            if (e.PropertyName == nameof(AccountSettings.AccountName) ||
                e.PropertyName == nameof(AccountSettings.InstanceNumber) ||
                e.PropertyName == nameof(AccountSettings.DoNotShutdown) ||
                e.PropertyName?.StartsWith("TroopTrainingSettings.") == true ||
                e.PropertyName?.StartsWith("FarmingBoostSettings.") == true)
            {
                _logger.LogInfo($"Account property '{e.PropertyName}' changed for '{SelectedAccount?.AccountName}', auto-saving config.");
                SaveConfig();
            }
        }

        private void SelectedAccount_EnabledTasks_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Skip auto-save if we're applying configuration to avoid circular loops
            if (_isApplyingConfig || _globalConfigReloading) return;
            
            _logger.LogInfo("EnabledTasks collection changed for selected account, auto-saving config.");
            SaveConfig();
        }

        private ObservableCollection<FarmingTargetViewModel> _selectedAccountFarmingTargets = new();
        public ObservableCollection<FarmingTargetViewModel> SelectedAccountFarmingTargets
        {
            get => _selectedAccountFarmingTargets;
            set
            {
                _selectedAccountFarmingTargets = value;
                OnPropertyChanged();
            }
        }

        // Individual resource checkbox properties
        public bool BreadTargetEnabled
        {
            get => SelectedAccount?.FarmingTargets?.Any(ft => ft.ResourceType == ResourceType.Bread) ?? false;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Bread);
                    if (value && existing == null)
                    {
                        SelectedAccount.FarmingTargets.Add(new FarmingTarget { ResourceType = ResourceType.Bread, Level = BreadTargetLevel });
                        SaveConfigIfNotApplying();
                    }
                    else if (!value && existing != null)
                    {
                        SelectedAccount.FarmingTargets.Remove(existing);
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public int BreadTargetLevel
        {
            get => SelectedAccount?.FarmingTargets?.FirstOrDefault(ft => ft.ResourceType == ResourceType.Bread)?.Level ?? 1;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Bread);
                    if (existing != null)
                    {
                        existing.Level = value;
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public bool WoodTargetEnabled
        {
            get => SelectedAccount?.FarmingTargets?.Any(ft => ft.ResourceType == ResourceType.Wood) ?? false;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Wood);
                    if (value && existing == null)
                    {
                        SelectedAccount.FarmingTargets.Add(new FarmingTarget { ResourceType = ResourceType.Wood, Level = WoodTargetLevel });
                        SaveConfigIfNotApplying();
                    }
                    else if (!value && existing != null)
                    {
                        SelectedAccount.FarmingTargets.Remove(existing);
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public int WoodTargetLevel
        {
            get => SelectedAccount?.FarmingTargets?.FirstOrDefault(ft => ft.ResourceType == ResourceType.Wood)?.Level ?? 1;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Wood);
                    if (existing != null)
                    {
                        existing.Level = value;
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public bool StoneTargetEnabled
        {
            get => SelectedAccount?.FarmingTargets?.Any(ft => ft.ResourceType == ResourceType.Stone) ?? false;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Stone);
                    if (value && existing == null)
                    {
                        SelectedAccount.FarmingTargets.Add(new FarmingTarget { ResourceType = ResourceType.Stone, Level = StoneTargetLevel });
                        SaveConfigIfNotApplying();
                    }
                    else if (!value && existing != null)
                    {
                        SelectedAccount.FarmingTargets.Remove(existing);
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public int StoneTargetLevel
        {
            get => SelectedAccount?.FarmingTargets?.FirstOrDefault(ft => ft.ResourceType == ResourceType.Stone)?.Level ?? 1;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Stone);
                    if (existing != null)
                    {
                        existing.Level = value;
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public bool IronTargetEnabled
        {
            get => SelectedAccount?.FarmingTargets?.Any(ft => ft.ResourceType == ResourceType.Iron) ?? false;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Iron);
                    if (value && existing == null)
                    {
                        SelectedAccount.FarmingTargets.Add(new FarmingTarget { ResourceType = ResourceType.Iron, Level = IronTargetLevel });
                        SaveConfigIfNotApplying();
                    }
                    else if (!value && existing != null)
                    {
                        SelectedAccount.FarmingTargets.Remove(existing);
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        public int IronTargetLevel
        {
            get => SelectedAccount?.FarmingTargets?.FirstOrDefault(ft => ft.ResourceType == ResourceType.Iron)?.Level ?? 1;
            set
            {
                if (SelectedAccount != null)
                {
                    var existing = SelectedAccount.FarmingTargets.FirstOrDefault(ft => ft.ResourceType == ResourceType.Iron);
                    if (existing != null)
                    {
                        existing.Level = value;
                        SaveConfigIfNotApplying();
                    }
                    OnPropertyChanged();
                }
            }
        }

        private readonly LogService _logger;
        private readonly BotConfig _config;

        // Event to notify when configuration changes and UI needs refresh
        public event Action? ConfigurationChanged;

        public ConfigViewModel(BotConfig config, LogService logger)
        {
            _config = config;
            _logger = logger;
            AddAccountCommand = new RelayCommand(_ => AddAccount());
            RemoveAccountCommand = new RelayCommand(_ => RemoveAccount(), _ => SelectedAccount != null);
            AccountReorderedCommand = new RelayCommand(_ => OnAccountReordered());
            AddFarmingTargetCommand = new RelayCommand(_ => AddFarmingTarget(), _ => SelectedAccount != null);
            RemoveFarmingTargetCommand = new RelayCommand(param => RemoveFarmingTarget(param as FarmingTargetViewModel), _ => SelectedAccount != null);
            ToggleTaskEnabledCommand = new RelayCommand(param => ToggleTaskEnabled(param as TaskType?));
            ApplyFarmingTargetsToAllCommand = new RelayCommand(_ => ApplyFarmingTargetsToAll(), _ => SelectedAccount != null);
            ApplyEnabledTasksToAllCommand = new RelayCommand(_ => ApplyEnabledTasksToAll(), _ => SelectedAccount != null);
            ApplyAutoShieldSettingsToAllCommand = new RelayCommand(_ => ApplyAutoShieldSettingsToAll(), _ => SelectedAccount != null);
            ApplyAutoBuildSettingsToAllCommand = new RelayCommand(_ => ApplyAutoBuildSettingsToAll(), _ => SelectedAccount != null);
            ApplyAutoHuntSettingsToAllCommand = new RelayCommand(_ => ApplyAutoHuntSettingsToAll(), _ => SelectedAccount != null);
            ApplyAutoRallySettingsToAllCommand = new RelayCommand(_ => ApplyAutoRallySettingsToAll(), _ => SelectedAccount != null);
            ApplyTroopTrainingSettingsToAllCommand = new RelayCommand(_ => ApplyTroopTrainingSettingsToAll(), _ => SelectedAccount != null);
            ApplyAllianceTechnologySettingsToAllCommand = new RelayCommand(_ => ApplyAllianceTechnologySettingsToAll(), _ => SelectedAccount != null);
            ApplyConquestCollectSettingsToAllCommand = new RelayCommand(_ => ApplyConquestCollectSettingsToAll(), _ => SelectedAccount != null);

            LoadConfigOrDefault();
        }

        private void UpdateSelectedAccountFarmingTargets()
        {
            SelectedAccountFarmingTargets.Clear();
            if (SelectedAccount != null)
            {
                if (SelectedAccount.FarmingTargets == null)
                {
                    SelectedAccount.FarmingTargets = new ObservableCollection<FarmingTarget>();
                }
                foreach (var ft in SelectedAccount.FarmingTargets)
                {
                    // Pass 'this' (ConfigViewModel) to FarmingTargetViewModel for auto-save callback
                    SelectedAccountFarmingTargets.Add(new FarmingTargetViewModel(ft, this)); 
                }
            }
        }

        private void AddFarmingTarget()
        {
            if (SelectedAccount != null)
            {
                var newFarmingTarget = new FarmingTarget { ResourceType = ResourceType.Bread, Level = 1 };
                SelectedAccount.FarmingTargets.Add(newFarmingTarget); // Add to model
                SelectedAccountFarmingTargets.Add(new FarmingTargetViewModel(newFarmingTarget, this)); // Add to ViewModel collection
                _logger.LogInfo($"Added new farming target for account {SelectedAccount.AccountName}");
                SaveConfigIfNotApplying(); // Auto-save
            }
        }

        private void RemoveFarmingTarget(FarmingTargetViewModel? targetViewModel)
        {
            if (SelectedAccount != null && targetViewModel != null)
            {
                SelectedAccount.FarmingTargets.Remove(targetViewModel.Model); // Remove from model
                SelectedAccountFarmingTargets.Remove(targetViewModel); // Remove from ViewModel collection
                _logger.LogInfo($"Removed farming target ({targetViewModel.ResourceType} Lvl {targetViewModel.Level}) for account {SelectedAccount.AccountName}");
                SaveConfigIfNotApplying(); // Auto-save
            }
        }

        private void LoadConfigOrDefault(string filePath = "configs/default_config.json")
        {
            try
            {
                var config = ConfigLoader.Load(filePath);
                if (config != null)
                {
                    ApplyConfigToViewModel(config);
                    _logger.LogInfo($"Config loaded from {filePath} into ViewModel.");
                }
                else
                {
                    _logger.LogWarning($"No config file found at {filePath} or it's empty. Using default ViewModel values.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading config into ViewModel from {filePath}: {ex.Message}");
            }
            if (Accounts.Any() && SelectedAccount == null) SelectedAccount = Accounts.First();
        }

        private async Task LoadConfigOrDefaultAsync(string filePath = "configs/default_config.json")
        {
            try
            {
                var config = await Task.Run(() => ConfigLoader.Load(filePath)).ConfigureAwait(false);
                if (config != null)
                {
                    // Switch back to UI thread for UI updates
                    await WPFApplication.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ApplyConfigToViewModel(config);
                        _logger.LogInfo($"Config loaded from {filePath} into ViewModel.");
                    });
                }
                else
                {
                    _logger.LogWarning($"No config file found at {filePath} or it's empty. Using default ViewModel values.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading config into ViewModel from {filePath}: {ex.Message}");
            }
            
            // Ensure UI thread for property access
            await WPFApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Accounts.Any() && SelectedAccount == null) SelectedAccount = Accounts.First();
            });
        }

        private void ApplyConfigToViewModel(BotConfig config)
        {
            Accounts.Clear();
            foreach (var acc in config.Accounts)
            {
                if (acc.FarmingTargets == null) acc.FarmingTargets = new ObservableCollection<FarmingTarget>();
                // Fix-up: Convert any string values in EnabledTasks to TaskType enum values
                if (acc.EnabledTasks != null)
                {
                    var fixedList = new ObservableCollection<TaskType>();
                    foreach (var item in acc.EnabledTasks)
                    {
                        if (item is TaskType t)
                            fixedList.Add(t);
                        else
                        {
                            var s = item.ToString();
                            if (!string.IsNullOrEmpty(s) && Enum.TryParse<TaskType>(s, out var parsed))
                                fixedList.Add(parsed);
                        }
                    }
                    acc.EnabledTasks = fixedList;
                }
                else
                {
                    acc.EnabledTasks = new ObservableCollection<TaskType>();
                }
                Accounts.Add(acc);
            }
            
            // Temporarily suppress auto-save during initial load from config
            _isApplyingConfig = true; 
            TotalRunningInstances = config.TotalRunningInstances;
            if (config.CycleManagement != null)
            {
                MinWaitTimeBetweenCyclesMinutes = config.CycleManagement.MinWaitTimeBetweenCyclesMinutes;
                MaxWaitTimeBetweenCyclesMinutes = config.CycleManagement.MaxWaitTimeBetweenCyclesMinutes;
                // ShutdownEmulatorsAfterCycle is now always true - no need to set it
                // MaxCycles property removed - no longer configurable
                MaxTroopTrainWaitMinutes = config.CycleManagement.MaxTroopTrainWaitMinutes;
                PersistentAccountWaitMinutes = config.CycleManagement.PersistentAccountWaitMinutes;
            }

            // Set SelectedAccount *after* all properties are loaded to avoid premature saves
            // and to ensure CollectionChanged subscription is set up on the fully loaded account.
            if (Accounts.Any()) SelectedAccount = Accounts.First();
            else SelectedAccount = null; // Ensure farming targets are cleared if no accounts
            
            UpdateSelectedAccountFarmingTargets();
            
            // Force UI refresh for all bound properties
            OnPropertyChanged(nameof(Accounts));
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(SelectedAccountFarmingTargets));
            OnPropertyChanged(nameof(BreadTargetEnabled));
            OnPropertyChanged(nameof(BreadTargetLevel));
            OnPropertyChanged(nameof(WoodTargetEnabled));
            OnPropertyChanged(nameof(WoodTargetLevel));
            OnPropertyChanged(nameof(StoneTargetEnabled));
            OnPropertyChanged(nameof(StoneTargetLevel));
            OnPropertyChanged(nameof(IronTargetEnabled));
            OnPropertyChanged(nameof(IronTargetLevel));
            OnPropertyChanged(nameof(TotalRunningInstances));
            OnPropertyChanged(nameof(MinWaitTimeBetweenCyclesMinutes));
            OnPropertyChanged(nameof(MaxWaitTimeBetweenCyclesMinutes));
            OnPropertyChanged(nameof(MaxTroopTrainWaitMinutes));
            
            // Re-enable auto-save after all configuration is applied
            _isApplyingConfig = false; 
        }
        private bool _isApplyingConfig = false; // Flag to prevent auto-save during ApplyConfigToViewModel
        private static bool _globalConfigReloading = false; // Global flag to prevent circular reloads
        
        /// <summary>
        /// Sets the global configuration reloading flag
        /// </summary>
        public static void SetGlobalConfigReloading(bool isReloading)
        {
            _globalConfigReloading = isReloading;
        }
        
        /// <summary>
        /// Saves configuration only if not currently applying config to avoid circular loops
        /// </summary>
        public async Task SaveConfigIfNotApplyingAsync()
        {
            if (!_isApplyingConfig && !_globalConfigReloading)
            {
                await SaveConfigAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Saves configuration only if not currently applying config to avoid circular loops (fire-and-forget version)
        /// </summary>
        public void SaveConfigIfNotApplying()
        {
            if (!_isApplyingConfig && !_globalConfigReloading)
            {
                SaveConfig();
            }
        }


        public BotConfig GetCurrentBotConfig()
        {
            var config = new BotConfig
            {
                Accounts = Accounts.ToList(),
                TotalRunningInstances = TotalRunningInstances,
                CycleManagement = new CycleManagementConfig
                {
                    MinWaitTimeBetweenCyclesMinutes = MinWaitTimeBetweenCyclesMinutes,
                    MaxWaitTimeBetweenCyclesMinutes = MaxWaitTimeBetweenCyclesMinutes,
                    ShutdownEmulatorsAfterCycle = true, // Always true
                    MaxCycles = 0, // Always 0 for infinite cycles
                    MaxTroopTrainWaitMinutes = MaxTroopTrainWaitMinutes,
                    PersistentAccountWaitMinutes = PersistentAccountWaitMinutes
                }
            };
            return config;
        }

        // SaveConfig is now public to be callable from other places if necessary, e.g. FarmingTargetViewModel
        // but primarily used by property setters within this ViewModel or CollectionChanged events.
        public async Task SaveConfigAsync() 
        {
            if (_isApplyingConfig || _globalConfigReloading) 
            {
                _logger.LogInfo("SaveConfig skipped - currently applying configuration or reloading");
                return; // Don't save while initially applying config or during reload
            }

            _logger.LogInfo("Auto-saving configuration...");
            try {
                var config = GetCurrentBotConfig();
                await Task.Run(() => ConfigLoader.Save("configs/default_config.json", config)).ConfigureAwait(false);
                _logger.LogInfo($"Config auto-saved to configs/default_config.json with {Accounts.Count} accounts and current settings.");
                
                // Notify that configuration has changed, but not during global config reloading
                if (!_globalConfigReloading)
                {
                    // Invoke on UI thread if needed
                    await WPFApplication.Current.Dispatcher.InvokeAsync(() => ConfigurationChanged?.Invoke());
                }
                else
                {
                    _logger.LogInfo("ConfigurationChanged event suppressed during global config reloading");
                }
            } catch (Exception ex) {
                _logger.LogError($"Error in auto-saving config: {ex}");
            }
        }

        // Keep synchronous version for property setters that can't be async
        public void SaveConfig()
        {
            _ = Task.Run(async () => await SaveConfigAsync().ConfigureAwait(false));
        }

        private void AddAccount()
        {
            try
            {
                var ldPlayerService = new LDPlayerService(_logger);
                var viewModel = new AddAccountViewModel(ldPlayerService, Accounts);
                var window = new AddAccountWindow { DataContext = viewModel, Owner = WPFApplication.Current.MainWindow };

                if (window.ShowDialog() == true)
                {
                    AccountSettings? lastAddedAccount = null;
                    foreach (var instance in viewModel.SelectedInstances)
                    {
                        var newAccount = new AccountSettings
                        {
                            AccountName = instance.Name,
                            InstanceNumber = instance.Index,
                            IsEnabled = true
                        };
                        
                        // Collections are now initialized in constructor
                        
                        // Add default enabled tasks
                        var defaultTasks = new[]
                        {
                            TaskType.Farming,
                            TaskType.AutoHunt,
                            TaskType.AutoHeal,
                            TaskType.ClaimMail,
                            TaskType.CollectVip
                        };
                        
                        foreach (var task in defaultTasks)
                        {
                            newAccount.EnabledTasks.Add(task);
                        }
                        
                        // Add basic farming targets
                        newAccount.FarmingTargets.Add(new FarmingTarget
                        {
                            ResourceType = ResourceType.Bread,
                            Level = 6
                        });
                        
                        newAccount.FarmingTargets.Add(new FarmingTarget
                        {
                            ResourceType = ResourceType.Wood,
                            Level = 6
                        });
                        
                        // Add account to collection
                        Accounts.Add(newAccount);
                        lastAddedAccount = newAccount;
                        
                        _logger.LogInfo($"Added account: {newAccount.AccountName} (Instance {newAccount.InstanceNumber}) with {newAccount.EnabledTasks.Count} tasks and {newAccount.FarmingTargets.Count} farming targets");
                    }
                    
                    // Set the selected account to the last added account
                    if (lastAddedAccount != null)
                    {
                        SelectedAccount = lastAddedAccount;
                        _logger.LogInfo($"Selected account set to: {lastAddedAccount.AccountName}");
                    }
                    
                    // Save config after setting selection
                    SaveConfig();
                    
                    // Force refresh of all account-related UI
                    RefreshAccountDisplay();
                    
                    // Force update of all command states
                    ((RelayCommand)RemoveAccountCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AddFarmingTargetCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)RemoveFarmingTargetCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyFarmingTargetsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyEnabledTasksToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoShieldSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoBuildSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoHuntSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAutoRallySettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyTroopTrainingSettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyAllianceTechnologySettingsToAllCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ApplyConquestCollectSettingsToAllCommand).RaiseCanExecuteChanged();

                    // Force CommandManager to re-query all commands
                    CommandManager.InvalidateRequerySuggested();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding account: {ex.Message}");
            }
        }

        private void RemoveAccount()
        {
            _logger.LogInfo("RemoveAccount called");
            try {
                if (SelectedAccount != null) {
                    var accountToRemove = SelectedAccount;
                    var index = Accounts.IndexOf(accountToRemove);
                    Accounts.Remove(accountToRemove);
                    if (Accounts.Any())
                    {
                        SelectedAccount = Accounts.ElementAtOrDefault(index > 0 ? index -1 : 0);
                    }
                    else
                    {
                        SelectedAccount = null;
                    }
                    _logger.LogInfo($"Account removed: {accountToRemove.AccountName} (Instance {accountToRemove.InstanceNumber})");
                    SaveConfigIfNotApplying(); // Auto-save after removing an account
                }
            } catch (Exception ex) {
                _logger.LogError($"Error in RemoveAccount: {ex}");
            }
        }

        private void OnAccountReordered()
        {
            _logger.LogInfo("Account order changed via drag and drop");
            SaveConfigIfNotApplying(); // Auto-save after reordering
        }

        private void ToggleTaskEnabled(object? param)
        {
            if (SelectedAccount == null || param == null)
                return;
            if (param is TaskType taskType)
            {
                if (SelectedAccount.EnabledTasks.Contains(taskType))
                {
                    SelectedAccount.EnabledTasks.Remove(taskType);
                }
                else
                {
                    SelectedAccount.EnabledTasks.Add(taskType);
                }
                // Force the UI to re-evaluate bindings that depend on EnabledTasks
                SelectedAccount.OnPropertyChanged(nameof(AccountSettings.EnabledTasks));
                // CollectionChanged event will trigger SaveConfig()
            }
            else if (param is string taskTypeString && Enum.TryParse<TaskType>(taskTypeString, out var parsedType))
            {
                if (SelectedAccount.EnabledTasks.Contains(parsedType))
                {
                    SelectedAccount.EnabledTasks.Remove(parsedType);
                }
                else
                {
                    SelectedAccount.EnabledTasks.Add(parsedType);
                }
                // Force the UI to re-evaluate bindings that depend on EnabledTasks
                SelectedAccount.OnPropertyChanged(nameof(AccountSettings.EnabledTasks));
                // CollectionChanged event will trigger SaveConfig()
            }
        }

        private void ApplyFarmingTargetsToAll()
        {
            if (SelectedAccount == null) return;

            var targetsToCopy = new ObservableCollection<FarmingTarget>(
                SelectedAccount.FarmingTargets.Select(ft => new FarmingTarget { ResourceType = ft.ResourceType, Level = ft.Level })
            );
            var sourceSettings = SelectedAccount.FarmingSettings;

            foreach (var acc in Accounts)
            {
                if (acc != SelectedAccount)
                {
                    // Apply farming targets
                    acc.FarmingTargets = new ObservableCollection<FarmingTarget>(
                        targetsToCopy.Select(ft => new FarmingTarget { ResourceType = ft.ResourceType, Level = ft.Level })
                    );
                    
                    // Apply farming settings
                    acc.FarmingSettings.MaxFarmingMarches = sourceSettings.MaxFarmingMarches;
                }
            }
            
            _logger.LogInfo($"Applied farming targets and settings from {SelectedAccount.AccountName} to all other accounts.");
            SaveConfig();
            
            // This is a UI-only update; if another account is selected, its view will update automatically.
            // No need to call UpdateSelectedAccountFarmingTargets here as it only affects the currently selected one.
        }

        private void ApplyEnabledTasksToAll()
        {
            if (SelectedAccount == null) return;

            var enabledTasks = new HashSet<TaskType>(SelectedAccount.EnabledTasks);
            
            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.EnabledTasks.Clear();
                foreach (var task in enabledTasks)
                {
                    account.EnabledTasks.Add(task);
                }
            }

            SaveConfig();
            _logger.LogInfo($"Applied enabled tasks from {SelectedAccount.AccountName} to all other accounts");
        }


        private void ApplyAutoShieldSettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.AutoShieldSettings;
            
            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.AutoShieldSettings.UseRechargeableShield = sourceSettings.UseRechargeableShield;
                account.AutoShieldSettings.SelectedBackupShield = sourceSettings.SelectedBackupShield;
            }

            SaveConfig();
            _logger.LogInfo($"Applied Auto Shield settings from {SelectedAccount.AccountName} to all other accounts");
        }

        private void ApplyAutoBuildSettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.AutoBuildSettings;
            
            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.AutoBuildSettings.MaxSpeedupMinutes = sourceSettings.MaxSpeedupMinutes;
                account.AutoBuildSettings.EnableAutoSpeedup = sourceSettings.EnableAutoSpeedup;
            }

            SaveConfig();
            _logger.LogInfo($"Applied Auto Build settings from {SelectedAccount.AccountName} to all other accounts");
        }

        private void ApplyAutoHuntSettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.AutoHuntSettings;
            
            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.AutoHuntSettings.UseEqualize = sourceSettings.UseEqualize;
            }

            SaveConfig();
            _logger.LogInfo($"Applied Auto Hunt settings from {SelectedAccount.AccountName} to all other accounts");
        }

        private void ApplyAutoRallySettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.AutoRallySettings;
            
            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.AutoRallySettings.AutoJoin = sourceSettings.AutoJoin;
                account.AutoRallySettings.AutoJoinMinHours = sourceSettings.AutoJoinMinHours;
                account.AutoRallySettings.AutoJoinCheckIntervalHours = sourceSettings.AutoJoinCheckIntervalHours;
                // Don't copy LastAutoJoinCheck - each account should maintain its own timing
            }

            SaveConfig();
            _logger.LogInfo($"Applied Auto Rally settings from {SelectedAccount.AccountName} to all other accounts");
        }

        private void ApplyTroopTrainingSettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.TroopTrainingSettings;

            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.TroopTrainingSettings.TrainLevel1Only = sourceSettings.TrainLevel1Only;
            }

            SaveConfig();
            _logger.LogInfo($"Applied Troop Training settings from {SelectedAccount.AccountName} to all other accounts");
        }

        private void ApplyAllianceTechnologySettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.AllianceTechnologySettings;

            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.AllianceTechnologySettings.WaitHours = sourceSettings.WaitHours;
            }

            SaveConfig();
            _logger.LogInfo($"Applied Alliance Technology settings from {SelectedAccount.AccountName} to all other accounts");
        }

        private void ApplyConquestCollectSettingsToAll()
        {
            if (SelectedAccount == null) return;

            var sourceSettings = SelectedAccount.ConquestCollectSettings;

            foreach (var account in Accounts.Where(a => a != SelectedAccount))
            {
                account.ConquestCollectSettings.WaitHours = sourceSettings.WaitHours;
            }

            SaveConfig();
            _logger.LogInfo($"Applied Conquest Collect settings from {SelectedAccount.AccountName} to all other accounts");
        }

        public void RefreshTaskList()
        {
            OnPropertyChanged(nameof(AllTaskTypes));
        }
        
        public void RefreshAccountDisplay()
        {
            // Skip refresh if we're applying configuration to prevent infinite loops
            if (_isApplyingConfig || _globalConfigReloading) return;
            
            _logger.LogInfo("RefreshAccountDisplay called - forcing complete UI refresh");
            
            // Force refresh of all account-related properties
            OnPropertyChanged(nameof(Accounts));
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(SelectedAccountFarmingTargets));
            OnPropertyChanged(nameof(BreadTargetEnabled));
            OnPropertyChanged(nameof(BreadTargetLevel));
            OnPropertyChanged(nameof(WoodTargetEnabled));
            OnPropertyChanged(nameof(WoodTargetLevel));
            OnPropertyChanged(nameof(StoneTargetEnabled));
            OnPropertyChanged(nameof(StoneTargetLevel));
            OnPropertyChanged(nameof(IronTargetEnabled));
            OnPropertyChanged(nameof(IronTargetLevel));
            OnPropertyChanged(nameof(AllTaskTypes));
            
            // Force the selected account to refresh its properties
            if (SelectedAccount != null)
            {
                SelectedAccount.OnPropertyChanged(nameof(AccountSettings.AccountName));
                SelectedAccount.OnPropertyChanged(nameof(AccountSettings.InstanceNumber));
                SelectedAccount.OnPropertyChanged(nameof(AccountSettings.EnabledTasks));
                SelectedAccount.OnPropertyChanged(nameof(AccountSettings.FarmingTargets));
            }
            
            // Update the farming targets view
            UpdateSelectedAccountFarmingTargets();
            
            // Trigger configuration changed event to refresh the main view, but not during global config reloading
            if (!_globalConfigReloading)
            {
                ConfigurationChanged?.Invoke();
            }
            else
            {
                _logger.LogInfo("ConfigurationChanged event suppressed in RefreshAccountDisplay during global config reloading");
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
} 