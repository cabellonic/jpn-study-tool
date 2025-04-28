// Models/TokenInfo.cs
namespace JpnStudyTool.Models;

public class TokenInfo
{
    public string Surface { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public string POSSubcategory1 { get; set; } = string.Empty;
    public string POSSubcategory2 { get; set; } = string.Empty;
    public string ConjugationType { get; set; } = string.Empty;
    public string ConjugationForm { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public string BaseForm { get; set; } = string.Empty;
    public string Pronunciation { get; set; } = string.Empty;
    public bool HasKanji { get; set; }
    public bool IsSelectable { get; set; }
}