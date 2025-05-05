// Converters/EnumToBooleanConverter.cs
using System;
using JpnStudyTool.Models; // IMPORTANTE: Añadir using para AiAnalysisTrigger
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // Convert sigue igual
        if (parameter is not string enumString)
            return false;

        if (value == null)
            return false;

        // Comprobar si el valor es del tipo correcto antes de comparar
        if (value.GetType() != typeof(AiAnalysisTrigger))
            return false;

        // Parsear el parámetro al enum para comparar
        try
        {
            var enumParamValue = Enum.Parse(typeof(AiAnalysisTrigger), enumString);
            return value.Equals(enumParamValue);
        }
        catch
        {
            return false; // Si el parámetro es inválido
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // targetType es ignorado aquí, usamos directamente typeof(AiAnalysisTrigger)
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
            // USA typeof(AiAnalysisTrigger) DIRECTAMENTE
            var result = Enum.Parse(typeof(AiAnalysisTrigger), enumString);
            System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Success: Parsed '{enumString}' to {result}");
            return result;
        }
        catch (ArgumentException ex)
        {
            // Ahora el error sería si enumString es inválido
            System.Diagnostics.Debug.WriteLine($"[EnumConv.ConvertBack] Error parsing enum string '{enumString}': {ex.Message}");
            return DependencyProperty.UnsetValue;
        }
    }
}