// Models/AI/AITokenInfo.cs
namespace JpnStudyTool.Models.AI;

public class AITokenInfo
{
    public string Surface { get; set; } = string.Empty;
    public string? Reading { get; set; }
    public string? ContextualMeaning { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? BaseForm { get; set; }
    public int StartIndex { get; set; }
    public int EndIndex { get; set; }
}