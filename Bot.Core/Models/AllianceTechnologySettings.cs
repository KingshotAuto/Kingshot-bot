using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public class AllianceTechnologySettings : INotifyPropertyChanged
    {
        private int _waitHours = 10;

        /// <summary>
        /// Number of hours to wait before running Alliance Technology task again.
        /// Default is 10 hours. Must be between 1 and 24 hours.
        /// </summary>
        [JsonPropertyName("waitHours")]
        public int WaitHours
        {
            get => _waitHours;
            set
            {
                // Clamp value between 1 and 24 hours
                var clampedValue = Math.Max(1, Math.Min(24, value));
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