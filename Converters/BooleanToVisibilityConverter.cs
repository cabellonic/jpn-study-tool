// Converters/BooleanToVisibilityConverter.cs
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool boolValue = value is bool b && b;
        string? paramString = parameter as string;

        if (paramString?.Equals("invert", StringComparison.OrdinalIgnoreCase) == true)
        {
            boolValue = !boolValue;
        }

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            bool boolValue = visibility == Visibility.Visible;
            string? paramString = parameter as string;
            if (paramString?.Equals("invert", StringComparison.OrdinalIgnoreCase) == true)
            {
                boolValue = !boolValue;
            }
            return boolValue;
        }
        return DependencyProperty.UnsetValue;
    }
}