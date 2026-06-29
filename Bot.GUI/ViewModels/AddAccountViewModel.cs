using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Bot.Core.Models;
using Bot.Core.Services;

namespace Bot.GUI.ViewModels
{
    public class AddAccountViewModel : INotifyPropertyChanged
    {
        private readonly LDPlayerService _ldPlayerService;
        private readonly IEnumerable<AccountSettings> _existingAccounts;
        private bool _isLoading;
        private string _errorMessage = string.Empty;

        public ObservableCollection<LDPlayerInstance> AvailableInstances { get; } = new();
        public List<LDPlayerInstance> SelectedInstances => AvailableInstances.Where(i => i.IsSelected).ToList();

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand LoadInstancesCommand { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<bool>? CloseRequested;

        public AddAccountViewModel(LDPlayerService ldPlayerService, IEnumerable<AccountSettings> existingAccounts)
        {
            _ldPlayerService = ldPlayerService;
            _existingAccounts = existingAccounts;

            LoadInstancesCommand = new RelayCommand(async _ => await LoadInstances());
            ConfirmCommand = new RelayCommand(_ => Confirm(), _ => CanConfirm());
            CancelCommand = new RelayCommand(_ => Cancel());

            // Load instances when the ViewModel is created
            _ = LoadInstances();
        }

        private async Task LoadInstances()
        {
            try
            {
                IsLoading = true;
                ErrorMessage = string.Empty;
                
                foreach (var instance in AvailableInstances)
                {
                    instance.PropertyChanged -= Instance_PropertyChanged;
                }
                AvailableInstances.Clear();

                var instances = await _ldPlayerService.GetInstancesAsync();
                var existingIndices = _existingAccounts.Select(a => a.InstanceNumber).ToHashSet();

                foreach (var instance in instances.Where(i => !existingIndices.Contains(i.Index)))
                {
                    instance.PropertyChanged += Instance_PropertyChanged;
                    AvailableInstances.Add(instance);
                }

                if (!AvailableInstances.Any())
                {
                    ErrorMessage = "No available instances found. All instances are already configured or none exist.";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error loading instances: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void Instance_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LDPlayerInstance.IsSelected))
            {
                // Force the ConfirmCommand to re-evaluate its CanExecute status
                if (ConfirmCommand is RelayCommand command)
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }

        private bool CanConfirm()
        {
            return SelectedInstances.Any() && !IsLoading;
        }

        private void Confirm()
        {
            CloseRequested?.Invoke(this, true);
        }

        private void Cancel()
        {
            CloseRequested?.Invoke(this, false);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            
            // If IsLoading changes, we need to re-evaluate ConfirmCommand
            if (propertyName == nameof(IsLoading) && ConfirmCommand is RelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }
} 