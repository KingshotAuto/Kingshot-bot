using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public class ConquestCollectSettings : INotifyPropertyChanged
    {
        private int _waitHours = 4;

        /// <summary>
        /// Number of hours to wait before running Conquest Collect task again.
        /// Default is 4 hours. Must be between 1 and 12 hours.
        /// </summary>
        [JsonPropertyName("waitHours")]
        public int WaitHours
        {
            get => _waitHours;
            set
            {
                // Clamp value between 1 and 12 hours
                var clampedValue = Math.Max(1, Math.Min(12, value));
                if (_waitHours != clampedValue)
                {
                    _waitHours = clampedValue;
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