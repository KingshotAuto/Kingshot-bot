using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.GUI.Converters
{
    public class ShieldTimeToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TimeSpan timeRemaining)
            {
                if (timeRemaining.TotalHours > 1)
                {
                    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                }
                else if (timeRemaining.TotalMinutes > 10)
                {
                    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0)); // Orange/Yellow
                }
                else
                {
                    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)); // Red
                }
            }
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)); // Gray for null/invalid
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 