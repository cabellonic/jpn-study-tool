// Converters/StringEqualsTriggerConverter.cs
using System;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class StringEqualsTriggerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string? stringValue = value as string;
        string? compareTo = parameter as string;
        bool result = stringValue?.Equals(compareTo, StringComparison.OrdinalIgnoreCase) ?? false;
        System.Diagnostics.Debug.WriteLine($"[Converter] StringEquals: Value='{stringValue ?? "null"}', Param='{compareTo ?? "null"}', Result={result}");
        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}