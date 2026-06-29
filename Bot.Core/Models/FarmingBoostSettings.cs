using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public enum BoostDuration
    {
        None = 0,
        EightHour = 8,
        TwentyFourHour = 24
    }

    public class FarmingBoostSettings : INotifyPropertyChanged
    {
        private bool _enableGatherBoost = false;
        private BoostDuration _selectedBoostDuration = BoostDuration.EightHour;

        [JsonPropertyName("enableGatherBoost")]
        public bool EnableGatherBoost
        {
            get => _enableGatherBoost;
            set
            {
                if (_enableGatherBoost != value)
                {
                    _enableGatherBoost = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonPropertyName("selectedBoostDuration")]
        public BoostDuration SelectedBoostDuration
        {
            get => _selectedBoostDuration;
            set
            {
                if (_selectedBoostDuration != value)
                {
                    _selectedBoostDuration = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}