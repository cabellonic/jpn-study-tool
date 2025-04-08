// Converters/NegatedBooleanConverter.cs
using System;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class NegatedBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return !(value is bool b && b);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return !(value is bool b && b);
    }
}