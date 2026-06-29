using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public class TroopTrainingSettings : INotifyPropertyChanged
    {
        private bool _trainLevel1Only = false;

        [JsonPropertyName("trainLevel1Only")]
        public bool TrainLevel1Only
        {
            get => _trainLevel1Only;
            set
            {
                if (_trainLevel1Only != value)
                {
                    _trainLevel1Only = value;
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