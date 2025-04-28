// En Converters/IntToVisibilityConverter.cs (o similar)
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isVisible = value is int count && count > 0;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}