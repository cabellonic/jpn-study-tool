// Converters/BoolToBrushConverter.cs
using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JpnStudyTool.Converters
{
    public class BoolToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ActiveSessionBrush = new SolidColorBrush(Colors.DarkGreen);
        private static readonly SolidColorBrush InactiveSessionBrush = Application.Current.Resources["TextFillColorSecondaryBrush"] as SolidColorBrush ?? new SolidColorBrush(Colors.Gray);

        private static readonly SolidColorBrush HighlightBrush = new SolidColorBrush(Colors.Yellow);
        private static readonly SolidColorBrush TransparentBrush = new SolidColorBrush(Colors.Transparent);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isTrue = value is bool b && b;
            string? param = parameter as string;

            if (param == "Highlight")
            {
                return isTrue ? HighlightBrush : TransparentBrush;
            }
            else if (param == "ActiveSessionText")
            {
                return isTrue ? ActiveSessionBrush : InactiveSessionBrush;
            }
            return isTrue ? ActiveSessionBrush : InactiveSessionBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}