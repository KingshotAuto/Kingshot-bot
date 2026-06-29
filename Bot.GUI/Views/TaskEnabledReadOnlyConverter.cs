using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using WPFBinding = System.Windows.Data.Binding;
using System.Windows.Data;
using Bot.Core.Models; // For TaskType and AccountSettings
// No longer need ViewModels for AccountSettingsViewModel here specifically

namespace Bot.GUI.Views
{
    public class TaskEnabledReadOnlyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return false;

            TaskType currentTaskType;
            if (values[0] is TaskType t)
                currentTaskType = t;
            else if (values[0] is string s && Enum.TryParse<TaskType>(s, out var parsed))
                currentTaskType = parsed;
            else
                return false;

            // Handle both AccountSettings and any view model that contains EnabledTasks
            var enabledTasks = values[1] is AccountSettings account 
                ? account.EnabledTasks 
                : (values[1]?.GetType().GetProperty("EnabledTasks")?.GetValue(values[1]) as System.Collections.Generic.ICollection<TaskType>);

            return enabledTasks != null && enabledTasks.Contains(currentTaskType);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            // This converter is only used for one-way binding
            return targetTypes.Select(t => WPFBinding.DoNothing).ToArray();
        }
    }
} 