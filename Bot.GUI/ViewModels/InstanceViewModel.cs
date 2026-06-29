using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Bot.Core.Models;
using Bot.Core.LDPlayer;
using Bot.GUI.ViewModels;
using Bot.Core.Logging;
using System;
using System.Linq;
using System.Windows;
using WPFApplication = System.Windows.Application;
using Bot.Core.Tasks.Modules;
using System.Threading.Tasks;
using Bot.Core.Services;

namespace Bot.GUI.ViewModels
{
    public class InstanceViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<InstanceItemViewModel> Instances { get; } = new();
        private InstanceItemViewModel? _selectedInstance;
        public InstanceItemViewModel? SelectedInstance
        {
            get => _selectedInstance;
            set {
                if (_selectedInstance != value) {
                    _selectedInstance = value;
                    _mainViewModel.AppendLog($"Selected instance changed to: {value?.InstanceNumber} ({value?.AccountName})");
                    OnPropertyChanged();
                }
            }
        }

        private readonly InstanceManager _instanceManager;
        private readonly MainViewModel _mainViewModel;
        private readonly AccountSettings _account;
        private string _currentPhase = string.Empty;

        public int InstanceNumber => _account.InstanceNumber;
        public string AccountName => _account.AccountName;

        public string CurrentPhase
        {
            get => _currentPhase;
            set
            {
                if (_currentPhase != value)
                {
                    _currentPhase = value;
                    OnPropertyChanged();
                }
            }
        }


        public InstanceViewModel(InstanceManager instanceManager, MainViewModel mainViewModel, AccountSettings account)
        {
            _instanceManager = instanceManager ?? throw new ArgumentNullException(nameof(instanceManager));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
            _account = account ?? throw new ArgumentNullException(nameof(account));
            
            // Subscribe to phase updates from StartupTask
            StartupTask.OnPhaseUpdate += UpdateInstancePhase;
        }

        private void UpdateInstancePhase(int instanceNumber, string accountName, string phase)
        {
            WPFApplication.Current.Dispatcher.BeginInvoke(() =>
            {
                var instance = Instances.FirstOrDefault(i => i.InstanceNumber == instanceNumber);
                if (instance != null)
                {
                    instance.CurrentPhase = phase;
                    instance.LastTask = $"Startup: {phase}";
                }
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class InstanceItemViewModel : INotifyPropertyChanged
    {
        public int InstanceNumber { get; set; }
        public string AccountName { get; set; } = string.Empty;
        
        public ICommand StartInstanceCommand { get; }
        public ICommand StopInstanceCommand { get; }
        
        private BotRuntimeStatus _status;
        public BotRuntimeStatus Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        
        private string _lastTask = string.Empty;
        public string LastTask 
        { 
            get => _lastTask; 
            set { _lastTask = value; OnPropertyChanged(); } 
        }

        private string _currentPhase = "Idle";
        public string CurrentPhase
        {
            get => _currentPhase;
            set
            {
                _currentPhase = value;
                OnPropertyChanged();
            }
        }

        private bool _isManuallyStopped = false;
        public bool IsManuallyStopped
        {
            get => _isManuallyStopped;
            set
            {
                _isManuallyStopped = value;
                OnPropertyChanged();
            }
        }

        private TimeSpan? _shieldTimeRemaining;
        public TimeSpan? ShieldTimeRemaining
        {
            get => _shieldTimeRemaining;
            private set
            {
                if (_shieldTimeRemaining != value)
                {
                    _shieldTimeRemaining = value;
                    OnPropertyChanged();
                }
            }
        }

        private readonly InstanceManager _instanceManager;
        private readonly MainViewModel _mainViewModel;
        
        public InstanceItemViewModel(AccountSettings account, InstanceManager instanceManager, MainViewModel mainViewModel)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));
            if (instanceManager == null)
                throw new ArgumentNullException(nameof(instanceManager));
            if (mainViewModel == null)
                throw new ArgumentNullException(nameof(mainViewModel));
                
            _instanceManager = instanceManager;
            _mainViewModel = mainViewModel;
            InstanceNumber = account.InstanceNumber;
            AccountName = account.AccountName;
            
            // Initialize commands
            StartInstanceCommand = new RelayCommand(param => {
                try {
                    _mainViewModel.AppendLog($"StartInstanceCommand executed for instance: {InstanceNumber}");
                    StartInstance();
                } catch (Exception ex) {
                    _mainViewModel.AppendLog($"Error in StartInstanceCommand: {ex}");
                }
            });
            StopInstanceCommand = new RelayCommand(param => {
                try {
                    _mainViewModel.AppendLog($"StopInstanceCommand executed for instance: {InstanceNumber}");
                    StopInstance();
                } catch (Exception ex) {
                    _mainViewModel.AppendLog($"Error in StopInstanceCommand: {ex}");
                }
            });
            
            // Subscribe to shield time updates for this specific instance
            ShieldTimeManager.Instance.ShieldTimeUpdated += OnShieldTimeUpdated;
            ShieldTimeManager.Instance.LoadShieldTime(account.InstanceNumber, account.AutoShieldSettings);
        }

        private async Task StartInstanceAsync()
        {
            try {
                _mainViewModel.AppendLog($"Starting instance {InstanceNumber}");
                
                var success = await _instanceManager.StartInstanceAsync(InstanceNumber).ConfigureAwait(false);
                
                if (success)
                {
                    Status = BotRuntimeStatus.Running;
                    IsManuallyStopped = false; // Reset manual stop flag
                    _mainViewModel.AppendLog($"[Instance {InstanceNumber}] LDPlayer started successfully.");
                    
                    // Add a delay to let the instance fully initialize
                    await Task.Delay(2000).ConfigureAwait(false);
                    
                    // Start bot tasks for this instance
                    _mainViewModel.AppendLog($"[Instance {InstanceNumber}] Starting bot tasks...");
                    
                    // Call the main view model's method to run bot tasks for this instance
                    await _mainViewModel.RunBotTasksForSpecificInstance(this).ConfigureAwait(false);
                }
                else
                {
                    _mainViewModel.AppendLog($"[Instance {InstanceNumber}] Failed to start LDPlayer instance.");
                    Status = BotRuntimeStatus.Stopped;
                }
            } catch (Exception ex) {
                _mainViewModel.AppendLog($"Error starting instance {InstanceNumber}: {ex}");
                Status = BotRuntimeStatus.Stopped;
            }
        }

        private void StartInstance()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await StartInstanceAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _mainViewModel.AppendLog($"Unhandled error in StartInstance: {ex}");
                }
            });
        }

        private async Task StopInstanceAsync()
        {
            try {
                _mainViewModel.AppendLog($"Stopping instance {InstanceNumber}");
                
                var success = await _instanceManager.StopInstanceAsync(InstanceNumber).ConfigureAwait(false);
                
                if (success)
                {
                    Status = BotRuntimeStatus.Stopped;
                    LastTask = "Idle";
                    IsManuallyStopped = true;
                    _mainViewModel.AppendLog($"[Instance {InstanceNumber}] Stopped.");
                }
                else
                {
                    _mainViewModel.AppendLog($"[Instance {InstanceNumber}] Failed to stop.");
                }
            } catch (Exception ex) {
                _mainViewModel.AppendLog($"Error stopping instance {InstanceNumber}: {ex}");
            }
        }

        private void StopInstance()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await StopInstanceAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _mainViewModel.AppendLog($"Unhandled error in StopInstance: {ex}");
                }
            });
        }

        private void OnShieldTimeUpdated(int instanceNumber, TimeSpan? remaining)
        {
            if (instanceNumber == InstanceNumber)
            {
                WPFApplication.Current.Dispatcher.BeginInvoke(() =>
                {
                    ShieldTimeRemaining = remaining;
                });
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
} 