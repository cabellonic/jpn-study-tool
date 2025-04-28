// Models/DictionaryEntry.cs
using System.Collections.Generic;
using System.Linq;

namespace JpnStudyTool.Models;

public class DictionaryEntry
{
    public long EntryId { get; set; }
    public string Term { get; set; } = string.Empty;
    public string? Reading { get; set; }
    public int SequenceId { get; set; }
    public double PopularityScore { get; set; }
    public string? DefinitionText { get; set; }
    public string? DefinitionHtml { get; set; }
    public string? InflectionRules { get; set; }


    public List<TagInfo> DefinitionTags { get; set; } = new();
    public List<TagInfo> TermTags { get; set; } = new();


    public string DefinitionTagsDisplay => string.Join(", ", DefinitionTags.Select(t => t.Notes ?? t.TagName));
    public string TermTagsDisplay => string.Join(", ", TermTags.Select(t => t.Notes ?? t.TagName));
}