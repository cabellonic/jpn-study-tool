// Converters/NullToVisibilityConverter.cs
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isVisible = value != null;
        if (parameter is string paramString && paramString.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}