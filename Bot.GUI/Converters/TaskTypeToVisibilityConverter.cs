using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Bot.Core.Models;
using System.Collections.Generic;

namespace Bot.GUI.Converters
{
    public class TaskTypeToVisibilityConverter : IValueConverter
    {
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

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ObservableCollection<TaskType> enabledTasks && parameter is string taskTypeStr)
            {
                if (Enum.TryParse<TaskType>(taskTypeStr, out var taskType))
                {
                    // Hide system tasks and disabled tasks
                    if (SystemTasks.Contains(taskType) || DisabledTasks.Contains(taskType))
                        return Visibility.Collapsed;

                    return enabledTasks.Contains(taskType) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 