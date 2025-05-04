using System.Text.RegularExpressions;
using JpnStudyTool.Models.AI;

namespace JpnStudyTool.Models;

public class DisplayToken
{
    public string Surface { get; }
    public bool IsSelectable { get; }

    public AITokenInfo? AiData { get; }
    public TokenInfo? MecabData { get; }

    private static readonly Regex PunctuationRegex = new Regex(@"^[\p{P}\p{S}\s]+$");

    public DisplayToken(AITokenInfo aiToken)
    {
        Surface = aiToken.Surface ?? string.Empty;
        IsSelectable = !string.IsNullOrWhiteSpace(Surface) && !PunctuationRegex.IsMatch(Surface);
        AiData = aiToken;
        MecabData = null;
    }

    public DisplayToken(TokenInfo mecabToken)
    {
        Surface = mecabToken.Surface ?? string.Empty;
        IsSelectable = !string.IsNullOrWhiteSpace(Surface) && !PunctuationRegex.IsMatch(Surface);
        AiData = null;
        MecabData = mecabToken;
    }
}