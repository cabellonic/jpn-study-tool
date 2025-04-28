using System;
using System.Text;
using Microsoft.UI.Xaml.Data;

namespace JpnStudyTool.Converters;

public class KatakanaToHiraganaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string katakanaString && !string.IsNullOrEmpty(katakanaString))
        {
            return ToHiraganaStatic(katakanaString);
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }

    public static string ToHiraganaStatic(string? katakana)
    {
        if (string.IsNullOrEmpty(katakana)) return katakana ?? string.Empty;
        StringBuilder hiragana = new StringBuilder(katakana.Length);
        foreach (char c in katakana)
        {
            if (c >= '\u30A1' && c <= '\u30F6') { hiragana.Append((char)(c - 0x60)); }
            else if (c == '\u30FD') { hiragana.Append('\u307E'); }
            else if (c == '\u30FE') { hiragana.Append('\u307F'); }
            else if (c == '\u30FC' || c == '\u30FB' || c == '\u3099' || c == '\u309A') { hiragana.Append(c); }
            else { hiragana.Append(c); }
        }
        return hiragana.ToString();
    }
}