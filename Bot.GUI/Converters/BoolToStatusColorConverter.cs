using System;
using System.Globalization;
using System.Windows.Data;
using WPFColor = System.Windows.Media.Color;
using System.Windows.Media;

namespace Bot.GUI.Converters
{
    public class BoolToStatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool isRunning && isRunning 
                ? new SolidColorBrush(WPFColor.FromRgb(76, 175, 80))  // Green for running
                : new SolidColorBrush(WPFColor.FromRgb(244, 67, 54)); // Red for stopped
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 