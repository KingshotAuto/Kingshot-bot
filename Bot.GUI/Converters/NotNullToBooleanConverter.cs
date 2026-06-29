using System;
using System.Globalization;
using System.Windows.Data;

namespace Bot.GUI.Converters
{
    public class NotNullToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This converter is typically used for one-way bindings (e.g., IsEnabled).
            // If ConvertBack is called, it means something is trying to write back, 
            // which is not the intended use for a simple null check to boolean.
            // Returning Binding.DoNothing indicates that the converter cannot convert the value back
            // and the binding engine should not attempt to update the source.
            return System.Windows.Data.Binding.DoNothing;
        }
    }
} 