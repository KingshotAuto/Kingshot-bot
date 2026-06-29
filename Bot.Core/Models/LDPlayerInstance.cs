using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bot.Core.Models
{
    public class LDPlayerInstance : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsRunning { get; set; }
        public string TopWindowHandle { get; set; } = string.Empty;
        public string BindWindowHandle { get; set; } = string.Empty;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public override bool Equals(object? obj)
        {
            if (obj is LDPlayerInstance other)
            {
                return Index == other.Index && Name == other.Name;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Index, Name);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
} 