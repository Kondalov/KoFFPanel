using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KoFFPanel.Presentation.Converters;

public class BooleanToVisibilityInvertibleConverter : IValueConverter
{
    public Visibility TrueValue { get; set; } = Visibility.Visible;
    public Visibility FalseValue { get; set; } = Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;

        // ИСПРАВЛЕНИЕ: Безопасное сравнение строк, поддержка параметра "Inverted" из XAML
        if (parameter != null && (parameter.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true || parameter.ToString()?.Equals("Inverted", StringComparison.OrdinalIgnoreCase) == true))
        {
            boolValue = !boolValue;
        }

        return boolValue ? TrueValue : FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            bool boolValue = visibility == TrueValue;

            // ИСПРАВЛЕНИЕ: Безопасное сравнение строк, поддержка параметра "Inverted" из XAML
            if (parameter != null && (parameter.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true || parameter.ToString()?.Equals("Inverted", StringComparison.OrdinalIgnoreCase) == true))
            {
                boolValue = !boolValue;
            }

            return boolValue;
        }
        return false;
    }
}