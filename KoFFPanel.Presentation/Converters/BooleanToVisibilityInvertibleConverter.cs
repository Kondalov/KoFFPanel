using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KoFFPanel.Presentation.Converters;

public class BooleanToVisibilityInvertibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isTrue = (value is bool booleanValue) && booleanValue;

        // ???? ??????? ???????? Inverted, ?????? ?????? ?? ???????????????
        if (parameter?.ToString()?.Equals("Inverted", StringComparison.OrdinalIgnoreCase) == true)
        {
            isTrue = !isTrue;
        }

        return isTrue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
