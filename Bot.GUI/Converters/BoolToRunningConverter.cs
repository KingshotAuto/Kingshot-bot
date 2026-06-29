using System;
using System.Globalization;
using System.Windows.Data;

namespace Bot.GUI.Converters
{
    public class BoolToRunningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool isRunning && isRunning ? "Running" : "Stopped";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 