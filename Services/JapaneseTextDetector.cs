// Services/JapaneseTextDetector.cs
using System.Text.RegularExpressions;

namespace JpnStudyTool.Services;

public static class JapaneseTextDetector
{
    // Regex to detect japanese chars in a string text
    private static readonly Regex JapaneseRegex = new Regex(
        @"[\u3040-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\u3001\u3002\uFF0C\uFF0E]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool ContainsJapanese(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        return JapaneseRegex.IsMatch(text);
    }
}