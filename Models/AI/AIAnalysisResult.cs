// Models/AI/AIAnalysisResult.cs
using System.Collections.Generic;

namespace JpnStudyTool.Models.AI;

public class AIAnalysisResult
{
    public AITranslation? FullTranslation { get; set; }
    public List<AITokenInfo> Tokens { get; set; } = new List<AITokenInfo>();
}