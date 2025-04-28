// ViewModels/MainWindowViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;

namespace JpnStudyTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ClipboardService _clipboardService;
    private readonly JapaneseTokenizerService _tokenizerService;
    private readonly DictionaryService _dictionaryService;
    private readonly DispatcherQueue _dispatcherQueue;

    public ObservableCollection<SentenceHistoryItem> SentenceHistory { get; } = new();

    [ObservableProperty]
    private int selectedSentenceIndex = -1;

    [ObservableProperty]
    private SentenceHistoryItem? selectedSentenceItem;

    [ObservableProperty]
    private SentenceHistoryItem? _detailedSentence;
    public ObservableCollection<TokenInfo> Tokens { get; } = new();

    [ObservableProperty]
    private int _selectedTokenIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTokenDefinitionEntry))]
    private TokenInfo? _selectedToken;

    [ObservableProperty]
    private string _currentViewInternal = "list";

    [ObservableProperty]
    private string _previousViewInternal = "list";

    [ObservableProperty]
    private string _detailFocusTarget = "Sentence";

    [ObservableProperty]
    private ObservableCollection<DictionaryEntry> _foundDefinitions = new();

    public DictionaryEntry? SelectedTokenDefinitionEntry => FoundDefinitions.FirstOrDefault();

    [ObservableProperty]
    private bool _isSearchingDefinition = false;

    public MainWindowViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _clipboardService = new ClipboardService();
        _tokenizerService = new JapaneseTokenizerService();
        _dictionaryService = new DictionaryService();
        _clipboardService.JapaneseTextCopied += OnJapaneseTextCopied;
        _clipboardService.StartMonitoring();
    }

    partial void OnSelectedSentenceIndexChanged(int value)
    {
        if (value >= 0 && value < SentenceHistory.Count) { SelectedSentenceItem = SentenceHistory[value]; }
        else { SelectedSentenceItem = null; }
        System.Diagnostics.Debug.WriteLine($"[ViewModel] List Index changed to: {value}");
    }


    async partial void OnSelectedTokenChanged(TokenInfo? value)
    {
        _dispatcherQueue.TryEnqueue(() => FoundDefinitions.Clear());
        OnPropertyChanged(nameof(SelectedTokenDefinitionEntry));

        if (value != null && CurrentViewInternal == "detail")
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Selected Token Changed: {value.Surface}. Base='{value.BaseForm}', Reading='{value.Reading}'. Requesting definition search...");
            IsSearchingDefinition = true;
            try
            {
                List<DictionaryEntry> definitions = await _dictionaryService.FindEntriesAsync(value.BaseForm, value.Reading);

                _dispatcherQueue.TryEnqueue(() =>
                {
                    FoundDefinitions.Clear();
                    if (definitions != null && definitions.Any())
                    {
                        foreach (var entry in definitions)
                        {
                            FoundDefinitions.Add(entry);
                        }
                        System.Diagnostics.Debug.WriteLine($"[ViewModel] Found {FoundDefinitions.Count} definitions for '{value.Surface}'. Displaying the first.");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ViewModel] No definitions found for '{value.Surface}'.");
                    }
                    OnPropertyChanged(nameof(SelectedTokenDefinitionEntry));
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ViewModel] Error fetching definitions: {ex.Message}");
                _dispatcherQueue.TryEnqueue(() => FoundDefinitions.Clear());
                OnPropertyChanged(nameof(SelectedTokenDefinitionEntry));
            }
            finally
            {
                IsSearchingDefinition = false;
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Selected Token is null or not in detail view. Definitions cleared.");
        }
    }

    private TokenInfo? FindSelectableTokenByIndex(int selectableIndex)
    {
        if (selectableIndex < 0) return null;
        int currentSelectableCount = -1;
        foreach (var token in Tokens)
        {
            if (token.IsSelectable)
            {
                currentSelectableCount++;
                if (currentSelectableCount == selectableIndex)
                {
                    return token;
                }
            }
        }
        return null;
    }


    public void EnterDetailView()
    {
        if (CurrentViewInternal != "list" || SelectedSentenceItem == null) return;
        System.Diagnostics.Debug.WriteLine($"[ViewModel] Entering Detail View for: {SelectedSentenceItem.Sentence}");
        DetailedSentence = SelectedSentenceItem;
        List<TokenInfo> tokenList = _tokenizerService.Tokenize(DetailedSentence.Sentence);

        _dispatcherQueue.TryEnqueue(() =>
        {
            Tokens.Clear();
            FoundDefinitions.Clear();
            OnPropertyChanged(nameof(SelectedTokenDefinitionEntry));

            int firstValidTokenIndex = -1;
            int currentSelectableIndex = 0;
            for (int i = 0; i < tokenList.Count; i++)
            {
                var token = tokenList[i];
                token.IsSelectable = IsTokenSelectable(token);
                Tokens.Add(token);
                if (firstValidTokenIndex == -1 && token.IsSelectable)
                {
                    firstValidTokenIndex = currentSelectableIndex;
                }
                if (token.IsSelectable)
                {
                    currentSelectableIndex++;
                }
            }
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Tokenized into {Tokens.Count} tokens. Selectable count: {currentSelectableIndex}");

            PreviousViewInternal = CurrentViewInternal;
            CurrentViewInternal = "detail";
            DetailFocusTarget = "Sentence";
            SelectedTokenIndex = firstValidTokenIndex;
        });
    }

    public void ExitDetailView()
    {
        if (CurrentViewInternal != "detail") return;
        System.Diagnostics.Debug.WriteLine($"[ViewModel] Exiting Detail View.");
        int previousListIndex = SelectedSentenceIndex;

        _dispatcherQueue.TryEnqueue(() =>
        {
            DetailedSentence = null;
            Tokens.Clear();
            FoundDefinitions.Clear();
            OnPropertyChanged(nameof(SelectedTokenDefinitionEntry));

            SelectedTokenIndex = -1;
            SelectedToken = null;

            CurrentViewInternal = "list";
            DetailFocusTarget = "Sentence";

            if (previousListIndex >= 0 && previousListIndex < SentenceHistory.Count)
            {
                SelectedSentenceIndex = previousListIndex;
                SelectedSentenceItem = SentenceHistory[previousListIndex];
                System.Diagnostics.Debug.WriteLine($"[ViewModel] Re-selected sentence at index {previousListIndex}.");
            }
            else
            {
                SelectedSentenceIndex = -1;
                SelectedSentenceItem = null;
            }
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Returned to view: {CurrentViewInternal}");
        });
    }

    public void FocusDefinitionArea()
    {
        if (CurrentViewInternal != "detail" || DetailFocusTarget == "Definition") return;
        DetailFocusTarget = "Definition";
        System.Diagnostics.Debug.WriteLine("[ViewModel] Detail focus changed to: Definition");
    }

    public void FocusSentenceArea()
    {
        if (CurrentViewInternal != "detail" || DetailFocusTarget == "Sentence") return;
        DetailFocusTarget = "Sentence";
        System.Diagnostics.Debug.WriteLine("[ViewModel] Detail focus changed to: Sentence");
    }

    public void MoveTokenSelection(int delta)
    {
        if (CurrentViewInternal != "detail" || !Tokens.Any() || DetailFocusTarget != "Sentence") return;
        int currentSelectableIndex = SelectedTokenIndex;
        int numSelectable = Tokens.Count(t => t.IsSelectable);
        if (numSelectable == 0) return;
        int nextSelectableIndex = Math.Clamp(currentSelectableIndex + delta, 0, numSelectable - 1);
        if (nextSelectableIndex != currentSelectableIndex)
        {
            SelectedTokenIndex = nextSelectableIndex;
            SelectedToken = FindSelectableTokenByIndex(nextSelectableIndex);
        }
    }

    public void MoveKanjiTokenSelection(int delta)
    {
        if (CurrentViewInternal != "detail" || !Tokens.Any() || DetailFocusTarget != "Sentence") return;
        int currentSelectableIndex = SelectedTokenIndex;
        int numSelectable = Tokens.Count(t => t.IsSelectable);
        if (numSelectable == 0) return;
        int direction = Math.Sign(delta);
        if (direction == 0) return;

        int searchIndex = currentSelectableIndex;
        while (true)
        {
            searchIndex += direction;
            if (searchIndex < 0 || searchIndex >= numSelectable) break;

            TokenInfo? nextToken = FindSelectableTokenByIndex(searchIndex);
            if (nextToken != null && nextToken.HasKanji)
            {
                SelectedTokenIndex = searchIndex;
                SelectedToken = nextToken;
                break;
            }
        }
    }

    private bool IsTokenSelectable(TokenInfo token)
    {
        if (string.IsNullOrEmpty(token.Surface)) return false;
        const string japanesePattern = @"[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\u3400-\u4DBF]";
        return Regex.IsMatch(token.Surface, japanesePattern);
    }

    public int GetActualTokenIndex(int selectableIndex)
    {
        if (selectableIndex < 0) return -1;
        int currentSelectableCount = -1;
        for (int i = 0; i < Tokens.Count; i++)
        {
            if (Tokens[i].IsSelectable)
            {
                currentSelectableCount++;
                if (currentSelectableCount == selectableIndex)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private void OnJapaneseTextCopied(object? sender, string text)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var newItem = new SentenceHistoryItem(text);
            SentenceHistory.Add(newItem);
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