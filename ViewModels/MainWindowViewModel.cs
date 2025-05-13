// ViewModels/MainWindowViewModel.cs
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
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
namespace JpnStudyTool.ViewModels
{
    public partial class SessionEntryDisplayItem : ObservableObject
    {
        public long EntryId { get; set; }
        public string Sentence { get; }
        public DateTime TimestampCaptured { get; }
        [ObservableProperty]
        private bool _isAiAnalysisCached;
        [ObservableProperty]
        private bool _isAiAnalysisLoading;
        public SessionEntryDisplayItem(SessionEntryInfo entryInfo)
        {
            EntryId = entryInfo.EntryId;
            Sentence = entryInfo.SentenceText;
            TimestampCaptured = DateTime.TryParse(entryInfo.TimestampCaptured, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.MinValue;
            _isAiAnalysisCached = entryInfo.IsAiAnalysisCachedAtSave;
            _isAiAnalysisLoading = false;
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

    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly ClipboardService _clipboardService;
        private readonly JapaneseTokenizerService _tokenizerService;
        private readonly DictionaryService _dictionaryService;
        private readonly SettingsService _settingsService;
        private readonly AIService _aiService;
        private readonly SessionManagerService _sessionManager;
        private readonly DatabaseService _dbService;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Dictionary<string, AIAnalysisResult> _aiAnalysisCache = new();
        public event Func<List<DisplayToken>, Task>? RequestRenderSentenceWebView;
        public event Func<string?, Task>? RequestLoadDefinitionHtml;
        public event Func<string, Task>? RequestUpdateDefinitionTokenDetails;
        public ObservableCollection<SessionEntryDisplayItem> CurrentSessionEntries { get; } = new();
        public ObservableCollection<DisplayToken> DisplayTokens { get; } = new();
        [ObservableProperty] private int selectedSentenceIndex = -1;
        [ObservableProperty] private SessionEntryDisplayItem? selectedSentenceItem;
        [ObservableProperty] private SessionEntryDisplayItem? _detailedSentenceEntry;
        [ObservableProperty] private int _selectedTokenIndex = -1;
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(CopyAIAnalysisCommand))] private DisplayToken? _selectedToken;
        [ObservableProperty]
        private bool _isHubViewActive = true;
        [ObservableProperty]
        private bool _isDetailViewActive = false;
        public bool IsSessionViewActive => !IsHubViewActive;
        [ObservableProperty] private string _detailFocusTarget = "Sentence";
        [ObservableProperty][NotifyPropertyChangedFor(nameof(CanInteractWithDetailView))] private bool _isAnalyzingData = false;
        [ObservableProperty] private bool _isLoadingDefinition = false;
        [ObservableProperty] private string? _apiErrorMessage = null;
        [ObservableProperty] private bool _isLoading = false;
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(CopyAIAnalysisCommand))] private AIAnalysisResult? _currentSentenceAIAnalysis = null;
        [ObservableProperty] private int _globalTokensToday = 0;
        [ObservableProperty] private int _globalTokensLast30 = 0;
        [ObservableProperty] private int _globalTokensTotal = 0;
        private bool _isAIModeActive = false;
        private string? _activeApiKey = null;
        private AiAnalysisTrigger _loadedAiTriggerMode = AiAnalysisTrigger.OnDemand;
        private bool _needsFullDefinitionLoad = true;
        private bool _isSettingInitialSelection = false;
        public bool CanInteractWithDetailView => !IsAnalyzingData;
        public bool IsAIModeActive => _isAIModeActive;
        public IRelayCommand CopyAIAnalysisCommand { get; }
        public IAsyncRelayCommand GoBackToHubCommand { get; }

        [ObservableProperty]
        private string? _viewingSessionId;
        [ObservableProperty]
        private string _viewingSessionName = "Session History";
        [ObservableProperty]
        private BitmapImage? _viewingSessionImageSource;
        [ObservableProperty]
        private string _viewingSessionStartTimeDisplay = "Start time: N/A";
        [ObservableProperty]
        private string _viewingSessionTokensUsedDisplay = "Tokens: N/A";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartThisSession))]
        [NotifyPropertyChangedFor(nameof(CanEndThisSession))]
        [NotifyPropertyChangedFor(nameof(CanGoToActiveSession))]
        private bool _isViewingSessionTheActiveSession;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanGoToActiveSession))]
        private bool _isAnySessionActiveGlobally;
        public IAsyncRelayCommand StartThisSessionCommand { get; }
        public IAsyncRelayCommand EndThisSessionCommand { get; }
        public IRelayCommand GoToActiveSessionCommand { get; }
        public bool CanStartThisSession => !IsViewingSessionTheActiveSession && !string.IsNullOrEmpty(ViewingSessionId);
        public bool CanEndThisSession => IsViewingSessionTheActiveSession;
        public bool CanGoToActiveSession => IsAnySessionActiveGlobally && !IsViewingSessionTheActiveSession;

        public MainWindowViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Cannot get DispatcherQueue for current thread.");
            _clipboardService = new ClipboardService();
            _tokenizerService = new JapaneseTokenizerService();
            _dictionaryService = new DictionaryService();
            _settingsService = new SettingsService();
            _aiService = new AIService();
            _dbService = DatabaseService.Instance;
            _sessionManager = App.SessionManager ?? throw new InvalidOperationException("Session Manager not initialized.");
            CopyAIAnalysisCommand = new RelayCommand(ExecuteCopyAIAnalysis, CanExecuteCopyAIAnalysis);
            GoBackToHubCommand = new AsyncRelayCommand(NavigateBackToHubAsync);
            LoadCurrentSettings();
            _clipboardService.JapaneseTextCopied += OnJapaneseTextCopied;

            StartThisSessionCommand = new AsyncRelayCommand(ExecuteStartThisSessionAsync, () => CanStartThisSession);
            EndThisSessionCommand = new AsyncRelayCommand(ExecuteEndThisSessionAsync, () => CanEndThisSession);
            GoToActiveSessionCommand = new RelayCommand(ExecuteGoToActiveSession, () => CanGoToActiveSession);
            if (_sessionManager != null) _sessionManager.ActiveSessionChanged += SessionManager_ActiveSessionChanged_MWVM;
            _ = LoadGlobalTokenStatsAsync();
            _clipboardService.StartMonitoring();
            System.Diagnostics.Debug.WriteLine("[MWVM] Constructor finished.");
        }
        public async Task LoadCurrentSessionDataAsync(string sessionIdToView)
        {
            await DispatcherQueue_EnqueueAsync(() =>
            {
                CurrentSessionEntries.Clear();
                IsLoading = true;
                IsHubViewActive = false;
                IsDetailViewActive = false;
                ApiErrorMessage = null;
                ViewingSessionId = sessionIdToView;
                ViewingSessionName = "Loading session...";
                ViewingSessionImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
                ViewingSessionStartTimeDisplay = "Loading...";
                ViewingSessionTokensUsedDisplay = "Loading...";
                System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData - DISPATCHER] Initializing view for session: {sessionIdToView}. ViewingSessionId set to: '{ViewingSessionId ?? "NULL"}'");
            });
            if (string.IsNullOrEmpty(sessionIdToView))
            {
                System.Diagnostics.Debug.WriteLine("[MWVM LoadSessionData] No session ID provided to view.");
                await DispatcherQueue_EnqueueAsync(() =>
                {
                    ViewingSessionName = "Error: No session specified";
                    IsLoading = false;
                    UpdateGlobalSessionActiveStatus();
                });
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] Loading entries for session to view: {sessionIdToView}");
            try
            {
                SessionInfo? sessionToViewInfo = null;
                string freeSessionId = await _dbService.GetOrCreateFreeSessionAsync();
                if (sessionIdToView == freeSessionId)
                {
                    string startTimeForFree = DateTime.MinValue.ToString("o");
                    int tokensForFree = 0;
                    sessionToViewInfo = await _dbService.GetSessionByIdOrDefaultAsync(freeSessionId) ??
                                        new SessionInfo(freeSessionId, "Free Session", null, startTimeForFree, null, tokensForFree, startTimeForFree, true);
                }
                else
                {
                    var allNamedSessions = await _dbService.GetAllNamedSessionsAsync();
                    sessionToViewInfo = allNamedSessions.FirstOrDefault(s => s.SessionId == sessionIdToView);
                }
                if (sessionToViewInfo != null)
                {
                    await DispatcherQueue_EnqueueAsync(async () =>
                    {
                        ViewingSessionName = string.IsNullOrEmpty(sessionToViewInfo.Name) ? "Unnamed Session" : sessionToViewInfo.Name;
                        if (!string.IsNullOrEmpty(sessionToViewInfo.ImagePath))
                        {
                            try
                            {
                                StorageFile file = await StorageFile.GetFileFromPathAsync(sessionToViewInfo.ImagePath);
                                BitmapImage img = new BitmapImage();
                                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                                {
                                    await img.SetSourceAsync(stream);
                                }
                                ViewingSessionImageSource = img;
                                System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] Loaded image for session {sessionIdToView} from: {sessionToViewInfo.ImagePath}");
                            }
                            catch (Exception imgEx)
                            {
                                ViewingSessionImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
                                System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] Failed to load image for session {sessionIdToView} from {sessionToViewInfo.ImagePath}: {imgEx.Message}");
                            }
                        }
                        else
                        {
                            ViewingSessionImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
                            System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] No image path for session {sessionIdToView}. Using default.");
                        }
                        try
                        {
                            ViewingSessionStartTimeDisplay = $"Started: {DateTime.Parse(sessionToViewInfo.StartTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g")}";
                        }
                        catch { ViewingSessionStartTimeDisplay = "Start time: N/A"; }
                        ViewingSessionTokensUsedDisplay = $"Tokens Used (Session): {sessionToViewInfo.TotalTokensUsedSession}";
                    });
                }
                else
                {
                    await DispatcherQueue_EnqueueAsync(() =>
                    {
                        ViewingSessionName = "Session Not Found";
                        ViewingSessionImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
                        ViewingSessionStartTimeDisplay = "Start time: N/A";
                        ViewingSessionTokensUsedDisplay = "Tokens: N/A";
                        System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] Session info not found in DB for ID: {sessionIdToView}");
                    });
                }
                var entriesFromDb = await _dbService.GetEntriesForSessionAsync(sessionIdToView);
                await DispatcherQueue_EnqueueAsync(() =>
                {
                    _aiAnalysisCache.Clear();
                    foreach (var entryInfo in entriesFromDb)
                    {
                        var displayItem = new SessionEntryDisplayItem(entryInfo);
                        if (entryInfo.AnalysisTypeUsed == "AI" && !string.IsNullOrWhiteSpace(entryInfo.AnalysisResultJson))
                        {
                            try
                            {
                                var cachedAnalysis = JsonSerializer.Deserialize<AIAnalysisResult>(entryInfo.AnalysisResultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (cachedAnalysis != null)
                                {
                                    _aiAnalysisCache[displayItem.Sentence] = cachedAnalysis;
                                    displayItem.IsAiAnalysisCached = true;
                                }
                            }
                            catch (JsonException) { displayItem.IsAiAnalysisCached = false; }
                        }
                        else { displayItem.IsAiAnalysisCached = false; }
                        CurrentSessionEntries.Add(displayItem);
                    }
                    if (!IsDetailViewActive)
                    {
                        SelectedSentenceIndex = CurrentSessionEntries.Count > 0 ? CurrentSessionEntries.Count - 1 : -1;
                    }
                    System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] Loaded {CurrentSessionEntries.Count} entries into UI for session {sessionIdToView}.");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData] Error loading session data: {ex.Message}\nStackTrace: {ex.StackTrace}");
                await DispatcherQueue_EnqueueAsync(() => ViewingSessionName = "Error loading session");
            }
            finally
            {
                await DispatcherQueue_EnqueueAsync(() =>
                {
                    IsLoading = false;
                    UpdateGlobalSessionActiveStatus();
                    System.Diagnostics.Debug.WriteLine($"[MWVM LoadSessionData - FINALLY] Called UpdateGlobalSessionActiveStatus. ViewingSessionId is now: '{ViewingSessionId ?? "NULL"}'");
                });
            }
        }
        private async Task ExecuteStartThisSessionAsync()
        {
            if (string.IsNullOrEmpty(ViewingSessionId) || IsViewingSessionTheActiveSession) return;
            System.Diagnostics.Debug.WriteLine($"[MWVM] ExecuteStartThisSessionAsync for ViewingSessionId: {ViewingSessionId}");
            IsLoading = true;
            await _sessionManager.StartExistingSessionAsync(ViewingSessionId);
            IsLoading = false;
        }
        private async Task ExecuteEndThisSessionAsync()
        {
            if (!IsViewingSessionTheActiveSession) return;
            System.Diagnostics.Debug.WriteLine($"[MWVM] ExecuteEndThisSessionAsync for ActiveSessionId: {_sessionManager.ActiveSessionId}");
            IsLoading = true;
            await _sessionManager.EndCurrentSessionAsync();
            IsLoading = false;
        }
        private async void ExecuteGoToActiveSession()
        {
            string? globalActiveId = _sessionManager.ActiveSessionId;
            System.Diagnostics.Debug.WriteLine($"[MWVM ExecuteGoToActiveSession] Called. GlobalActiveId: '{globalActiveId ?? "NULL"}', ViewingSessionId: '{ViewingSessionId ?? "NULL"}'");
            if (!string.IsNullOrEmpty(globalActiveId) && globalActiveId != ViewingSessionId)
            {
                System.Diagnostics.Debug.WriteLine($"[MWVM ExecuteGoToActiveSession] Navigating directly to active session: {globalActiveId}");
                if (App.MainWin != null)
                {
                    await App.MainWin.NavigateToSessionViewAsync(globalActiveId);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MWVM ExecuteGoToActiveSession] App.MainWin is NULL. Cannot navigate.");
                }
            }
            else if (string.IsNullOrEmpty(globalActiveId))
            {
                System.Diagnostics.Debug.WriteLine("[MWVM ExecuteGoToActiveSession] No global session active. Returning to Hub.");
                await GoBackToHubCommand.ExecuteAsync(null);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MWVM ExecuteGoToActiveSession] Already viewing the active session or globalActiveId is null.");
            }
        }

        private void UpdateGlobalSessionActiveStatus()
        {
            string? currentGlobalActiveId = _sessionManager.ActiveSessionId;
            bool oldIsViewingActive = IsViewingSessionTheActiveSession;
            bool oldIsAnyActive = IsAnySessionActiveGlobally;
            IsAnySessionActiveGlobally = !string.IsNullOrEmpty(_sessionManager.ActiveSessionId);
            IsViewingSessionTheActiveSession = !string.IsNullOrEmpty(ViewingSessionId) && ViewingSessionId == _sessionManager.ActiveSessionId;
            System.Diagnostics.Debug.WriteLine($"[MWVM UpdateGlobalStatus] ViewingSessionId: '{ViewingSessionId ?? "NULL"}'");
            System.Diagnostics.Debug.WriteLine($"[MWVM UpdateGlobalStatus] GlobalActiveSessionId: '{currentGlobalActiveId ?? "NULL"}'");
            System.Diagnostics.Debug.WriteLine($"[MWVM UpdateGlobalStatus] IsViewingSessionTheActiveSession: {IsViewingSessionTheActiveSession}");
            System.Diagnostics.Debug.WriteLine($"[MWVM UpdateGlobalStatus] IsAnySessionActiveGlobally: {IsAnySessionActiveGlobally}");
            if (oldIsViewingActive != IsViewingSessionTheActiveSession) OnPropertyChanged(nameof(IsViewingSessionTheActiveSession));
            if (oldIsAnyActive != IsAnySessionActiveGlobally) OnPropertyChanged(nameof(IsAnySessionActiveGlobally));
            OnPropertyChanged(nameof(CanStartThisSession));
            OnPropertyChanged(nameof(CanEndThisSession));
            OnPropertyChanged(nameof(CanGoToActiveSession));
            StartThisSessionCommand.NotifyCanExecuteChanged();
            EndThisSessionCommand.NotifyCanExecuteChanged();
            GoToActiveSessionCommand.NotifyCanExecuteChanged();
        }
        private void SessionManager_ActiveSessionChanged_MWVM(object? sender, EventArgs e)
        {
            _dispatcherQueue?.TryEnqueue(UpdateGlobalSessionActiveStatus);
        }

        private async Task LoadGlobalTokenStatsAsync()
        {
            try { var (today, last30, total) = await _dbService.GetGlobalTokenCountsAsync(); await DispatcherQueue_EnqueueAsync(() => { GlobalTokensToday = today; GlobalTokensLast30 = last30; GlobalTokensTotal = total; }); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MWVM LoadGlobalStats] Error: {ex.Message}"); }
        }
        private async void OnJapaneseTextCopied(object? sender, string text)
        {
            string? currentSessionId = _sessionManager.ActiveSessionId;
            if (string.IsNullOrEmpty(currentSessionId) || IsHubViewActive) { return; }
            System.Diagnostics.Debug.WriteLine($"[MWVM OnJapaneseTextCopied] Adding '{text.Substring(0, Math.Min(20, text.Length))}...'");
            var tempEntryInfo = new SessionEntryInfo(0, text, DateTime.UtcNow.ToString("o"), null, null, null, false);
            var newItem = new SessionEntryDisplayItem(tempEntryInfo);
            newItem.IsAiAnalysisCached = _aiAnalysisCache.ContainsKey(text);
            await DispatcherQueue_EnqueueAsync(() =>
            {
                CurrentSessionEntries.Add(newItem);
            });
            long newEntryId = 0;
            try { await _sessionManager.AddSentenceToCurrentSessionAsync(text); var entries = await _dbService.GetEntriesForSessionAsync(currentSessionId); var matchingEntry = entries.LastOrDefault(en => en.SentenceText == text); if (matchingEntry != null) { newEntryId = matchingEntry.EntryId; await DispatcherQueue_EnqueueAsync(() => { var uiItem = CurrentSessionEntries.FirstOrDefault(i => i.Sentence == text && i.EntryId == 0); if (uiItem != null) { uiItem.EntryId = newEntryId; } }); System.Diagnostics.Debug.WriteLine($"[MWVM OnJapaneseTextCopied] DB Entry ID obtained: {newEntryId}"); } else { /* log warning */ } }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MWVM OnJapaneseTextCopied] Error saving to DB: {ex.Message}"); await DispatcherQueue_EnqueueAsync(() => { var uiItem = CurrentSessionEntries.FirstOrDefault(i => i.Sentence == text && i.EntryId == 0); if (uiItem != null) CurrentSessionEntries.Remove(uiItem); }); return; }
            if (newEntryId > 0 && _loadedAiTriggerMode == AiAnalysisTrigger.OnCopy && _isAIModeActive && !string.IsNullOrWhiteSpace(_activeApiKey))
            {
                var itemToProcess = await DispatcherQueue_EnqueueAsync(() => CurrentSessionEntries.FirstOrDefault(i => i.EntryId == newEntryId));
                if (itemToProcess != null) { _ = CacheAiAnalysisAsync(text, itemToProcess); }
            }
        }
        public void LoadCurrentSettings() { AppSettings currentSettings = _settingsService.LoadSettings(); bool previousAIMode = _isAIModeActive; string? previousApiKey = _activeApiKey; AiAnalysisTrigger previousTrigger = _loadedAiTriggerMode; _isAIModeActive = currentSettings.UseAIMode; _activeApiKey = currentSettings.GeminiApiKey; _loadedAiTriggerMode = currentSettings.AiAnalysisTriggerMode; bool settingsChanged = previousAIMode != _isAIModeActive || previousTrigger != _loadedAiTriggerMode || (_isAIModeActive && previousApiKey != _activeApiKey); if (settingsChanged) { System.Diagnostics.Debug.WriteLine("[MWVM] Clearing AI Cache due to settings change."); _aiAnalysisCache.Clear(); _dispatcherQueue.TryEnqueue(() => { CurrentSentenceAIAnalysis = null; foreach (var item in CurrentSessionEntries) { item.IsAiAnalysisCached = false; item.IsAiAnalysisLoading = false; } }); } _needsFullDefinitionLoad = true; System.Diagnostics.Debug.WriteLine($"[MWVM] Settings Loaded/Reloaded: AI Mode Active = {_isAIModeActive}, Trigger = {_loadedAiTriggerMode}, Has API Key = {!string.IsNullOrWhiteSpace(_activeApiKey)}"); OnPropertyChanged(nameof(IsAIModeActive)); _ = LoadGlobalTokenStatsAsync(); }
        partial void OnSelectedSentenceIndexChanged(int value) { if (value >= 0 && value < CurrentSessionEntries.Count) { SelectedSentenceItem = CurrentSessionEntries[value]; } else { SelectedSentenceItem = null; } }
        partial void OnSelectedTokenIndexChanged(int value) { if (_isSettingInitialSelection) return; if (value >= 0 && value < DisplayTokens.Count) { SelectedToken = DisplayTokens[value]; } else { SelectedToken = null; } _ = UpdateDefinitionViewAsync(); }
        partial void OnSelectedTokenChanged(DisplayToken? value) { CopyAIAnalysisCommand.NotifyCanExecuteChanged(); }
        partial void OnCurrentSentenceAIAnalysisChanged(AIAnalysisResult? value) { CopyAIAnalysisCommand.NotifyCanExecuteChanged(); }
        public void EnterDetailView()
        {
            if (IsHubViewActive || IsDetailViewActive || SelectedSentenceItem == null) return;
            DetailedSentenceEntry = SelectedSentenceItem;
            System.Diagnostics.Debug.WriteLine($"[MWVM EnterDetailView] EntryId: {DetailedSentenceEntry.EntryId}");
            _dispatcherQueue.TryEnqueue(() =>
            {
                DisplayTokens.Clear(); SelectedToken = null; SelectedTokenIndex = -1;
                IsAnalyzingData = true; IsLoadingDefinition = false; ApiErrorMessage = null;
                _needsFullDefinitionLoad = true; IsDetailViewActive = true;
                DetailFocusTarget = "Sentence";
                _ = RequestLoadDefinitionHtml?.Invoke("<p><i>Loading analysis...</i></p>");
            });
            _ = PerformInitialAnalysisAndPopulateAsync();
        }
        public void ExitDetailView()
        {
            if (IsHubViewActive || !IsDetailViewActive) return;
            System.Diagnostics.Debug.WriteLine($"[MWVM ExitDetailView]");
            int previousListIndex = SelectedSentenceIndex;
            _dispatcherQueue.TryEnqueue(() =>
            {
                DetailedSentenceEntry = null;
                DisplayTokens.Clear();
                SelectedToken = null;
                SelectedTokenIndex = -1;
                IsAnalyzingData = false;
                IsLoadingDefinition = false;
                ApiErrorMessage = null;
                CurrentSentenceAIAnalysis = null;
                IsDetailViewActive = false;
                DetailFocusTarget = "Sentence";
                if (previousListIndex >= 0 && previousListIndex < CurrentSessionEntries.Count)
                {
                    SelectedSentenceIndex = previousListIndex;
                }
                else
                {
                    SelectedSentenceIndex = -1;
                }
            });
        }
        private async Task NavigateBackToHubAsync()
        {
            if (IsHubViewActive) return;
            string? lastViewedId = ViewingSessionId;
            await DispatcherQueue_EnqueueAsync(() =>
            {
                IsHubViewActive = true; IsDetailViewActive = false;
                DetailedSentenceEntry = null; DisplayTokens.Clear(); SelectedToken = null;
                SelectedTokenIndex = -1; ApiErrorMessage = null; CurrentSentenceAIAnalysis = null;
                ViewingSessionId = null;
                System.Diagnostics.Debug.WriteLine("[MWVM] State reset for returning to Hub.");
            });
            App.MainWin?.NavigateToHub();
        }
        private async Task PerformInitialAnalysisAndPopulateAsync() { await DispatcherQueue_EnqueueAsync(() => { IsAnalyzingData = true; ApiErrorMessage = null; DisplayTokens.Clear(); SelectedTokenIndex = -1; SelectedToken = null; CurrentSentenceAIAnalysis = null; _needsFullDefinitionLoad = true; _ = RequestLoadDefinitionHtml?.Invoke("<p><i>Loading analysis...</i></p>"); }); SessionEntryDisplayItem? targetEntryItem = DetailedSentenceEntry; string currentSentenceText = targetEntryItem?.Sentence ?? string.Empty; long currentEntryId = targetEntryItem?.EntryId ?? 0; if (string.IsNullOrEmpty(currentSentenceText) || targetEntryItem == null || currentEntryId == 0) { System.Diagnostics.Debug.WriteLine("[MWVM PerformInitial] Invalid target entry or text."); await DispatcherQueue_EnqueueAsync(() => IsAnalyzingData = false); return; } System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] Analyzing EntryId: {currentEntryId}"); AIAnalysisResult? analysisResult = null; int tokensUsed = 0; bool analysisSuccess = false; bool usedCache = false; string analysisTypeUsed = "OFFLINE"; string analysisJsonResult = string.Empty; List<TokenInfo> mecabTokens = new List<TokenInfo>(); if (_isAIModeActive && !string.IsNullOrWhiteSpace(_activeApiKey)) { if (_aiAnalysisCache.TryGetValue(currentSentenceText, out analysisResult)) { System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] Cache Hit."); analysisSuccess = true; usedCache = true; analysisTypeUsed = "AI"; await DispatcherQueue_EnqueueAsync(() => { CurrentSentenceAIAnalysis = analysisResult; ApiErrorMessage = null; targetEntryItem.IsAiAnalysisCached = true; }); analysisJsonResult = JsonSerializer.Serialize(analysisResult); } else { System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] Cache Miss. Calling API."); (analysisResult, tokensUsed) = await _aiService.AnalyzeSentenceAsync(currentSentenceText, _activeApiKey); await _sessionManager.AddTokensToCurrentSessionAsync(tokensUsed); await LoadGlobalTokenStatsAsync(); if (analysisResult != null && analysisResult.Tokens.Any()) { analysisSuccess = true; analysisTypeUsed = "AI"; _aiAnalysisCache[currentSentenceText] = analysisResult; analysisJsonResult = JsonSerializer.Serialize(analysisResult); await DispatcherQueue_EnqueueAsync(() => { CurrentSentenceAIAnalysis = analysisResult; ApiErrorMessage = null; targetEntryItem.IsAiAnalysisCached = true; }); System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] API Success."); } else { System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] API Failed. Falling back."); analysisSuccess = false; analysisTypeUsed = "OFFLINE"; await DispatcherQueue_EnqueueAsync(() => { CurrentSentenceAIAnalysis = null; ApiErrorMessage = "AI analysis failed. Using offline mode."; targetEntryItem.IsAiAnalysisCached = false; }); mecabTokens = _tokenizerService.Tokenize(currentSentenceText); analysisJsonResult = JsonSerializer.Serialize(mecabTokens); } } } else { System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] AI Off/NoKey. Using offline."); analysisSuccess = false; analysisTypeUsed = "OFFLINE"; await DispatcherQueue_EnqueueAsync(() => { targetEntryItem.IsAiAnalysisCached = false; }); mecabTokens = _tokenizerService.Tokenize(currentSentenceText); analysisJsonResult = JsonSerializer.Serialize(mecabTokens); } try { await _dbService.UpdateSessionEntryDetailsAsync(currentEntryId, analysisTypeUsed, analysisJsonResult, targetEntryItem.IsAiAnalysisCached); } catch (Exception dbEx) { System.Diagnostics.Debug.WriteLine($"[MWVM PerformInitial] FAILED to update DB entry {currentEntryId}: {dbEx.Message}"); } if (analysisSuccess && analysisResult != null) { await PopulateWithAIDataAsync(analysisResult); } else { await PopulateWithMecabDataAsync(mecabTokens); } if (RequestRenderSentenceWebView != null) { await RequestRenderSentenceWebView.Invoke(new List<DisplayToken>(DisplayTokens)); } int firstSelectable = -1; await DispatcherQueue_EnqueueAsync(() => { firstSelectable = DisplayTokens.ToList().FindIndex(t => t.IsSelectable); _isSettingInitialSelection = true; SelectedTokenIndex = firstSelectable; SelectedToken = (firstSelectable >= 0) ? DisplayTokens[firstSelectable] : null; _isSettingInitialSelection = false; IsAnalyzingData = false; }); await UpdateDefinitionViewAsync(); }
        internal async Task CacheAiAnalysisAsync(string sentence, SessionEntryDisplayItem historyItem) { if (!_isAIModeActive || string.IsNullOrWhiteSpace(_activeApiKey) || string.IsNullOrWhiteSpace(sentence) || _aiAnalysisCache.ContainsKey(sentence) || historyItem.IsAiAnalysisCached || historyItem.IsAiAnalysisLoading) { return; } System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] STARTING for EntryId: {historyItem.EntryId}"); await DispatcherQueue_EnqueueAsync(() => historyItem.SetLoadingState(true)); (AIAnalysisResult? analysisResult, int tokensUsed) = (null, 0); bool success = false; string analysisJson = string.Empty; string analysisType = "AI"; try { (analysisResult, tokensUsed) = await _aiService.AnalyzeSentenceAsync(sentence, _activeApiKey); success = analysisResult != null && analysisResult.Tokens.Any(); await _sessionManager.AddTokensToCurrentSessionAsync(tokensUsed); await LoadGlobalTokenStatsAsync(); if (success) { _aiAnalysisCache[sentence] = analysisResult!; analysisJson = JsonSerializer.Serialize(analysisResult!); await DispatcherQueue_EnqueueAsync(() => historyItem.IsAiAnalysisCached = true); System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] SUCCESS."); if (historyItem.EntryId > 0) { try { await _dbService.UpdateSessionEntryDetailsAsync(historyItem.EntryId, analysisType, analysisJson, true); } catch (Exception dbEx) { System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] Failed to update DB entry {historyItem.EntryId}: {dbEx.Message}"); } } else { System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] Warning: Cannot update DB, EntryId is 0."); } } else { await DispatcherQueue_EnqueueAsync(() => historyItem.IsAiAnalysisCached = false); System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] FAILED."); } } catch (Exception ex) { await DispatcherQueue_EnqueueAsync(() => historyItem.IsAiAnalysisCached = false); success = false; System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] EXCEPTION: {ex.Message}"); } finally { await DispatcherQueue_EnqueueAsync(() => historyItem.SetLoadingState(false)); System.Diagnostics.Debug.WriteLine($"[MWVM CacheAi] FINISHED (Success: {success})"); } }
        private async Task PopulateWithAIDataAsync(AIAnalysisResult analysisResult) { await DispatcherQueue_EnqueueAsync(() => { DisplayTokens.Clear(); foreach (var aiToken in analysisResult.Tokens) { DisplayTokens.Add(new DisplayToken(aiToken)); } }); }
        private async Task PopulateWithMecabDataAsync(List<TokenInfo> mecabTokens) { await DispatcherQueue_EnqueueAsync(() => { DisplayTokens.Clear(); foreach (var mecabToken in mecabTokens) { DisplayTokens.Add(new DisplayToken(mecabToken)); } }); }
        public async Task UpdateDefinitionViewAsync()
        {
            if (IsHubViewActive || !IsDetailViewActive) { return; }
            await DispatcherQueue_EnqueueAsync(() => IsLoadingDefinition = true);
            string? fullHtmlToLoad = null; string? tokenDetailSnippet = null; DisplayToken? currentSelection = SelectedToken; AIAnalysisResult? currentAnalysis = CurrentSentenceAIAnalysis; bool isCurrentlyUsingAI = _isAIModeActive && currentAnalysis != null; if (isCurrentlyUsingAI) { if (_needsFullDefinitionLoad) { fullHtmlToLoad = FormatAIDataForWebView(currentAnalysis!, currentSelection, true); _needsFullDefinitionLoad = false; } else { tokenDetailSnippet = FormatAIDataForWebView(currentAnalysis!, currentSelection, false); } } else { TokenInfo? mecabToken = currentSelection?.MecabData; fullHtmlToLoad = await FetchAndFormatTraditionalAnalysisAsync(mecabToken); _needsFullDefinitionLoad = true; if (!_isAIModeActive) { await DispatcherQueue_EnqueueAsync(() => ApiErrorMessage = null); } }
            if (fullHtmlToLoad != null && RequestLoadDefinitionHtml != null) { await DispatcherQueue_EnqueueAsync(() => RequestLoadDefinitionHtml.Invoke(fullHtmlToLoad)); } else if (tokenDetailSnippet != null && RequestUpdateDefinitionTokenDetails != null) { await DispatcherQueue_EnqueueAsync(() => RequestUpdateDefinitionTokenDetails.Invoke(tokenDetailSnippet)); } else if (RequestLoadDefinitionHtml != null) { await DispatcherQueue_EnqueueAsync(() => RequestLoadDefinitionHtml.Invoke(FormatTraditionalDataForWebView(null, null))); }
            await DispatcherQueue_EnqueueAsync(() => IsLoadingDefinition = false);
        }
        private async Task<string?> FetchAndFormatTraditionalAnalysisAsync(TokenInfo? mecabToken) { if (mecabToken == null) return FormatTraditionalDataForWebView(null, null); List<DictionaryEntry> definitions = new List<DictionaryEntry>(); string? formattedHtml = null; try { definitions = await _dictionaryService.FindEntriesAsync(mecabToken.BaseForm, mecabToken.Reading); formattedHtml = FormatTraditionalDataForWebView(definitions, mecabToken); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MWVM] Error fetching traditional definitions: {ex.Message}"); formattedHtml = "<p>Error loading offline definitions.</p>"; } return formattedHtml; }
        private double DefinitionFontSize => App.MainWin?.DefinitionFontSize ?? 18.0;
        private string FormatAIDataForWebView(AIAnalysisResult aiResult, DisplayToken? currentSelection, bool includeWrapperAndTranslation) { StringBuilder htmlBuilder = new StringBuilder(); if (includeWrapperAndTranslation) { htmlBuilder.Append($@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"); htmlBuilder.Append(@"html, body { height: 100%; margin: 0; padding: 0; box-sizing: border-box; overflow: hidden; font-family: 'Segoe UI', sans-serif; line-height: 1.6; margin: 5px; background-color: transparent; color: var(--text-primary-color, black); -webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; overflow-y: auto; scrollbar-width: none; -ms-overflow-style: none; } "); htmlBuilder.Append(@"body::-webkit-scrollbar { display: none; } "); htmlBuilder.Append(@".section-header { font-weight: bold; margin-top: 0.8em; margin-bottom: 0.3em; padding-bottom: 0.2em; border-bottom: 1px solid #eee; font-size: 1.1em; } "); htmlBuilder.Append(@".translation { margin-bottom: 0.8em; padding: 8px; background-color: rgba(0, 120, 215, 0.05); border-left: 3px solid rgba(0, 120, 215, 0.5); border-radius: 3px; } "); htmlBuilder.Append(@".token-info { margin-top: 0em; } "); htmlBuilder.Append(@".token-surface { font-weight: bold; font-size: 1.2em; } "); htmlBuilder.Append(@".token-reading { color: gray; margin-left: 5px; } "); htmlBuilder.Append(@".token-meaning { margin-top: 5px; } "); htmlBuilder.Append(@".token-pos { font-style: italic; color: #555; margin-top: 3px; font-size: 0.9em; } "); htmlBuilder.Append(@"</style></head><body id='definition-body'>"); if (aiResult.FullTranslation != null) { htmlBuilder.Append("<div id='full-translation-div'>"); htmlBuilder.Append("<div class='section-header'>Full Translation</div>"); htmlBuilder.Append($"<div class='translation'>{System.Security.SecurityElement.Escape(aiResult.FullTranslation.Text)}</div>"); htmlBuilder.Append("</div>"); } htmlBuilder.Append("<div class='section-header'>Selected Token Analysis</div>"); htmlBuilder.Append("<div id='selected-token-div'>"); } AITokenInfo? selectedAiToken = currentSelection?.AiData; if (selectedAiToken != null) { htmlBuilder.Append("<div class='token-info'>"); htmlBuilder.Append($"<span class='token-surface'>{System.Security.SecurityElement.Escape(selectedAiToken.Surface)}</span>"); if (!string.IsNullOrWhiteSpace(selectedAiToken.Reading)) { htmlBuilder.Append($"<span class='token-reading'>[{System.Security.SecurityElement.Escape(selectedAiToken.Reading)}]</span>"); } if (!string.IsNullOrWhiteSpace(selectedAiToken.ContextualMeaning)) { htmlBuilder.Append($"<div class='token-meaning'>{System.Security.SecurityElement.Escape(selectedAiToken.ContextualMeaning)}</div>"); } if (!string.IsNullOrWhiteSpace(selectedAiToken.PartOfSpeech)) { htmlBuilder.Append($"<div class='token-pos'>({System.Security.SecurityElement.Escape(selectedAiToken.PartOfSpeech)}"); if (!string.IsNullOrWhiteSpace(selectedAiToken.BaseForm) && selectedAiToken.BaseForm != selectedAiToken.Surface) { htmlBuilder.Append($", Base: {System.Security.SecurityElement.Escape(selectedAiToken.BaseForm)}"); } htmlBuilder.Append(")"); htmlBuilder.Append("</div>"); } htmlBuilder.Append("</div>"); } else if (currentSelection != null) { htmlBuilder.Append($"<p><i>Select a Japanese token.</i></p>"); } else { htmlBuilder.Append("<p><i>No token selected.</i></p>"); } if (includeWrapperAndTranslation) { htmlBuilder.Append("</div>"); htmlBuilder.Append(@"<script> function updateTokenDetails(tokenHtml) { try { const targetDiv = document.getElementById('selected-token-div'); if (targetDiv) { targetDiv.innerHTML = tokenHtml; console.log('Updated token details via JS.');} else { console.error('Target div selected-token-div not found!');} } catch(e) { console.error('JS Error updateTokenDetails:', e); } } function initializeNavigation() { return 0; } function highlightListItem(index) { return false; } function isCurrentListItemInternalLink(index) { return false; } function scrollDefinition(delta) { try { document.body.scrollTop += delta; } catch(e) {} } function setDefinitionFocus(isFocused) { } </script>"); htmlBuilder.Append("</body></html>"); } return htmlBuilder.ToString(); }
        private string FormatTraditionalDataForWebView(List<DictionaryEntry>? definitions, TokenInfo? mecabToken) { StringBuilder htmlBuilder = new StringBuilder(); htmlBuilder.Append($@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"); htmlBuilder.Append(@"html, body { height: 100%; margin: 0; padding: 0; box-sizing: border-box; overflow: hidden; } "); htmlBuilder.Append(@"body { "); htmlBuilder.Append($"font-family: 'Segoe UI', sans-serif; font-size: {DefinitionFontSize}px; line-height: 1.6; margin: 5px; padding: 0; "); htmlBuilder.Append(@"height: 100%; "); htmlBuilder.Append(@"background-color: transparent; color: var(--text-primary-color, black); "); htmlBuilder.Append(@"-webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; "); htmlBuilder.Append(@"overflow-y: auto; "); htmlBuilder.Append(@"scrollbar-width: none; "); htmlBuilder.Append(@"-ms-overflow-style: none; "); htmlBuilder.Append(@"} "); htmlBuilder.Append(@"body::-webkit-scrollbar { display: none; } "); htmlBuilder.Append(@"ul { padding-left: 20px; margin-top: 0.5em; margin-bottom: 0.5em; list-style: disc; } "); htmlBuilder.Append(@"li { margin-bottom: 0.2em; } "); htmlBuilder.Append(@"div[data-content='notes'] ul, div[data-content='examples'] ul { margin-top: 0.2em; list-style: none; padding-left: 5px; } "); htmlBuilder.Append(@"div[data-content='references'] ul li span[data-content='refGlosses'] { font-size: 0.9em; color: var(--text-secondary-color, gray); margin-left: 5px; } "); htmlBuilder.Append(@"span.internal-link { color: var(--system-accent-color, blue); cursor: pointer; text-decoration: underline; } "); htmlBuilder.Append(@"li.focusable-li { outline: none; } "); htmlBuilder.Append(@"li.current-nav-item { background-color: rgba(0, 120, 215, 0.1); outline: 1px dashed rgba(0, 120, 215, 0.3); border-radius: 3px; } "); htmlBuilder.Append(@"body.definition-focused li.current-nav-item { background-color: rgba(0, 120, 215, 0.2); outline: 1px solid rgba(0, 120, 215, 0.5); } "); htmlBuilder.Append(@".definition-entry-header { font-weight: bold; margin-top: 0.8em; padding-bottom: 0.2em; border-bottom: 1px solid #eee; } "); htmlBuilder.Append(@"hr { border: none; border-top: 1px dashed #ccc; margin: 1em 0; } "); htmlBuilder.Append(@"</style></head><body id='definition-body'>"); if (mecabToken != null) { string term = System.Security.SecurityElement.Escape(mecabToken.Surface); string headerTerm = (!string.IsNullOrEmpty(mecabToken.BaseForm) && mecabToken.BaseForm != mecabToken.Surface) ? System.Security.SecurityElement.Escape(mecabToken.BaseForm) : term; string reading = System.Security.SecurityElement.Escape(Converters.KatakanaToHiraganaConverter.ToHiraganaStatic(mecabToken.Reading)); htmlBuilder.Append($"<div class='definition-entry-header'>{headerTerm} [{reading}]</div>"); } if (definitions != null && definitions.Any()) { for (int i = 0; i < definitions.Count; i++) { var entry = definitions[i]; if (i > 0) { htmlBuilder.Append("<hr />"); } if (!string.IsNullOrWhiteSpace(entry.DefinitionHtml)) { htmlBuilder.Append(entry.DefinitionHtml); } else if (!string.IsNullOrWhiteSpace(entry.DefinitionText)) { htmlBuilder.Append($"<p>{System.Security.SecurityElement.Escape(entry.DefinitionText)}</p>"); } } } else if (mecabToken != null) { htmlBuilder.Append("<p><i>No offline definitions found.</i></p>"); } else { htmlBuilder.Append("<p><i>Select a token.</i></p>"); } htmlBuilder.Append(@"<script> let focusableItems = []; let currentHighlightIndex = -1; function initializeNavigation() { try { focusableItems = Array.from(document.querySelectorAll('li')); focusableItems.forEach(item => item.classList.add('focusable-li')); currentHighlightIndex = -1; return focusableItems.length; } catch(e) { return 0; } } function highlightListItem(index) { try { if (currentHighlightIndex >= 0 && currentHighlightIndex < focusableItems.length) { focusableItems[currentHighlightIndex].classList.remove('current-nav-item'); } if (index >= 0 && index < focusableItems.length) { const newItem = focusableItems[index]; newItem.classList.add('current-nav-item'); newItem.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' }); currentHighlightIndex = index; return newItem.querySelector('span.internal-link') !== null; } else { currentHighlightIndex = -1; return false; } } catch(e) { return false;} } function isCurrentListItemInternalLink(index) { try { if (index >= 0 && index < focusableItems.length) { return focusableItems[index].querySelector('span.internal-link') !== null; } return false; } catch(e) { return false;} } function scrollDefinition(delta) { try { document.body.scrollTop += delta; } catch(e) {} } function setDefinitionFocus(isFocused) { try { const body = document.getElementById('definition-body'); if(isFocused) { body.classList.add('definition-focused'); } else { body.classList.remove('definition-focused'); } } catch(e) {} } </script></body></html>"); return htmlBuilder.ToString(); }
        public void MoveSelection(int delta) { if (IsHubViewActive || IsDetailViewActive || CurrentSessionEntries.Count == 0) return; int newIndex = SelectedSentenceIndex + delta; SelectedSentenceIndex = Math.Clamp(newIndex, 0, CurrentSessionEntries.Count - 1); }
        public void MoveTokenSelection(int delta)
        {
            if (!CanInteractWithDetailView || IsHubViewActive || !IsDetailViewActive || !DisplayTokens.Any() || DetailFocusTarget != "Sentence")
            {
                System.Diagnostics.Debug.WriteLine($"[MWVM MoveTokenSelection] Conditions not met. DetailFocus: {DetailFocusTarget}, CanInteract: {CanInteractWithDetailView}");
                return;
            }
            int oldTokenIndex = SelectedTokenIndex;
            int currentSelectableIndex = SelectedTokenIndex;
            int numTokens = DisplayTokens.Count;
            if (numTokens == 0) return;
            int nextIndex = currentSelectableIndex;
            int Bumper = numTokens * 2;
            int count = 0;
            do
            {
                nextIndex += delta;
                count++;
                if (nextIndex < 0 || nextIndex >= numTokens)
                {
                    nextIndex = currentSelectableIndex;
                    break;
                }
                if (DisplayTokens[nextIndex].IsSelectable)
                {
                    break;
                }
            } while (count < Bumper);
            if (count >= Bumper || (nextIndex >= 0 && nextIndex < numTokens && !DisplayTokens[nextIndex].IsSelectable))
            {
                nextIndex = currentSelectableIndex;
            }
            if (nextIndex >= 0 && nextIndex < numTokens && nextIndex != currentSelectableIndex)
            {
                SelectedTokenIndex = nextIndex;
            }
            System.Diagnostics.Debug.WriteLine($"[MWVM MoveTokenSelection] Delta: {delta}, OldTokenIndex: {oldTokenIndex}, NewTokenIndex: {SelectedTokenIndex}");
        }
        public void MoveKanjiTokenSelection(int delta) { if (!CanInteractWithDetailView || IsHubViewActive || !IsDetailViewActive || !DisplayTokens.Any() || DetailFocusTarget != "Sentence") return; /* ... */ }
        public void FocusDefinitionArea() { if (IsHubViewActive || !IsDetailViewActive || DetailFocusTarget == "Definition" || IsAnalyzingData) return; DetailFocusTarget = "Definition"; _needsFullDefinitionLoad = true; _ = UpdateDefinitionViewAsync(); }
        public void FocusSentenceArea() { if (IsHubViewActive || !IsDetailViewActive || DetailFocusTarget == "Sentence" || IsAnalyzingData) return; DetailFocusTarget = "Sentence"; _needsFullDefinitionLoad = true; _ = UpdateDefinitionViewAsync(); }
        private bool CanExecuteCopyAIAnalysis() => CurrentSentenceAIAnalysis != null;
        private void ExecuteCopyAIAnalysis() { if (CurrentSentenceAIAnalysis == null) return; try { string jsonToCopy = JsonSerializer.Serialize(CurrentSentenceAIAnalysis, new JsonSerializerOptions { WriteIndented = true }); ClipboardService.SetClipboardTextWithIgnore(jsonToCopy); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MWVM] Failed to copy AI analysis: {ex.Message}"); } }
        public async Task HandleGlobalOpenAnalysisAsync() { if (_loadedAiTriggerMode == AiAnalysisTrigger.OnGlobalOpen && _isAIModeActive && !string.IsNullOrWhiteSpace(_activeApiKey)) { SessionEntryDisplayItem? lastItem = null; await DispatcherQueue_EnqueueAsync(() => { lastItem = CurrentSessionEntries.LastOrDefault(); }); if (lastItem != null) { System.Diagnostics.Debug.WriteLine($"[MWVM] Handling Global Open Trigger - Caching last sentence..."); await CacheAiAnalysisAsync(lastItem.Sentence, lastItem); } else { System.Diagnostics.Debug.WriteLine($"[MWVM] Handling Global Open Trigger - No sentences."); } } }
        public void Cleanup()
        {
            _clipboardService.StopMonitoring();
            _clipboardService.JapaneseTextCopied -= OnJapaneseTextCopied;
            if (_sessionManager != null)
            {
                _sessionManager.ActiveSessionChanged -= SessionManager_ActiveSessionChanged_MWVM;
            }
            RequestLoadDefinitionHtml = null;
            RequestRenderSentenceWebView = null;
            RequestUpdateDefinitionTokenDetails = null;
            System.Diagnostics.Debug.WriteLine("[MWVM] Cleanup called and event subscriptions removed.");
        }
        private Task DispatcherQueue_EnqueueAsync(Action function, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { function(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue action.")); } return tcs.Task; }
        private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue async action.")); } return tcs.Task; }
        private Task<T> DispatcherQueue_EnqueueAsync<T>(Func<T> function, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource<T>(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { T result = function(); tcs.SetResult(result); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue function.")); } return tcs.Task; }
        public void ForceFullDefinitionReload() => _needsFullDefinitionLoad = true;
    }
}