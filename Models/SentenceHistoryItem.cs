// Models/SentenceHistoryItem.cs
using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JpnStudyTool.Models;

public partial class SentenceHistoryItem : ObservableObject
{
    public string Sentence { get; }
    public DateTime Timestamp { get; }

    [ObservableProperty]
    private bool _isAiAnalysisCached;

    [ObservableProperty]
    private bool _isAiAnalysisLoading;

    public SentenceHistoryItem(string sentence)
    {
        Sentence = sentence ?? string.Empty;
        Timestamp = DateTime.Now;
        _isAiAnalysisCached = false;
        _isAiAnalysisLoading = false;
    }

    public override string ToString()
    {
        return Sentence;
    }

    public void SetLoadingState(bool isLoading)
    {
        IsAiAnalysisLoading = isLoading;
        if (isLoading)
        {
            IsAiAnalysisCached = false;
        }
    }
}