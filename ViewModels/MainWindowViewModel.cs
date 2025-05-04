using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JpnStudyTool.Models;
using JpnStudyTool.Models.AI;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;

namespace JpnStudyTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ClipboardService _clipboardService;
    private readonly JapaneseTokenizerService _tokenizerService;
    private readonly DictionaryService _dictionaryService;
    private readonly SettingsService _settingsService;
    private readonly AIService _aiService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<string, AIAnalysisResult> _aiAnalysisCache = new();

    public event Func<List<DisplayToken>, Task>? RequestRenderSentenceWebView;
    public event Func<string?, Task>? RequestLoadDefinitionHtml;
    public event Func<string, Task>? RequestUpdateDefinitionTokenDetails;

    public ObservableCollection<SentenceHistoryItem> SentenceHistory
    {
        get;
    } = new();

    [ObservableProperty] private int selectedSentenceIndex = -1;
    [ObservableProperty] private SentenceHistoryItem? selectedSentenceItem;
    [ObservableProperty] private SentenceHistoryItem? _detailedSentence;
    public ObservableCollection<DisplayToken> DisplayTokens
    {
        get;
    } = new();
    [ObservableProperty] private int _selectedTokenIndex = -1;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(CopyAIAnalysisCommand))] private DisplayToken? _selectedToken;
    [ObservableProperty] private string _currentViewInternal = "list";
    [ObservableProperty] private string _previousViewInternal = "list";
    [ObservableProperty] private string _detailFocusTarget = "Sentence";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanInteractWithDetailView))] private bool _isAnalyzingData = false;
    [ObservableProperty] private bool _isLoadingDefinition = false;
    [ObservableProperty] private string? _apiErrorMessage = null;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(CopyAIAnalysisCommand))] private AIAnalysisResult? _currentSentenceAIAnalysis = null;

    private bool _isAIModeActive = false;
    private string? _activeApiKey = null;
    private bool _needsFullDefinitionLoad = true;
    private bool _isSettingInitialSelection = false;
    public bool CanInteractWithDetailView => !IsAnalyzingData;
    public bool IsAIModeActive => _isAIModeActive;
    public IRelayCommand CopyAIAnalysisCommand { get; }

    public MainWindowViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("Cannot get DispatcherQueue for current thread.");
        _clipboardService = new ClipboardService();
        _tokenizerService = new JapaneseTokenizerService();
        _dictionaryService = new DictionaryService();
        _settingsService = new SettingsService();
        _aiService = new AIService();
        CopyAIAnalysisCommand = new RelayCommand(ExecuteCopyAIAnalysis, CanExecuteCopyAIAnalysis);
        LoadCurrentSettings();
        _clipboardService.JapaneseTextCopied += OnJapaneseTextCopied;
        _clipboardService.StartMonitoring();
    }

    public void LoadCurrentSettings()
    {
        AppSettings currentSettings = _settingsService.LoadSettings();
        bool previousAIMode = _isAIModeActive;
        string? previousApiKey = _activeApiKey;
        _isAIModeActive = currentSettings.UseAIMode;
        _activeApiKey = currentSettings.GeminiApiKey;
        if (previousAIMode != _isAIModeActive || (_isAIModeActive && previousApiKey != _activeApiKey))
        {
            System.Diagnostics.Debug.WriteLine("[ViewModel] Clearing AI Cache due to settings change."); _aiAnalysisCache.Clear(); CurrentSentenceAIAnalysis = null;
        }
        _needsFullDefinitionLoad = true;
        System.Diagnostics.Debug.WriteLine($"[ViewModel] Settings Loaded/Reloaded: AI Mode Active = {_isAIModeActive}, Has API Key = {!string.IsNullOrWhiteSpace(_activeApiKey)}");
        OnPropertyChanged(nameof(IsAIModeActive));
    }

    partial void OnSelectedSentenceIndexChanged(int value)
    {
        if (value >= 0 && value < SentenceHistory.Count)
        {
            SelectedSentenceItem = SentenceHistory[value];
        }
        else
        {
            SelectedSentenceItem = null;
        }
    }
    partial void OnSelectedTokenIndexChanged(int value)
    {
        if (_isSettingInitialSelection) return;
        if (value >= 0 && value < DisplayTokens.Count)
        {
            SelectedToken = DisplayTokens[value];
        }
        else
        {
            SelectedToken = null;
        }
        _ = UpdateDefinitionViewAsync();
    }
    partial void OnSelectedTokenChanged(DisplayToken? value)
    {
        CopyAIAnalysisCommand.NotifyCanExecuteChanged();
    }
    partial void OnCurrentSentenceAIAnalysisChanged(AIAnalysisResult? value)
    {
        CopyAIAnalysisCommand.NotifyCanExecuteChanged();
    }

    private async Task PerformInitialAnalysisAndPopulateAsync()
    {
        await DispatcherQueue_EnqueueAsync(() =>
        {
            IsAnalyzingData = true; ApiErrorMessage = null; DisplayTokens.Clear();
            SelectedTokenIndex = -1; SelectedToken = null; CurrentSentenceAIAnalysis = null;
            _needsFullDefinitionLoad = true;
            _ = RequestLoadDefinitionHtml?.Invoke("<p><i>Loading analysis...</i></p>");
        });

        string currentSentenceText = DetailedSentence?.Sentence ?? string.Empty;
        if (string.IsNullOrEmpty(currentSentenceText))
        {
            await DispatcherQueue_EnqueueAsync(() => IsAnalyzingData = false); if (RequestRenderSentenceWebView != null) await RequestRenderSentenceWebView.Invoke(new List<DisplayToken>()); await UpdateDefinitionViewAsync(); return;
        }

        LoadCurrentSettings();
        AIAnalysisResult? analysisResult = null;
        bool analysisSuccess = false;
        bool useMecabFallback = false;

        if (_isAIModeActive && !string.IsNullOrWhiteSpace(_activeApiKey) && _aiAnalysisCache.TryGetValue(currentSentenceText, out analysisResult))
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Cache Hit for sentence: '{currentSentenceText.Substring(0, Math.Min(20, currentSentenceText.Length))}'"); analysisSuccess = true; await DispatcherQueue_EnqueueAsync(() =>
            {
                CurrentSentenceAIAnalysis = analysisResult; ApiErrorMessage = null;
            });
        }
        else if (_isAIModeActive && !string.IsNullOrWhiteSpace(_activeApiKey))
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Cache Miss. Performing AI Analysis for: '{currentSentenceText.Substring(0, Math.Min(20, currentSentenceText.Length))}'"); analysisResult = await _aiService.AnalyzeSentenceAsync(currentSentenceText, _activeApiKey); if (analysisResult != null && analysisResult.Tokens.Any())
            {
                System.Diagnostics.Debug.WriteLine("[ViewModel] AI Analysis successful."); analysisSuccess = true; _aiAnalysisCache[currentSentenceText] = analysisResult; await DispatcherQueue_EnqueueAsync(() =>
                {
                    CurrentSentenceAIAnalysis = analysisResult; ApiErrorMessage = null;
                });
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[ViewModel] AI Analysis failed. Falling back."); useMecabFallback = true; await DispatcherQueue_EnqueueAsync(() =>
                {
                    CurrentSentenceAIAnalysis = null; ApiErrorMessage = "AI analysis failed. Using offline mode.";
                });
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[ViewModel] AI Mode Off/No Key. Using offline mode."); useMecabFallback = true; await DispatcherQueue_EnqueueAsync(() =>
            {
                CurrentSentenceAIAnalysis = null; if (_isAIModeActive && string.IsNullOrWhiteSpace(_activeApiKey))
                {
                    ApiErrorMessage = "AI Mode active, but API Key is missing.";
                }
                else
                {
                    ApiErrorMessage = null;
                }
            });
        }

        if (analysisSuccess && analysisResult != null)
        {
            await PopulateWithAIDataAsync(analysisResult);
        }
        else
        {
            await PopulateWithMecabDataAsync(currentSentenceText);
        }

        if (RequestRenderSentenceWebView != null)
        {
            await RequestRenderSentenceWebView.Invoke(new List<DisplayToken>(DisplayTokens));
        }

        int firstSelectable = -1;
        await DispatcherQueue_EnqueueAsync(() =>
        {
            firstSelectable = DisplayTokens.ToList().FindIndex(t => t.IsSelectable);


            _isSettingInitialSelection = true;


            SelectedTokenIndex = firstSelectable;
            SelectedToken = (firstSelectable >= 0) ? DisplayTokens[firstSelectable] : null;

            _isSettingInitialSelection = false;


            IsAnalyzingData = false;
        });


        System.Diagnostics.Debug.WriteLine($"[ViewModel] Initial analysis complete. Explicitly calling UpdateDefinitionViewAsync for index: {firstSelectable}");
        await UpdateDefinitionViewAsync();
    }

    private async Task PopulateWithAIDataAsync(AIAnalysisResult analysisResult)
    {
        await DispatcherQueue_EnqueueAsync(() =>
        {
            DisplayTokens.Clear(); foreach (var aiToken in analysisResult.Tokens)
            {
                DisplayTokens.Add(new DisplayToken(aiToken));
            }
        });
    }
    private async Task PopulateWithMecabDataAsync(string sentence)
    {
        List<TokenInfo> mecabTokens = _tokenizerService.Tokenize(sentence); await DispatcherQueue_EnqueueAsync(() =>
        {
            DisplayTokens.Clear(); foreach (var mecabToken in mecabTokens)
            {
                DisplayTokens.Add(new DisplayToken(mecabToken));
            }
        });
    }


    public async Task UpdateDefinitionViewAsync()
    {


        if (CurrentViewInternal != "detail")
        {
            return;
        }

        await DispatcherQueue_EnqueueAsync(() => IsLoadingDefinition = true);

        string? fullHtmlToLoad = null;
        string? tokenDetailSnippet = null;
        DisplayToken? currentSelection = SelectedToken;
        AIAnalysisResult? currentAnalysis = CurrentSentenceAIAnalysis;
        bool isCurrentlyUsingAI = _isAIModeActive && currentAnalysis != null;

        if (isCurrentlyUsingAI)
        {
            if (_needsFullDefinitionLoad)
            {
                fullHtmlToLoad = FormatAIDataForWebView(currentAnalysis!, currentSelection, true); _needsFullDefinitionLoad = false;
            }
            else
            {
                tokenDetailSnippet = FormatAIDataForWebView(currentAnalysis!, currentSelection, false);
            }
        }
        else
        {
            TokenInfo? mecabToken = currentSelection?.MecabData; fullHtmlToLoad = await FetchAndFormatTraditionalAnalysisAsync(mecabToken); _needsFullDefinitionLoad = true; if (!_isAIModeActive)
            {
                await DispatcherQueue_EnqueueAsync(() => ApiErrorMessage = null);
            }
        }

        if (fullHtmlToLoad != null && RequestLoadDefinitionHtml != null)
        {
            await DispatcherQueue_EnqueueAsync(() => RequestLoadDefinitionHtml.Invoke(fullHtmlToLoad));
        }
        else if (tokenDetailSnippet != null && RequestUpdateDefinitionTokenDetails != null)
        {
            await DispatcherQueue_EnqueueAsync(() => RequestUpdateDefinitionTokenDetails.Invoke(tokenDetailSnippet));
        }
        else if (RequestLoadDefinitionHtml != null)
        {
            await DispatcherQueue_EnqueueAsync(() => RequestLoadDefinitionHtml.Invoke(FormatTraditionalDataForWebView(null, null)));
        }

        await DispatcherQueue_EnqueueAsync(() => IsLoadingDefinition = false);
    }

    private async Task<string?> FetchAndFormatTraditionalAnalysisAsync(TokenInfo? mecabToken)
    {
        if (mecabToken == null) return FormatTraditionalDataForWebView(null, null); List<DictionaryEntry> definitions = new List<DictionaryEntry>(); string? formattedHtml = null; try
        {
            definitions = await _dictionaryService.FindEntriesAsync(mecabToken.BaseForm, mecabToken.Reading); formattedHtml = FormatTraditionalDataForWebView(definitions, mecabToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Error fetching traditional definitions: {ex.Message}"); formattedHtml = "<p>Error loading offline definitions.</p>";
        }
        return formattedHtml;
    }
    private string FormatAIDataForWebView(AIAnalysisResult aiResult, DisplayToken? currentSelection, bool includeWrapperAndTranslation)
    {
        StringBuilder htmlBuilder = new StringBuilder(); if (includeWrapperAndTranslation)
        {
            htmlBuilder.Append($@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"); htmlBuilder.Append(@"html, body { 
        height: 100%; margin: 0; padding: 0; box-sizing: border-box; overflow: hidden; font-family: 'Segoe UI', sans-serif; line-height: 1.6; margin: 5px; background-color: transparent; color: var(--text-primary-color, black); -webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; overflow-y: auto; scrollbar-width: none; -ms-overflow-style: none; } "); htmlBuilder.Append(@"body::-webkit-scrollbar { 
        display: none; } "); htmlBuilder.Append(@".section-header { 
        font-weight: bold; margin-top: 0.8em; padding-bottom: 0.2em; border-bottom: 1px solid #eee; font-size: 1.1em; } "); htmlBuilder.Append(@".translation { 
        margin-top: 0.5em; padding: 8px; background-color: rgba(0, 120, 215, 0.05); border-left: 3px solid rgba(0, 120, 215, 0.5); border-radius: 3px; } "); htmlBuilder.Append(@".token-info { 
        margin-top: 0.5em; } "); htmlBuilder.Append(@".token-surface { 
        font-weight: bold; font-size: 1.2em; } "); htmlBuilder.Append(@".token-reading { 
        color: gray; margin-left: 5px; } "); htmlBuilder.Append(@".token-meaning { 
        margin-top: 5px; } "); htmlBuilder.Append(@".token-pos { 
        font-style: italic; color: #555; margin-top: 3px; font-size: 0.9em; } "); htmlBuilder.Append(@"</style></head><body id='definition-body'>"); if (aiResult.FullTranslation != null)
            {
                htmlBuilder.Append("<div id='full-translation-div'>"); htmlBuilder.Append("<div class='section-header'>Full Translation</div>"); htmlBuilder.Append($"<div class='translation'>{System.Security.SecurityElement.Escape(aiResult.FullTranslation.Text)}</div>"); htmlBuilder.Append("</div>");
            }
            htmlBuilder.Append("<div class='section-header'>Selected Token Analysis</div>"); htmlBuilder.Append("<div id='selected-token-div'>");
        }
        AITokenInfo? selectedAiToken = currentSelection?.AiData; if (selectedAiToken != null)
        {
            htmlBuilder.Append("<div class='token-info'>"); htmlBuilder.Append($"<span class='token-surface'>{System.Security.SecurityElement.Escape(selectedAiToken.Surface)}</span>"); if (!string.IsNullOrWhiteSpace(selectedAiToken.Reading))
            {
                htmlBuilder.Append($"<span class='token-reading'>[{System.Security.SecurityElement.Escape(selectedAiToken.Reading)}]</span>");
            }
            if (!string.IsNullOrWhiteSpace(selectedAiToken.ContextualMeaning))
            {
                htmlBuilder.Append($"<div class='token-meaning'>{System.Security.SecurityElement.Escape(selectedAiToken.ContextualMeaning)}</div>");
            }
            if (!string.IsNullOrWhiteSpace(selectedAiToken.PartOfSpeech))
            {
                htmlBuilder.Append($"<div class='token-pos'>({System.Security.SecurityElement.Escape(selectedAiToken.PartOfSpeech)}"); if (!string.IsNullOrWhiteSpace(selectedAiToken.BaseForm) && selectedAiToken.BaseForm != selectedAiToken.Surface)
                {
                    htmlBuilder.Append($", Base: {System.Security.SecurityElement.Escape(selectedAiToken.BaseForm)}");
                }
                htmlBuilder.Append(")"); htmlBuilder.Append("</div>");
            }
            htmlBuilder.Append("</div>");
        }
        else if (currentSelection != null)
        {
            htmlBuilder.Append($"<p><i>Select a Japanese token.</i></p>");
        }
        else
        {
            htmlBuilder.Append("<p><i>No token selected.</i></p>");
        }
        if (includeWrapperAndTranslation)
        {
            htmlBuilder.Append("</div>"); htmlBuilder.Append(@"<script> function updateTokenDetails(tokenHtml) { 
        try { 
        const targetDiv = document.getElementById('selected-token-div'); if (targetDiv) { 
        targetDiv.innerHTML = tokenHtml; console.log('Updated token details via JS.');} else { 
        console.error('Target div selected-token-div not found!');} } catch(e) { 
        console.error('JS Error updateTokenDetails:', e); } } function initializeNavigation() { 
        return 0; } function highlightListItem(index) { 
        return false; } function isCurrentListItemInternalLink(index) { 
        return false; } function scrollDefinition(delta) { 
        try { 
        document.body.scrollTop += delta; } catch(e) {} } function setDefinitionFocus(isFocused) { 
        
    } </script>"); htmlBuilder.Append("</body></html>");
        }
        return htmlBuilder.ToString();
    }
    private string FormatTraditionalDataForWebView(List<DictionaryEntry>? definitions, TokenInfo? mecabToken)
    {
        StringBuilder htmlBuilder = new StringBuilder(); htmlBuilder.Append($@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"); htmlBuilder.Append(@"html, body { 
        height: 100%; margin: 0; padding: 0; box-sizing: border-box; overflow: hidden; } "); htmlBuilder.Append(@"body { 
        "); htmlBuilder.Append($"font-family: 'Segoe UI', sans-serif; font-size: {DefinitionFontSize}px; line-height: 1.6; margin: 5px; padding: 0; "); htmlBuilder.Append(@"height: 100%; "); htmlBuilder.Append(@"background-color: transparent; color: var(--text-primary-color, black); "); htmlBuilder.Append(@"-webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; "); htmlBuilder.Append(@"overflow-y: auto; "); htmlBuilder.Append(@"scrollbar-width: none; "); htmlBuilder.Append(@"-ms-overflow-style: none; "); htmlBuilder.Append(@"} "); htmlBuilder.Append(@"body::-webkit-scrollbar { 
        display: none; } "); htmlBuilder.Append(@"ul { 
        padding-left: 20px; margin-top: 0.5em; margin-bottom: 0.5em; list-style: disc; } "); htmlBuilder.Append(@"li { 
        margin-bottom: 0.2em; } "); htmlBuilder.Append(@"div[data-content='notes'] ul, div[data-content='examples'] ul { 
        margin-top: 0.2em; list-style: none; padding-left: 5px; } "); htmlBuilder.Append(@"div[data-content='references'] ul li span[data-content='refGlosses'] { 
        font-size: 0.9em; color: var(--text-secondary-color, gray); margin-left: 5px; } "); htmlBuilder.Append(@"span.internal-link { 
        color: var(--system-accent-color, blue); cursor: pointer; text-decoration: underline; } "); htmlBuilder.Append(@"li.focusable-li { 
        outline: none; } "); htmlBuilder.Append(@"li.current-nav-item { 
        background-color: rgba(0, 120, 215, 0.1); outline: 1px dashed rgba(0, 120, 215, 0.3); border-radius: 3px; } "); htmlBuilder.Append(@"body.definition-focused li.current-nav-item { 
        background-color: rgba(0, 120, 215, 0.2); outline: 1px solid rgba(0, 120, 215, 0.5); } "); htmlBuilder.Append(@".definition-entry-header { 
        font-weight: bold; margin-top: 0.8em; padding-bottom: 0.2em; border-bottom: 1px solid #eee; } "); htmlBuilder.Append(@"hr { 
        border: none; border-top: 1px dashed #ccc; margin: 1em 0; } "); htmlBuilder.Append(@"</style></head><body id='definition-body'>"); if (mecabToken != null)
        {
            string term = System.Security.SecurityElement.Escape(mecabToken.Surface); string headerTerm = (!string.IsNullOrEmpty(mecabToken.BaseForm) && mecabToken.BaseForm != mecabToken.Surface) ? System.Security.SecurityElement.Escape(mecabToken.BaseForm) : term; string reading = System.Security.SecurityElement.Escape(Converters.KatakanaToHiraganaConverter.ToHiraganaStatic(mecabToken.Reading)); htmlBuilder.Append($"<div class='definition-entry-header'>{headerTerm} [{reading}]</div>");
        }
        if (definitions != null && definitions.Any())
        {
            for (int i = 0; i < definitions.Count; i++)
            {
                var entry = definitions[i]; if (i > 0)
                {
                    htmlBuilder.Append("<hr />");
                }
                if (!string.IsNullOrWhiteSpace(entry.DefinitionHtml))
                {
                    htmlBuilder.Append(entry.DefinitionHtml);
                }
                else if (!string.IsNullOrWhiteSpace(entry.DefinitionText))
                {
                    htmlBuilder.Append($"<p>{System.Security.SecurityElement.Escape(entry.DefinitionText)}</p>");
                }
            }
        }
        else if (mecabToken != null)
        {
            htmlBuilder.Append("<p><i>No offline definitions found.</i></p>");
        }
        else
        {
            htmlBuilder.Append("<p><i>Select a token.</i></p>");
        }
        htmlBuilder.Append(@"<script> let focusableItems = []; let currentHighlightIndex = -1; function initializeNavigation() { 
        try { 
        focusableItems = Array.from(document.querySelectorAll('li')); focusableItems.forEach(item => item.classList.add('focusable-li')); currentHighlightIndex = -1; return focusableItems.length; } catch(e) { 
        return 0; } } function highlightListItem(index) { 
        try { 
        if (currentHighlightIndex >= 0 && currentHighlightIndex < focusableItems.length) { 
        focusableItems[currentHighlightIndex].classList.remove('current-nav-item'); } if (index >= 0 && index < focusableItems.length) { 
        const newItem = focusableItems[index]; newItem.classList.add('current-nav-item'); newItem.scrollIntoView({ 
        behavior: 'smooth', block: 'nearest', inline: 'nearest' }); currentHighlightIndex = index; return newItem.querySelector('span.internal-link') !== null; } else { 
        currentHighlightIndex = -1; return false; } } catch(e) { 
        return false;} } function isCurrentListItemInternalLink(index) { 
        try { 
        if (index >= 0 && index < focusableItems.length) { 
        return focusableItems[index].querySelector('span.internal-link') !== null; } return false; } catch(e) { 
        return false;} } function scrollDefinition(delta) { 
        try { 
        document.body.scrollTop += delta; } catch(e) {} } function setDefinitionFocus(isFocused) { 
        try { 
        const body = document.getElementById('definition-body'); if(isFocused) { 
        body.classList.add('definition-focused'); } else { 
        body.classList.remove('definition-focused'); } } catch(e) {} } </script></body></html>"); return htmlBuilder.ToString();
    }

    public async void EnterDetailView()
    {
        if (CurrentViewInternal == "detail" || SelectedSentenceItem == null) return; DetailedSentence = SelectedSentenceItem; await DispatcherQueue_EnqueueAsync(() =>
        {
            DisplayTokens.Clear(); SelectedToken = null; SelectedTokenIndex = -1; IsAnalyzingData = true; IsLoadingDefinition = false; ApiErrorMessage = null; _needsFullDefinitionLoad = true; PreviousViewInternal = CurrentViewInternal; CurrentViewInternal = "detail"; DetailFocusTarget = "Sentence";
        }); await PerformInitialAnalysisAndPopulateAsync();
    }
    public async void ExitDetailView()
    {
        if (CurrentViewInternal != "detail") return; int previousListIndex = SelectedSentenceIndex; await DispatcherQueue_EnqueueAsync(() =>
        {
            DetailedSentence = null; DisplayTokens.Clear(); SelectedToken = null; SelectedTokenIndex = -1; IsAnalyzingData = false; IsLoadingDefinition = false; ApiErrorMessage = null; CurrentSentenceAIAnalysis = null; CurrentViewInternal = "list"; DetailFocusTarget = "Sentence"; if (previousListIndex >= 0 && previousListIndex < SentenceHistory.Count)
            {
                SelectedSentenceIndex = previousListIndex;
            }
            else
            {
                SelectedSentenceIndex = -1;
            }
        }); if (RequestRenderSentenceWebView != null) await RequestRenderSentenceWebView.Invoke(new List<DisplayToken>()); if (RequestLoadDefinitionHtml != null) await RequestLoadDefinitionHtml.Invoke(null);
    }
    public async void FocusDefinitionArea()
    {
        if (CurrentViewInternal != "detail" || DetailFocusTarget == "Definition" || IsAnalyzingData) return; DetailFocusTarget = "Definition"; _needsFullDefinitionLoad = true; await UpdateDefinitionViewAsync();
    }
    public async void FocusSentenceArea()
    {
        if (CurrentViewInternal != "detail" || DetailFocusTarget == "Sentence" || IsAnalyzingData) return; DetailFocusTarget = "Sentence"; _needsFullDefinitionLoad = true; await UpdateDefinitionViewAsync();
    }

    public void MoveTokenSelection(int delta)
    {
        if (!CanInteractWithDetailView || CurrentViewInternal != "detail" || !DisplayTokens.Any() || DetailFocusTarget != "Sentence") return; int currentSelectableIndex = SelectedTokenIndex; int numTokens = DisplayTokens.Count; if (numTokens == 0) return; int nextIndex = currentSelectableIndex; int Bumper = numTokens * 2; int count = 0; do
        {
            nextIndex += delta; count++; if (nextIndex < 0 || nextIndex >= numTokens)
            {
                nextIndex = currentSelectableIndex; break;
            }
            if (DisplayTokens[nextIndex].IsSelectable)
            {
                break;
            }
        } while (count < Bumper); if (count >= Bumper || (nextIndex >= 0 && nextIndex < numTokens && !DisplayTokens[nextIndex].IsSelectable))
        {
            nextIndex = currentSelectableIndex;
        }
        if (nextIndex >= 0 && nextIndex < numTokens && nextIndex != currentSelectableIndex)
        {
            SelectedTokenIndex = nextIndex;
        }
    }
    public void MoveKanjiTokenSelection(int delta)
    {
        if (!CanInteractWithDetailView || CurrentViewInternal != "detail" || !DisplayTokens.Any() || DetailFocusTarget != "Sentence") return; int currentSelectableIndex = SelectedTokenIndex; int numTokens = DisplayTokens.Count; if (numTokens <= 1) return; int direction = Math.Sign(delta); if (direction == 0) return; int searchIndex = currentSelectableIndex; int Bumper = numTokens * 2; int count = 0; while (count < Bumper)
        {
            searchIndex += direction; if (searchIndex < 0 || searchIndex >= numTokens) break; bool hasKanji = false; if (DisplayTokens[searchIndex].AiData != null)
            {
                hasKanji = DisplayTokens[searchIndex].Surface.Any(c => c >= 0x4E00 && c <= 0x9FAF);
            }
            else if (DisplayTokens[searchIndex].MecabData != null)
            {
                hasKanji = DisplayTokens[searchIndex].MecabData?.HasKanji ?? false;
            }
            if (DisplayTokens[searchIndex].IsSelectable && hasKanji)
            {
                SelectedTokenIndex = searchIndex; return;
            }
            count++;
        }
    }

    private bool CanExecuteCopyAIAnalysis()
    {
        return CurrentSentenceAIAnalysis != null;
    }
    private void ExecuteCopyAIAnalysis()
    {
        if (CurrentSentenceAIAnalysis == null) return; try
        {
            string jsonToCopy = JsonSerializer.Serialize(CurrentSentenceAIAnalysis, new JsonSerializerOptions
            {
                WriteIndented = true
            }); ClipboardService.SetClipboardTextWithIgnore(jsonToCopy);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] Failed to copy AI analysis: {ex.Message}");
        }
    }

    private void OnJapaneseTextCopied(object? sender, string text)
    {
        var newItem = new SentenceHistoryItem(text); _dispatcherQueue.TryEnqueue(() =>
        {
            SentenceHistory.Add(newItem);
        }); _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (SentenceHistory.Count > 0)
            {
                SelectedSentenceIndex = SentenceHistory.Count - 1;
            }
        });
    }
    public void MoveSelection(int delta)
    {
        if (SentenceHistory.Count == 0) return; int newIndex = SelectedSentenceIndex + delta; SelectedSentenceIndex = Math.Clamp(newIndex, 0, SentenceHistory.Count - 1);
    }
    public void EnterMenu()
    {
        /* Placeholder */
    }
    public void ExitMenu()
    {
        /* Placeholder */
    }
    private void reset_navigation_state_internal()
    {
        /* Placeholder */
    }
    public void Cleanup()
    {
        _clipboardService.StopMonitoring(); _clipboardService.JapaneseTextCopied -= OnJapaneseTextCopied; RequestLoadDefinitionHtml = null; RequestRenderSentenceWebView = null; RequestUpdateDefinitionTokenDetails = null;
    }

    private const double DEFAULT_DEFINITION_FONT_SIZE = 18; public double DefinitionFontSize => DEFAULT_DEFINITION_FONT_SIZE;
    private Task DispatcherQueue_EnqueueAsync(Action function, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource(); if (!_dispatcherQueue.TryEnqueue(priority, () =>
        {
            try
            {
                function(); tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue action."));
        }
        return tcs.Task;
    }
    private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource(); if (!_dispatcherQueue.TryEnqueue(priority, async () =>
        {
            try
            {
                await asyncFunction(); tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue async action."));
        }
        return tcs.Task;
    }

    public void ForceFullDefinitionReload()
    {
        _needsFullDefinitionLoad = true;
    }
}