using System;
using System.Globalization;
using System.Windows.Data;
using Bot.Core.Models;

namespace Bot.GUI.Views
{
    public class TaskEnabledToCheckedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string taskTypeString && parameter != null)
            {
                if (Enum.TryParse<TaskType>(taskTypeString, out var taskType))
                {
                    var enabledTasks = parameter is AccountSettings account 
                        ? account.EnabledTasks 
                        : (parameter?.GetType().GetProperty("EnabledTasks")?.GetValue(parameter) as System.Collections.Generic.ICollection<TaskType>);

                    return enabledTasks != null && enabledTasks.Contains(taskType);
                }
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This converter is only used for one-way binding
            return System.Windows.Data.Binding.DoNothing;
        }
    }
} 