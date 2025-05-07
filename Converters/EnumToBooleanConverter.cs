// Converters/EnumToBooleanConverter.cs
using System;
using JpnStudyTool.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter is not string enumString)
            return false;

        if (value == null)
            return false;

        if (value.GetType() != typeof(AiAnalysisTrigger))
            return false;

        try
        {
            var enumParamValue = Enum.Parse(typeof(AiAnalysisTrigger), enumString);
            return value.Equals(enumParamValue);
        }
        catch
        {
            return false;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Value: {value}, TargetType(ignored): {targetType.Name}, Param: {parameter}");

        if (parameter is not string enumString)
        {
            System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Error: Parameter is not a string.");
            return DependencyProperty.UnsetValue;
        }

        if (value is not bool isChecked || !isChecked)
        {
            System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Value is not true boolean. Returning UnsetValue.");
            return DependencyProperty.UnsetValue;
        }

        try
        {
            var result = Enum.Parse(typeof(AiAnalysisTrigger), enumString);
            System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Success: Parsed '{enumString}' to {result}");
            return result;
        }
        catch (ArgumentException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Error parsing enum string '{enumString}': {ex.Message}");
            return DependencyProperty.UnsetValue;
        }
    }
}