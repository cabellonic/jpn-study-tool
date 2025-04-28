using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Gaming.Input;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace JpnStudyTool;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    public MainWindowViewModel ViewModel { get; }
    private readonly WindowActivationService _windowActivationService;
    private Gamepad? _localGamepad = null;
    private DateTime _lastGamepadNavTime = DateTime.MinValue;
    private readonly TimeSpan _gamepadNavDebounce = TimeSpan.FromMilliseconds(200);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gamepadPollingTimer;
    private AppSettings _currentSettings = new();
    private readonly Dictionary<VirtualKey, string> _localKeyToActionId = new();
    private readonly Dictionary<GamepadButtons, string> _localButtonToActionId = new();
    private SettingsWindow? _settingsWindowInstance = null;

    private const double DEFAULT_SENTENCE_FONT_SIZE = 24;
    private const double DEFAULT_DEFINITION_FONT_SIZE = 18;
    public double SentenceFontSize => DEFAULT_SENTENCE_FONT_SIZE;
    public double DefinitionFontSize => DEFAULT_DEFINITION_FONT_SIZE;

    private bool _isSentenceWebViewReady = false;
    private bool _isDefinitionWebViewReady = false;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _highlightThrottleTimer;
    private readonly TimeSpan _highlightThrottleInterval = TimeSpan.FromMilliseconds(100);
    private DateTime _lastHighlightTime = DateTime.MinValue;
    private bool _isHighlightUpdatePending = false;
    private int _pendingHighlightIndex = -1;

    private int _definitionLiCount = 0;
    private int _currentDefinitionLiIndex = -1;

    private bool _isLoadingSentence = false;
    private bool _isLoadingDefinition = false;


    public MainWindow()
    {
        this.InitializeComponent();
        _windowActivationService = new WindowActivationService();
        IntPtr hwndPtr = WindowNative.GetWindowHandle(this);
        _windowActivationService.RegisterWindowHandle(hwndPtr);
        ViewModel = new MainWindowViewModel();
        ViewModel.RequestLoadDefinitionHtml += ViewModel_RequestLoadDefinitionHtml;

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.DataContext = ViewModel;
            rootElement.PreviewKeyDown += MainWindow_Root_PreviewKeyDown;
        }
        this.Title = "Jpn Study Tool"; this.Closed += MainWindow_Closed; this.Activated += MainWindow_Activated;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateViewVisibilityUI(ViewModel.CurrentViewInternal);
        Gamepad.GamepadAdded += Gamepad_GamepadAdded;
        Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
        TryHookCorrectGamepad();
        SetupGamepadPollingTimer();
        InitializeHighlightThrottleTimer();
    }

    private async Task ViewModel_RequestLoadDefinitionHtml(string? combinedHtmlContent)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] Received request to load definition HTML (Is Null or Empty? {string.IsNullOrEmpty(combinedHtmlContent)}).");
        await LoadDefinitionHtmlAsync(combinedHtmlContent);
    }

    private void InitializeHighlightThrottleTimer()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq != null)
        {
            _highlightThrottleTimer = dq.CreateTimer();
            _highlightThrottleTimer.IsRepeating = false;
            _highlightThrottleTimer.Tick += HighlightThrottleTimer_Tick;
            System.Diagnostics.Debug.WriteLine("[Throttle] Highlight throttle timer initialized.");
        }
        else { System.Diagnostics.Debug.WriteLine("[Throttle] CRITICAL: Failed to get DispatcherQueue for throttle timer."); }
    }

    private async void HighlightThrottleTimer_Tick(object? sender, object e)
    {
        _highlightThrottleTimer?.Stop();
        _isHighlightUpdatePending = false;
        System.Diagnostics.Debug.WriteLine($"[Throttle] Timer ticked. Highlighting index {_pendingHighlightIndex}");
        await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(_pendingHighlightIndex));
        _lastHighlightTime = DateTime.UtcNow;
    }

    private async void SentenceWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception != null) { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Init failed: {args.Exception.Message}"); _isSentenceWebViewReady = false; return; }
        System.Diagnostics.Debug.WriteLine("[WebView2 Sentence] Initialized.");
        _isSentenceWebViewReady = true;
        if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; }
        else { _isSentenceWebViewReady = false; return; }
        if (ViewModel.CurrentViewInternal == "detail") { await LoadSentenceDataAsync(); }
    }

    private async void DefinitionWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception != null) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Init failed: {args.Exception.Message}"); _isDefinitionWebViewReady = false; return; }
        System.Diagnostics.Debug.WriteLine("[WebView2 Definition] Initialized.");
        _isDefinitionWebViewReady = true;
        if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; }
        else { _isDefinitionWebViewReady = false; return; }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CurrentViewInternal))
        {
            DispatcherQueue.TryEnqueue(() => UpdateViewVisibilityUI(ViewModel.CurrentViewInternal));
            if (ViewModel.CurrentViewInternal == "detail")
            {
                await DispatcherQueue_EnqueueAsync(LoadSentenceDataAsync);
            }
            else { await ClearWebViewsAsync(); }
        }
        else if (e.PropertyName == nameof(ViewModel.SelectedTokenIndex))
        {
            int newIndex = ViewModel.SelectedTokenIndex;
            _pendingHighlightIndex = newIndex;
            var now = DateTime.UtcNow;
            var timeSinceLastHighlight = now - _lastHighlightTime;
            if (timeSinceLastHighlight >= _highlightThrottleInterval)
            {
                System.Diagnostics.Debug.WriteLine($"[Throttle] Interval ({_highlightThrottleInterval.TotalMilliseconds}ms) passed. Highlighting index {newIndex} immediately.");
                _highlightThrottleTimer?.Stop(); _isHighlightUpdatePending = false;
                await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(newIndex));
                _lastHighlightTime = DateTime.UtcNow;
            }
            else if (!_isHighlightUpdatePending)
            {
                var delayNeeded = _highlightThrottleInterval - timeSinceLastHighlight;
                System.Diagnostics.Debug.WriteLine($"[Throttle] Throttling active. Scheduling highlight for index {newIndex} in {delayNeeded.TotalMilliseconds}ms.");
                _isHighlightUpdatePending = true;
                if (_highlightThrottleTimer != null)
                { _highlightThrottleTimer.Interval = delayNeeded > TimeSpan.Zero ? delayNeeded : TimeSpan.FromMilliseconds(1); _highlightThrottleTimer.Start(); }
            }
            else { System.Diagnostics.Debug.WriteLine($"[Throttle] Throttling active. Update already pending for latest index ({_pendingHighlightIndex}). Ignoring request for {newIndex}."); }
        }
        else if (e.PropertyName == nameof(ViewModel.DetailFocusTarget))
        {
            await DispatcherQueue_EnqueueAsync(UpdateDetailFocusVisuals);
        }
    }

    private void UpdateViewVisibilityUI(string? currentView)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateViewVisibilityUI: State={currentView ?? "null"}");
        if (currentView == "list")
        {
            ListViewContentGrid.Visibility = Visibility.Visible;
            DetailViewContentGrid.Visibility = Visibility.Collapsed;
            SentenceWebView.Visibility = Visibility.Collapsed;
            DefinitionWebView.Visibility = Visibility.Collapsed;
            TrySetFocusToListOrRoot();
        }
        else if (currentView == "detail")
        {
            ListViewContentGrid.Visibility = Visibility.Collapsed;
            DetailViewContentGrid.Visibility = Visibility.Visible;
            SentenceWebView.Visibility = Visibility.Collapsed;
            DefinitionWebView.Visibility = Visibility.Collapsed;
            DispatcherQueue.TryEnqueue(() => SentenceFocusAnchor.Focus(FocusState.Programmatic));
        }
        else { ListViewContentGrid.Visibility = Visibility.Visible; DetailViewContentGrid.Visibility = Visibility.Collapsed; SentenceWebView.Visibility = Visibility.Collapsed; DefinitionWebView.Visibility = Visibility.Collapsed; }
    }

    private async Task ClearWebViewsAsync()
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] Clearing WebViews...");
        if (_isSentenceWebViewReady && SentenceWebView.CoreWebView2 != null) { try { SentenceWebView.CoreWebView2.Navigate("about:blank"); } catch { } }
        if (_isDefinitionWebViewReady && DefinitionWebView.CoreWebView2 != null) { try { DefinitionWebView.CoreWebView2.Navigate("about:blank"); } catch { } }
        SentenceWebView.Visibility = Visibility.Collapsed;
        DefinitionWebView.Visibility = Visibility.Collapsed;
        await Task.CompletedTask;
    }

    private async Task LoadSentenceDataAsync()
    {
        if (_isLoadingSentence) return;
        _isLoadingSentence = true;
        System.Diagnostics.Debug.WriteLine("[WebView2 Sentence] LoadSentenceDataAsync START");
        SentenceWebView.Visibility = Visibility.Collapsed;
        if (!_isSentenceWebViewReady || SentenceWebView.CoreWebView2 == null) { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Cannot load: Not ready."); _isLoadingSentence = false; return; }
        if (ViewModel.Tokens == null || !ViewModel.Tokens.Any()) { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Cannot load: No tokens."); SentenceWebView.CoreWebView2.Navigate("about:blank"); _isLoadingSentence = false; return; }

        StringBuilder htmlBuilder = new StringBuilder();
        htmlBuilder.Append(@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>body { font-family: 'Segoe UI', sans-serif; font-size: "); htmlBuilder.Append(this.SentenceFontSize); htmlBuilder.Append(@"px;line-height: 1.5; margin: 5px; padding: 0; overflow-wrap: break-word; word-wrap: break-word;background-color: transparent; color: black;-webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; } .token-span { cursor: default; padding: 0 1px; display: inline; border-radius: 3px; transition: background-color 0.1s ease-in-out, color 0.1s ease-in-out; } .highlighted-sentence { background-color: LightSkyBlue; } .highlighted-definition { background-color: #406080; color: white; } .focus-sentence .highlighted-sentence { } .focus-definition .highlighted-definition { } html, body { height: 100%; box-sizing: border-box; }</style></head><body id='sentence-body' class='focus-sentence'>");
        int actualIndex = 0; foreach (var token in ViewModel.Tokens) { string escapedSurface = System.Security.SecurityElement.Escape(token.Surface) ?? string.Empty; htmlBuilder.Append($"<span id='token-{actualIndex}' class='token-span {(token.IsSelectable ? "selectable" : "")}'>{escapedSurface}</span>"); actualIndex++; }
        htmlBuilder.Append(@"<script>let currentHighlightId = null; let currentHighlightClass = ''; function highlightToken(tokenId, focusTarget) { try { const newElement = document.getElementById(tokenId); const body = document.getElementById('sentence-body'); let highlightClass = ''; if (focusTarget === 'Sentence') { highlightClass = 'highlighted-sentence'; body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); } else { highlightClass = 'highlighted-definition'; body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); } if (currentHighlightId) { const oldElement = document.getElementById(currentHighlightId); if (oldElement) { oldElement.classList.remove('highlighted-sentence', 'highlighted-definition'); } } if (newElement) { newElement.classList.add(highlightClass); currentHighlightId = tokenId; currentHighlightClass = highlightClass; if (focusTarget === 'Sentence') { newElement.scrollIntoView({ behavior: 'auto', block: 'nearest', inline: 'nearest' }); } } else { currentHighlightId = null; currentHighlightClass = ''; } } catch(e) { console.error('JS Error highlightToken:', e);}} function setFocusClass(focusTarget) { try { const body = document.getElementById('sentence-body'); const currentElement = currentHighlightId ? document.getElementById(currentHighlightId) : null; let newHighlightClass = ''; if (focusTarget === 'Sentence') { body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); newHighlightClass = 'highlighted-sentence'; } else { body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); newHighlightClass = 'highlighted-definition'; } if (currentElement && currentHighlightClass !== newHighlightClass) { currentElement.classList.remove(currentHighlightClass); currentElement.classList.add(newHighlightClass); currentHighlightClass = newHighlightClass; } } catch(e) { console.error('JS Error setFocusClass:', e);}}</script></body></html>");

        TaskCompletionSource<bool> navigationCompletedTcs = new TaskCompletionSource<bool>();
        TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandler = null;
        try
        {
            navigationCompletedHandler = (sender, args) => { if (SentenceWebView?.CoreWebView2 != null) { SentenceWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandler; } if (args.IsSuccess) { navigationCompletedTcs.TrySetResult(true); } else { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Navigation failed: {args.WebErrorStatus}"); navigationCompletedTcs.TrySetResult(false); } };
            try { SentenceWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandler; } catch { }
            SentenceWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandler;
            System.Diagnostics.Debug.WriteLine("[WebView2 Sentence] Navigating...");
            SentenceWebView.CoreWebView2.NavigateToString(htmlBuilder.ToString());
            bool navigationSuccess = await navigationCompletedTcs.Task;
            if (navigationSuccess) { System.Diagnostics.Debug.WriteLine("[WebView2 Sentence] Navigation successful."); await HighlightTokenInWebViewAsync(ViewModel.SelectedTokenIndex); SentenceWebView.Visibility = Visibility.Visible; }
            else { System.Diagnostics.Debug.WriteLine("[WebView2 Sentence] Navigation failed."); SentenceWebView.Visibility = Visibility.Collapsed; }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Error loading: {ex.Message}"); SentenceWebView.Visibility = Visibility.Collapsed; }
        finally { _isLoadingSentence = false; System.Diagnostics.Debug.WriteLine("[WebView2 Sentence] LoadSentenceDataAsync END"); }
    }

    private async Task LoadDefinitionHtmlAsync(string? combinedDefinitionHtml)
    {
        if (_isLoadingDefinition) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Already loading, skipping new request."); return; }
        ;
        _isLoadingDefinition = true;
        System.Diagnostics.Debug.WriteLine("[WebView2 Definition] LoadDefinitionHtmlAsync START");
        DefinitionWebView.Visibility = Visibility.Collapsed;
        _definitionLiCount = 0; _currentDefinitionLiIndex = -1;

        if (!_isDefinitionWebViewReady || DefinitionWebView.CoreWebView2 == null) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Cannot load: Not ready."); _isLoadingDefinition = false; return; }

        if (string.IsNullOrWhiteSpace(combinedDefinitionHtml))
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] No HTML content provided. Clearing.");
            DefinitionWebView.CoreWebView2.Navigate("about:blank");
            _isLoadingDefinition = false; return;
        }
        System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Combined HTML content received. Preparing...");

        StringBuilder fullHtml = new StringBuilder();
        fullHtml.Append($@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>body {{ font-family: 'Segoe UI', sans-serif; font-size: {this.DefinitionFontSize}px; line-height: 1.6; margin: 5px; padding: 0; background-color: transparent; color: var(--text-primary-color, black); -webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; height: 100%; box-sizing: border-box; overflow-y: auto; }} body::-webkit-scrollbar {{ display: none; }} body {{ -ms-overflow-style: none; scrollbar-width: none; }} ul {{ padding-left: 20px; margin-top: 0.5em; margin-bottom: 0.5em; list-style: disc; }} li {{ margin-bottom: 0.2em; }} div[data-content='notes'] ul, div[data-content='examples'] ul {{ margin-top: 0.2em; list-style: none; padding-left: 5px; }} div[data-content='references'] ul li span[data-content='refGlosses'] {{ font-size: 0.9em; color: var(--text-secondary-color, gray); margin-left: 5px; }} span.internal-link {{ color: var(--system-accent-color, blue); cursor: pointer; text-decoration: underline; }} li.focusable-li {{ outline: none; }} li.current-nav-item {{ background-color: rgba(0, 120, 215, 0.1); outline: 1px dashed rgba(0, 120, 215, 0.3); border-radius: 3px; }} body.definition-focused li.current-nav-item {{ background-color: rgba(0, 120, 215, 0.2); outline: 1px solid rgba(0, 120, 215, 0.5); }} .definition-entry-header {{ font-weight: bold; margin-top: 0.8em; padding-bottom: 0.2em; border-bottom: 1px solid #eee; }} hr {{ border: none; border-top: 1px dashed #ccc; margin: 1em 0; }} </style></head><body id='definition-body'>");

        if (ViewModel.SelectedToken != null)
        {
            string term = System.Security.SecurityElement.Escape(ViewModel.SelectedToken.Surface);
            string reading = System.Security.SecurityElement.Escape(Converters.KatakanaToHiraganaConverter.ToHiraganaStatic(ViewModel.SelectedToken.Reading));
            fullHtml.Append($"<div class='definition-entry-header'>{term} [{reading}]</div>");
        }

        fullHtml.Append(combinedDefinitionHtml);
        fullHtml.Append(@"<script> let focusableItems = []; let currentHighlightIndex = -1; function initializeNavigation() { try { focusableItems = Array.from(document.querySelectorAll('li')); focusableItems.forEach(item => item.classList.add('focusable-li')); currentHighlightIndex = -1; console.log('Initialized definition navigation. Found ' + focusableItems.length + ' items.'); return focusableItems.length; } catch(e) { console.error('Error in initializeNavigation:', e); return 0; } } function highlightListItem(index) { try { if (currentHighlightIndex >= 0 && currentHighlightIndex < focusableItems.length) { focusableItems[currentHighlightIndex].classList.remove('current-nav-item'); } if (index >= 0 && index < focusableItems.length) { const newItem = focusableItems[index]; newItem.classList.add('current-nav-item'); newItem.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' }); currentHighlightIndex = index; console.log('Highlighted definition item index: ' + index); return newItem.querySelector('span.internal-link') !== null; } else { currentHighlightIndex = -1; console.log('Cleared definition item highlight.'); return false; } } catch(e) { console.error('Error in highlightListItem:', e); return false;} } function isCurrentListItemInternalLink(index) { try { if (index >= 0 && index < focusableItems.length) { return focusableItems[index].querySelector('span.internal-link') !== null; } return false; } catch(e) { console.error('Error in isCurrentListItemInternalLink:', e); return false;} } function scrollDefinition(delta) { try { document.body.scrollTop += delta; } catch(e) { console.error('Error in scrollDefinition:', e); } } function setDefinitionFocus(isFocused) { try { const body = document.getElementById('definition-body'); if(isFocused) { body.classList.add('definition-focused'); } else { body.classList.remove('definition-focused'); } } catch(e) { console.error('Error in setDefinitionFocus:', e); } } </script></body></html>");

        TaskCompletionSource<bool> navigationCompletedTcsDef = new TaskCompletionSource<bool>();
        TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandlerDef = null;
        try
        {
            navigationCompletedHandlerDef = (sender, args) => { if (DefinitionWebView?.CoreWebView2 != null) { DefinitionWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandlerDef; } if (args.IsSuccess) { navigationCompletedTcsDef.TrySetResult(true); } else { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Navigation failed: {args.WebErrorStatus}"); navigationCompletedTcsDef.TrySetResult(false); } };
            try { DefinitionWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandlerDef; } catch { }
            DefinitionWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandlerDef;
            System.Diagnostics.Debug.WriteLine("[WebView2 Definition] Navigating...");
            DefinitionWebView.CoreWebView2.NavigateToString(fullHtml.ToString());
            bool navigationSuccessDef = await navigationCompletedTcsDef.Task;
            if (navigationSuccessDef)
            {
                System.Diagnostics.Debug.WriteLine("[WebView2 Definition] Navigation successful. Initializing JS...");
                string scriptInitNav = "initializeNavigation()"; string result = "0";
                try { result = await DefinitionWebView.CoreWebView2.ExecuteScriptAsync(scriptInitNav); }
                catch (Exception initEx) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Error executing init script: {initEx.Message}"); }
                if (!string.IsNullOrEmpty(result) && result != "null" && int.TryParse(result, out int count)) { _definitionLiCount = count; } else { _definitionLiCount = 0; System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Failed parsing item count: '{result}'"); }
                _currentDefinitionLiIndex = -1;
                System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Found {_definitionLiCount} list items.");
                DefinitionWebView.Visibility = Visibility.Visible;
            }
            else { System.Diagnostics.Debug.WriteLine("[WebView2 Definition] Navigation failed."); DefinitionWebView.Visibility = Visibility.Collapsed; }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Error loading: {ex.Message}"); DefinitionWebView.Visibility = Visibility.Collapsed; }
        finally { _isLoadingDefinition = false; System.Diagnostics.Debug.WriteLine("[WebView2 Definition] LoadDefinitionHtmlAsync END"); }
    }


    private async Task HighlightTokenInWebViewAsync(int selectedSelectableIndex)
    {
        if (!_isSentenceWebViewReady || SentenceWebView.CoreWebView2 == null) return;
        int actualIndex = ViewModel.GetActualTokenIndex(selectedSelectableIndex);
        string targetTokenId = (actualIndex >= 0 && actualIndex < ViewModel.Tokens?.Count) ? $"token-{actualIndex}" : "null";
        System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Highlighting: {targetTokenId}. Focus: {ViewModel.DetailFocusTarget}");
        try { await SentenceWebView.CoreWebView2.ExecuteScriptAsync($"highlightToken('{targetTokenId}', '{ViewModel.DetailFocusTarget}');"); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Error highlighting: {ex.Message}"); }
    }

    private async Task HighlightDefinitionItemAsync(int index)
    {
        if (!_isDefinitionWebViewReady || DefinitionWebView.CoreWebView2 == null || _definitionLiCount <= 0) return;
        _currentDefinitionLiIndex = Math.Clamp(index, -1, _definitionLiCount - 1);
        System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Highlighting item: {_currentDefinitionLiIndex}");
        try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"highlightListItem({_currentDefinitionLiIndex});"); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Error highlighting item: {ex.Message}"); }
    }

    private async Task UpdateDetailFocusVisuals()
    {
        if (ViewModel.CurrentViewInternal != "detail") return;
        System.Diagnostics.Debug.WriteLine($"[Focus] Updating focus visuals. Target: {ViewModel.DetailFocusTarget}");

        if (_isSentenceWebViewReady && SentenceWebView.CoreWebView2 != null) { try { await SentenceWebView.CoreWebView2.ExecuteScriptAsync($"setFocusClass('{ViewModel.DetailFocusTarget}');"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Sentence] Error setting focus class: {ex.Message}"); } }
        if (_isDefinitionWebViewReady && DefinitionWebView.CoreWebView2 != null) { try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"setDefinitionFocus({(ViewModel.DetailFocusTarget == "Definition").ToString().ToLowerInvariant()});"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Error setting focus class: {ex.Message}"); } }

        bool focused = SentenceFocusAnchor.Focus(FocusState.Programmatic);
        System.Diagnostics.Debug.WriteLine($"[Focus] Attempted to set UI focus to SentenceFocusAnchor. Success: {focused}");
        if (!focused) { if (DetailViewContentGrid != null) { focused = DetailViewContentGrid.Focus(FocusState.Programmatic); System.Diagnostics.Debug.WriteLine($"[Focus] Fallback: DetailViewContentGrid. Success: {focused}"); } if (!focused && this.Content is FrameworkElement rootElement) { focused = rootElement.Focus(FocusState.Programmatic); System.Diagnostics.Debug.WriteLine($"[Focus] Final Fallback: Root Element. Success: {focused}"); } }
    }

    private void CheckGamepadLocalInput(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_localGamepad == null) return;
        var now = DateTime.UtcNow;
        if (now - _lastGamepadNavTime < _gamepadNavDebounce) return;
        try
        {
            GamepadReading reading = _localGamepad.GetCurrentReading(); bool processed = false; const double triggerThr = 0.75; const double deadzone = 0.7;
            foreach (var kvp in _localButtonToActionId)
            {
                GamepadButtons button = kvp.Key; string actionId = kvp.Value;
                if (reading.Buttons.HasFlag(button)) { System.Diagnostics.Debug.WriteLine($"[Input] Local Button: {button} TRIGGERED Action: {actionId}"); ExecuteLocalAction(actionId); processed = true; break; }
            }
            if (!processed)
            {
                string? triggerId = null; string? ltAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZL").Key; string? rtAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZR").Key;
                if (ltAct != null && reading.LeftTrigger > triggerThr) triggerId = ltAct;
                else if (rtAct != null && reading.RightTrigger > triggerThr) triggerId = rtAct;
                if (triggerId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Local Trigger mapped to Action: {triggerId}"); ExecuteLocalAction(triggerId); processed = true; }
            }
            if (!processed)
            {
                string? axisId = null; double ly = reading.LeftThumbstickY; double lx = reading.LeftThumbstickX;
                if (ly > deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_UP");
                else if (ly < -deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_DOWN");
                else if (lx < -deadzone) axisId = FindActionForAxis("STICK_LEFT_X_LEFT");
                else if (lx > deadzone) axisId = FindActionForAxis("STICK_LEFT_X_RIGHT");
                if (axisId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Local Axis mapped to Action: {axisId}"); ExecuteLocalAction(axisId); processed = true; }
            }
            if (processed) { _lastGamepadNavTime = now; }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Input] Error reading local gamepad: {ex.Message}"); _localGamepad = null; _gamepadPollingTimer?.Stop(); }
    }

    private void MainWindow_Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Key={e.Key}, OriginalSource={e.OriginalSource?.GetType().Name}, Handled={e.Handled}");
        if (e.Handled) { System.Diagnostics.Debug.WriteLine($"[Input] Event already handled by {e.OriginalSource?.GetType().Name}. Skipping."); return; }
        if (_localKeyToActionId.TryGetValue(e.Key, out string? actionId) && actionId != null)
        {
            System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Mapped {e.Key} to Action: {actionId}");
            ExecuteLocalAction(actionId);
            e.Handled = true;
        }
        else
        {
            bool lvFocus = false; XamlRoot? xr = (this.Content as UIElement)?.XamlRoot;
            if (xr != null) { var fe = FocusManager.GetFocusedElement(xr); lvFocus = fe is ListView || fe is ListViewItem; }
            if (e.Key == VirtualKey.Enter && lvFocus)
            {
                System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Enter on ListView(Item). Attempting DETAIL_ENTER.");
                ExecuteLocalAction("DETAIL_ENTER");
                e.Handled = true;
            }
            else { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Key {e.Key} not mapped or handled by specific logic."); e.Handled = false; }
        }
    }

    private async void ExecuteLocalAction(string actionId)
    {
        System.Diagnostics.Debug.WriteLine($"[Action] Attempting Action: '{actionId}' in View: '{ViewModel.CurrentViewInternal}', Focus: '{ViewModel.DetailFocusTarget}'");
        bool handled = false;

        if (ViewModel.CurrentViewInternal == "list")
        {
            switch (actionId) { case "NAV_UP": ViewModel.MoveSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveSelection(1); handled = true; break; case "DETAIL_ENTER": if (ViewModel.SelectedSentenceIndex >= 0 && ViewModel.SelectedSentenceIndex < ViewModel.SentenceHistory.Count) { ViewModel.EnterDetailView(); handled = true; } break; case "DELETE_WORD": handled = true; break; }
        }
        else if (ViewModel.CurrentViewInternal == "detail")
        {
            if (ViewModel.DetailFocusTarget == "Sentence")
            {
                switch (actionId) { case "NAV_LEFT": ViewModel.MoveTokenSelection(-1); handled = true; break; case "NAV_RIGHT": ViewModel.MoveTokenSelection(1); handled = true; break; case "NAV_UP": ViewModel.MoveKanjiTokenSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveKanjiTokenSelection(1); handled = true; break; case "DETAIL_ENTER": ViewModel.FocusDefinitionArea(); handled = true; break; case "DETAIL_BACK": ViewModel.ExitDetailView(); await DispatcherQueue_EnqueueAsync(TrySetFocusToListOrRoot, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low); handled = true; break; case "SAVE_WORD": handled = true; break; }
            }
            else if (ViewModel.DetailFocusTarget == "Definition")
            {
                switch (actionId)
                {
                    case "NAV_UP": if (_definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex - 1); } handled = true; break;
                    case "NAV_DOWN": if (_definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex + 1); } handled = true; break;
                    case "DETAIL_ENTER":
                        if (_currentDefinitionLiIndex >= 0 && _isDefinitionWebViewReady && DefinitionWebView.CoreWebView2 != null)
                        {
                            try
                            {
                                string scriptCheckLink = $"isCurrentListItemInternalLink({_currentDefinitionLiIndex})";
                                string resultIsLink = await DefinitionWebView.CoreWebView2.ExecuteScriptAsync(scriptCheckLink);
                                if (resultIsLink != "null" && bool.TryParse(resultIsLink.ToLowerInvariant(), out bool isLink) && isLink)
                                {
                                    string scriptGetLookup = $"(function() {{ try {{ const item = focusableItems[{_currentDefinitionLiIndex}]; const linkSpan = item ? item.querySelector('span.internal-link') : null; return linkSpan ? linkSpan.getAttribute('data-lookup') : null; }} catch(e) {{ return null; }} }})();";
                                    string lookupTermJson = await DefinitionWebView.CoreWebView2.ExecuteScriptAsync(scriptGetLookup);
                                    string? lookupTerm = null; try { if (lookupTermJson != "null") lookupTerm = JsonSerializer.Deserialize<string?>(lookupTermJson); } catch { }
                                    if (!string.IsNullOrEmpty(lookupTerm)) { System.Diagnostics.Debug.WriteLine($"[Action] TODO: Trigger internal link lookup for term '{lookupTerm}' from item index {_currentDefinitionLiIndex}"); }
                                    else { System.Diagnostics.Debug.WriteLine($"[Action] Could not extract lookup term from item index {_currentDefinitionLiIndex}"); }
                                }
                                else { System.Diagnostics.Debug.WriteLine($"[Action] Definition item {_currentDefinitionLiIndex} activated, but not an internal link."); }
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Action] Error checking/activating definition item: {ex.Message}"); }
                        }
                        handled = true; break;
                    case "DETAIL_BACK": ViewModel.FocusSentenceArea(); handled = true; break;
                }
            }
        }
        if (!handled) { System.Diagnostics.Debug.WriteLine($"[Action] Action '{actionId}' ignored or unknown."); }
        if (handled && ViewModel.CurrentViewInternal == "list" && actionId.StartsWith("NAV_") && ViewModel.SelectedSentenceItem != null) { await DispatcherQueue_EnqueueAsync(() => { if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem); }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low); }
    }

    private void reset_navigation_state() => _lastGamepadNavTime = DateTime.MinValue;
    private GamepadButtons? MapInternalCodeToGamepadButton(string code) => code.ToUpperInvariant() switch { "BTN_A" => GamepadButtons.A, "BTN_B" => GamepadButtons.B, "BTN_X" => GamepadButtons.X, "BTN_Y" => GamepadButtons.Y, "BTN_TL" => GamepadButtons.LeftShoulder, "BTN_TR" => GamepadButtons.RightShoulder, "BTN_SELECT" => GamepadButtons.View, "BTN_START" => GamepadButtons.Menu, "BTN_THUMBL" => GamepadButtons.LeftThumbstick, "BTN_THUMBR" => GamepadButtons.RightThumbstick, "DPAD_UP" => GamepadButtons.DPadUp, "DPAD_DOWN" => GamepadButtons.DPadDown, "DPAD_LEFT" => GamepadButtons.DPadLeft, "DPAD_RIGHT" => GamepadButtons.DPadRight, _ => null };
    private string? FindActionForAxis(string axis) => _currentSettings.LocalJoystickBindings switch { null => null, var b => axis switch { "STICK_LEFT_Y_UP" => b.ContainsValue("NAV_UP") ? "NAV_UP" : null, "STICK_LEFT_Y_DOWN" => b.ContainsValue("NAV_DOWN") ? "NAV_DOWN" : null, "STICK_LEFT_X_LEFT" => b.ContainsValue("NAV_LEFT") ? "NAV_LEFT" : null, "STICK_LEFT_X_RIGHT" => b.ContainsValue("NAV_RIGHT") ? "NAV_RIGHT" : null, _ => null } };
    private void SetupGamepadPollingTimer()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq == null) return;
        _gamepadPollingTimer = dq.CreateTimer();
        _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50);
        _gamepadPollingTimer.Tick += CheckGamepadLocalInput;
    }
    private void TryHookCorrectGamepad() { Gamepad? prev = _localGamepad; _localGamepad = null; Gamepad? pot = null; var gamepads = Gamepad.Gamepads; foreach (var gp in gamepads) { RawGameController? raw = null; try { raw = RawGameController.FromGameController(gp); } catch { } bool isRawPro = raw != null && raw.HardwareVendorId == 0x057E; if (!isRawPro) { pot = gp; break; } else if (pot == null) { pot = gp; } } _localGamepad = pot; if (_localGamepad != null) { if (_localGamepad != prev) { try { string name = RawGameController.FromGameController(_localGamepad)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Local gamepad hooked: {name}"); } catch { } } try { _ = _localGamepad.GetCurrentReading(); } catch { _localGamepad = null; } } else { System.Diagnostics.Debug.WriteLine("[Input] No suitable local gamepad detected."); } }
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) { if (args.WindowActivationState == WindowActivationState.Deactivated) { PauseLocalPolling(); reset_navigation_state(); } else { TryHookCorrectGamepad(); ResumeLocalPolling(); } }
    private void Gamepad_GamepadAdded(object? sender, Gamepad e) { try { string name = RawGameController.FromGameController(e)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Gamepad added (WinRT): {name}. Re-evaluating hook."); } catch { } TryHookCorrectGamepad(); ResumeLocalPolling(); }
    private void Gamepad_GamepadRemoved(object? sender, Gamepad e) { try { string name = RawGameController.FromGameController(e)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Gamepad removed (WinRT): {name}. Re-evaluating hook."); } catch { } TryHookCorrectGamepad(); if (_localGamepad == null) PauseLocalPolling(); }
    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListView { SelectedItem: not null } lv) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (lv.SelectedItem != null) lv.ScrollIntoView(lv.SelectedItem); }); } }
    private void MainWindow_Closed(object sender, WindowEventArgs args) { _highlightThrottleTimer?.Stop(); ViewModel.PropertyChanged -= ViewModel_PropertyChanged; ViewModel.RequestLoadDefinitionHtml -= ViewModel_RequestLoadDefinitionHtml; PauseLocalPolling(); Gamepad.GamepadAdded -= Gamepad_GamepadAdded; Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved; SentenceWebView?.Close(); DefinitionWebView?.Close(); ViewModel?.Cleanup(); _localGamepad = null; _gamepadPollingTimer = null; System.Diagnostics.Debug.WriteLine("[MainWindow] Closed event handled."); }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
    private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args) { System.Diagnostics.Debug.WriteLine("[MainWindow] Detected SettingsWindow closed."); if (sender is SettingsWindow closedWindow) { closedWindow.Closed -= SettingsWindow_Instance_Closed; } _settingsWindowInstance = null; ResumeLocalPolling(); TrySetFocusToListOrRoot(); }
    private void TrySetFocusToListOrRoot() { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { System.Diagnostics.Debug.WriteLine($"[Focus] Attempting to set focus after view change/window close..."); bool fs = HistoryListView.Focus(FocusState.Programmatic); if (!fs && this.Content is FrameworkElement root) { fs = root.Focus(FocusState.Programmatic); } System.Diagnostics.Debug.WriteLine($"[Focus] Focus set attempt result: {fs}"); }); }
    public void UpdateLocalBindings(AppSettings settings) { System.Diagnostics.Debug.WriteLine("[MainWindow] === Updating local bindings START ==="); _currentSettings = settings ?? new AppSettings(); _localKeyToActionId.Clear(); _localButtonToActionId.Clear(); if (_currentSettings.LocalKeyboardBindings != null) { foreach (var kvp in _currentSettings.LocalKeyboardBindings) { if (!string.IsNullOrEmpty(kvp.Value) && Enum.TryParse<VirtualKey>(kvp.Value, true, out var vk)) { _localKeyToActionId[vk] = kvp.Key; } } } if (_currentSettings.LocalJoystickBindings != null) { foreach (var kvp in _currentSettings.LocalJoystickBindings) { string actionId = kvp.Key; string? buttonCode = kvp.Value; if (!string.IsNullOrEmpty(buttonCode)) { GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode); if (buttonEnum.HasValue) { _localButtonToActionId[buttonEnum.Value] = actionId; } } } } System.Diagnostics.Debug.WriteLine("[MainWindow] === Finished updating local bindings ==="); reset_navigation_state(); }
    public void ToggleVisibility() => DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
    public void TriggerMenuToggle() => DispatcherQueue?.TryEnqueue(() => { if (ViewModel != null) { if (ViewModel.CurrentViewInternal != "menu") ViewModel.EnterMenu(); else ViewModel.ExitMenu(); } else { System.Diagnostics.Debug.WriteLine("[MainWindow] TriggerMenuToggle called but ViewModel is null."); } });
    public void PauseLocalPolling() { _gamepadPollingTimer?.Stop(); System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling PAUSED explicitly."); }
    public void ResumeLocalPolling() { DispatcherQueue?.TryEnqueue(() => { bool isActive = false; try { HWND fWnd = PInvoke.GetForegroundWindow(); HWND myWnd = (HWND)WindowNative.GetWindowHandle(this); isActive = fWnd == myWnd; } catch { isActive = false; } if (isActive && _localGamepad != null && _gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning) { _gamepadPollingTimer.Start(); System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling RESUMED explicitly."); } }); }
    public void ShowSettingsWindow() { if (_settingsWindowInstance != null) { _settingsWindowInstance.Activate(); return; } _settingsWindowInstance = new SettingsWindow(); _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed; PauseLocalPolling(); _settingsWindowInstance.Activate(); System.Diagnostics.Debug.WriteLine("[MainWindow] Settings window opened."); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private Task DispatcherQueue_EnqueueAsync(Action function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (DispatcherQueue.TryEnqueue(priority, () => { try { function(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { } else { tcs.SetException(new InvalidOperationException("Failed to enqueue action.")); } return tcs.Task; }
    private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (DispatcherQueue.TryEnqueue(priority, async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { } else { tcs.SetException(new InvalidOperationException("Failed to enqueue async action.")); } return tcs.Task; }
}