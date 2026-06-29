using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public enum TaskType
    {
        Startup,
        AccountDetection,
        Farming,
        AutoHunt,
        AutoHeal,
        Recovery,
        AutoAllianceHelp,
        TroopTraining,
        ConquestCollect,
        ClaimMail,
        ChangeAccount,
        AutoBuild,
        AutoClaimHero,
        AutoShield,
        CollectVip,
        ClaimMissions,
        ResidentWelcome,
        AllianceTechnology,
        AutoTechnology,
        AutoRally
    }

    public class AccountSettings : INotifyPropertyChanged
    {
        public AccountSettings()
        {
            // Ensure collections are always initialized
            TaskSequence = new ObservableCollection<TaskType>();
            EnabledTasks = new ObservableCollection<TaskType>();
            TaskSettings = new Dictionary<string, string>();
            FarmingTargets = new ObservableCollection<FarmingTarget>();
            AutoHuntSettings = new AutoHuntSettings();
            AutoRallySettings = new AutoRallySettings();
            AutoBuildSettings = new AutoBuildSettings();
            AutoShieldSettings = new AutoShieldSettings();
            FarmingSettings = new FarmingSettings();
            TroopTrainingSettings = new TroopTrainingSettings();
            FarmingBoostSettings = new FarmingBoostSettings();
            AllianceTechnologySettings = new AllianceTechnologySettings();
            ConquestCollectSettings = new ConquestCollectSettings();

            // Subscribe to nested settings PropertyChanged events to propagate changes
            TroopTrainingSettings.PropertyChanged += (sender, e) => OnPropertyChanged($"TroopTrainingSettings.{e.PropertyName}");
            FarmingBoostSettings.PropertyChanged += (sender, e) => OnPropertyChanged($"FarmingBoostSettings.{e.PropertyName}");
            AllianceTechnologySettings.PropertyChanged += (sender, e) => OnPropertyChanged($"AllianceTechnologySettings.{e.PropertyName}");
            ConquestCollectSettings.PropertyChanged += (sender, e) => OnPropertyChanged($"ConquestCollectSettings.{e.PropertyName}");
        }
        
        private string _accountName = string.Empty;
        [JsonPropertyName("accountName")]
        public string AccountName
        {
            get => _accountName;
            set
            {
                if (_accountName != value)
                {
                    _accountName = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _instanceNumber;
        [JsonPropertyName("instanceNumber")]
        public int InstanceNumber
        {
            get => _instanceNumber;
            set
            {
                if (_instanceNumber != value)
                {
                    _instanceNumber = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonPropertyName("taskSequence")]
        public ObservableCollection<TaskType> TaskSequence { get; set; }

        [JsonPropertyName("enabledTasks")]
        public ObservableCollection<TaskType> EnabledTasks { get; set; }

        [JsonPropertyName("taskSettings")]
        public Dictionary<string, string> TaskSettings { get; set; }

        [JsonPropertyName("farmingTargets")]
        public ObservableCollection<FarmingTarget> FarmingTargets { get; set; }

        private bool _isEnabled = true;
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        private string? _customConfigPath;
        [JsonPropertyName("customConfigPath")]
        public string? CustomConfigPath
        {
            get => _customConfigPath;
            set
            {
                if (_customConfigPath != value)
                {
                    _customConfigPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public AutoHuntSettings AutoHuntSettings { get; set; }
        
        [JsonPropertyName("autoRallySettings")]
        public AutoRallySettings AutoRallySettings { get; set; }
        
        [JsonPropertyName("autoBuildSettings")]
        public AutoBuildSettings AutoBuildSettings { get; set; }

        [JsonPropertyName("autoShieldSettings")]
        public AutoShieldSettings AutoShieldSettings { get; set; }

        [JsonPropertyName("farmingSettings")]
        public FarmingSettings FarmingSettings { get; set; }

        [JsonPropertyName("troopTrainingSettings")]
        public TroopTrainingSettings TroopTrainingSettings { get; set; }

        [JsonPropertyName("farmingBoostSettings")]
        public FarmingBoostSettings FarmingBoostSettings { get; set; }

        [JsonPropertyName("allianceTechnologySettings")]
        public AllianceTechnologySettings AllianceTechnologySettings { get; set; }

        [JsonPropertyName("conquestCollectSettings")]
        public ConquestCollectSettings ConquestCollectSettings { get; set; }

        private bool _doNotShutdown = false;
        [JsonPropertyName("doNotShutdown")]
        public bool DoNotShutdown
        {
            get => _doNotShutdown;
            set
            {
                if (_doNotShutdown != value)
                {
                    _doNotShutdown = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
} 