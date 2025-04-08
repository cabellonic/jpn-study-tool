// ViewModels/MainWindowViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;
using System.Linq;
using System;

namespace JpnStudyTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    // Data binding for the sentence history ListView
    public ObservableCollection<SentenceHistoryItem> SentenceHistory { get; } = new();

    private readonly ClipboardService _clipboardService;
    private readonly DispatcherQueue _dispatcherQueue; // For UI thread updates

    public MainWindowViewModel()
    {
        // Must get the dispatcher for the UI thread this ViewModel is created on
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _clipboardService = new ClipboardService();
        _clipboardService.JapaneseTextCopied += OnJapaneseTextCopied;
        _clipboardService.StartMonitoring();
    }

    private void OnJapaneseTextCopied(object? sender, string text)
    {
        // Ensure collection modification happens on the UI thread
        _dispatcherQueue.TryEnqueue(() =>
        {
            var newItem = new SentenceHistoryItem(text);
            SentenceHistory.Add(newItem);
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Added: {text.Substring(0, Math.Min(50, text.Length))}...");
        });
    }

    // Called when the associated window is closing
    public void Cleanup()
    {
        _clipboardService.StopMonitoring();
        _clipboardService.JapaneseTextCopied -= OnJapaneseTextCopied;
    }

}