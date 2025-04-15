// ViewModels/MainWindowViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;

namespace JpnStudyTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<SentenceHistoryItem> SentenceHistory { get; } = new();

    [ObservableProperty]
    private string _currentViewInternal = "list";

    [ObservableProperty]
    private string _previousViewInternal = "list";

    [ObservableProperty]
    private int selectedSentenceIndex = -1;

    [ObservableProperty]
    private SentenceHistoryItem? selectedSentenceItem;

    public ICommand OpenSettingsCommand { get; }

    private readonly ClipboardService _clipboardService;
    private readonly DispatcherQueue _dispatcherQueue;

    public MainWindowViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _clipboardService = new ClipboardService();
        _clipboardService.JapaneseTextCopied += OnJapaneseTextCopied;
        _clipboardService.StartMonitoring();
    }

    partial void OnSelectedSentenceIndexChanged(int value)
    {
        if (value >= 0 && value < SentenceHistory.Count)
        {
            SelectedSentenceItem = SentenceHistory[value];
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Index changed to: {value}, Item: {SelectedSentenceItem?.Sentence.Substring(0, Math.Min(20, SelectedSentenceItem.Sentence.Length))}...");
        }
        else
        {
            SelectedSentenceItem = null;
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Index changed to: {value} (No selection)");
        }
    }

    private void OnJapaneseTextCopied(object? sender, string text)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var newItem = new SentenceHistoryItem(text);
            SentenceHistory.Add(newItem);
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Added: {text.Substring(0, Math.Min(50, text.Length))}...");
            SelectedSentenceIndex = SentenceHistory.Count - 1;
        });
    }

    public void MoveSelection(int delta)
    {
        if (SentenceHistory.Count == 0) return;
        int newIndex = SelectedSentenceIndex + delta;
        SelectedSentenceIndex = Math.Clamp(newIndex, 0, SentenceHistory.Count - 1);
    }

    public void EnterMenu()
    {
        if (CurrentViewInternal == "menu") return;

        PreviousViewInternal = CurrentViewInternal;
        CurrentViewInternal = "menu";
        reset_navigation_state_internal();
        System.Diagnostics.Debug.WriteLine($"[ViewModel] Entering Menu. Previous view: {PreviousViewInternal}");
    }

    public void ExitMenu()
    {
        if (CurrentViewInternal != "menu") return;

        CurrentViewInternal = PreviousViewInternal;
        reset_navigation_state_internal();
        System.Diagnostics.Debug.WriteLine($"[ViewModel] Exiting Menu. Returning to: {CurrentViewInternal}");
    }

    private void reset_navigation_state_internal()
    {
        System.Diagnostics.Debug.WriteLine("[ViewModel] Navigation state reset requested (needs implementation).");
    }

    public void Cleanup()
    {
        _clipboardService.StopMonitoring();
        _clipboardService.JapaneseTextCopied -= OnJapaneseTextCopied;
    }
}