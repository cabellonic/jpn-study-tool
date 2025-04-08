// Models/SentenceHistoryItem.cs
using System;

namespace JpnStudyTool.Models;

public class SentenceHistoryItem
{
    public string Sentence { get; }
    public DateTime Timestamp { get; }

    public SentenceHistoryItem(string sentence)
    {
        Sentence = sentence ?? string.Empty;
        Timestamp = DateTime.Now;
    }

    public override string ToString()
    {
        return Sentence;
    }
}