using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isNull = value == null;
        bool invert = parameter is string paramString && paramString.Equals("invert", StringComparison.OrdinalIgnoreCase);

        bool shouldBeVisible = invert ? isNull : !isNull;

        return shouldBeVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}