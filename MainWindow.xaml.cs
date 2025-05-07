// MainWindow.xaml.cs
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
using JpnStudyTool.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Gaming.Input;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;


namespace JpnStudyTool
{
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        public MainWindowViewModel ViewModel { get; }
        private readonly WindowActivationService _windowActivationService;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

        private Frame? contentFrame;
        private Grid? rootGrid;

        private Gamepad? _localGamepad = null;
        private DateTime _lastGamepadNavTime = DateTime.MinValue;
        private readonly TimeSpan _gamepadDebounce = TimeSpan.FromMilliseconds(200);
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

        public MainWindow()
        {
            this.InitializeComponent();

            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Cannot get DispatcherQueue for current thread in MainWindow.");
            _windowActivationService = new WindowActivationService();
            IntPtr hwndPtr = WindowNative.GetWindowHandle(this);
            _windowActivationService.RegisterWindowHandle(hwndPtr);

            ViewModel = new MainWindowViewModel();

            if (this.Content is Grid grid)
            {
                rootGrid = grid;
                rootGrid.DataContext = ViewModel;
                contentFrame = rootGrid.FindName("ContentFrameElement") as Frame;
                SessionViewContainer = rootGrid.FindName("SessionViewContainer") as Grid;
                if (SessionViewContainer != null)
                {
                    var listGrid = SessionViewContainer.FindName("ListViewContentGrid") as Grid;
                    if (listGrid != null) { HistoryListView = listGrid.FindName("HistoryListView") as ListView; }

                    DetailViewContentGrid = SessionViewContainer.FindName("DetailViewContentGrid") as Grid;
                    if (DetailViewContentGrid != null)
                    {
                        SentenceFocusAnchor = DetailViewContentGrid.FindName("SentenceFocusAnchor") as Border;
                        SentenceWebView = DetailViewContentGrid.FindName("SentenceWebView") as WebView2;
                        DefinitionWebView = DetailViewContentGrid.FindName("DefinitionWebView") as WebView2;
                    }
                }

                if (contentFrame == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] CRITICAL Error: ContentFrameElement not found."); }
                if (HistoryListView == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] WARNING: HistoryListView not found."); }
                if (SentenceWebView == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] WARNING: SentenceWebView not found."); }
                if (DefinitionWebView == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] WARNING: DefinitionWebView not found."); }
                if (SentenceFocusAnchor == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] WARNING: SentenceFocusAnchor not found."); }


                rootGrid.PreviewKeyDown += MainWindow_Global_PreviewKeyDown;
                rootGrid.Loaded += MainWindow_Loaded;
            }
            else { System.Diagnostics.Debug.WriteLine("[MainWindow] CRITICAL Error: Root content is not a Grid."); }

            ViewModel.RequestLoadDefinitionHtml += ViewModel_RequestLoadDefinitionHtml;
            ViewModel.RequestRenderSentenceWebView += ViewModel_RequestRenderSentenceWebView;
            ViewModel.RequestUpdateDefinitionTokenDetails += ViewModel_RequestUpdateDefinitionTokenDetails;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            this.Title = "Jpn Study Tool";
            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated;

            Gamepad.GamepadAdded += Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
            TryHookCorrectGamepad();
            SetupGamepadPollingTimer();
            InitializeHighlightThrottleTimer();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) { NavigateToHub(); }

        public void NavigateToHub()
        {
            if (contentFrame == null)
            {
                System.Diagnostics.Debug.WriteLine("[MainWindow NavigateToHub] Error: contentFrame is null.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[MainWindow NavigateToHub] CurrentSourcePageType: {contentFrame.CurrentSourcePageType?.Name ?? "null"}, Target: {nameof(MainHubPage)}");

            contentFrame.Navigate(typeof(MainHubPage), null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());

            System.Diagnostics.Debug.WriteLine("[MainWindow NavigateToHub] Navigation call made to MainHubPage.");
        }

        public async Task NavigateToSessionViewAsync()
        {
            if (contentFrame == null) return;
            await ViewModel.LoadCurrentSessionDataAsync();
            ViewModel.IsHubViewActive = false;
            System.Diagnostics.Debug.WriteLine("[MainWindow] Navigated TO Session View (List)");
            TrySetFocusToListOrRoot();
        }

        private async Task ViewModel_RequestLoadDefinitionHtml(string? fullHtmlContent) { await LoadDefinitionHtmlAsync(fullHtmlContent); }
        private async Task ViewModel_RequestRenderSentenceWebView(List<DisplayToken> displayTokens) { await LoadSentenceDataAsync(displayTokens); }
        private async Task ViewModel_RequestUpdateDefinitionTokenDetails(string tokenDetailHtml) { await UpdateDefinitionTokenDetailsAsync(tokenDetailHtml); }

        private void SentenceWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args) { if (args.Exception != null) { System.Diagnostics.Debug.WriteLine($"[MW SentenceWebView Init Error] {args.Exception.Message}"); _isSentenceWebViewReady = false; return; } _isSentenceWebViewReady = true; if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; } else { _isSentenceWebViewReady = false; return; } System.Diagnostics.Debug.WriteLine("[MW SentenceWebView] Initialized."); }
        private void DefinitionWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args) { if (args.Exception != null) { System.Diagnostics.Debug.WriteLine($"[MW DefinitionWebView Init Error] {args.Exception.Message}"); _isDefinitionWebViewReady = false; return; } _isDefinitionWebViewReady = true; if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; } else { _isDefinitionWebViewReady = false; return; } System.Diagnostics.Debug.WriteLine("[MW DefinitionWebView] Initialized."); }

        private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ViewModel.SelectedTokenIndex)) { int newIndex = ViewModel.SelectedTokenIndex; _pendingHighlightIndex = newIndex; var now = DateTime.UtcNow; var timeSinceLastHighlight = now - _lastHighlightTime; if (timeSinceLastHighlight >= _highlightThrottleInterval) { _highlightThrottleTimer?.Stop(); _isHighlightUpdatePending = false; await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(newIndex)); _lastHighlightTime = DateTime.UtcNow; } else if (!_isHighlightUpdatePending) { var delayNeeded = _highlightThrottleInterval - timeSinceLastHighlight; _isHighlightUpdatePending = true; if (_highlightThrottleTimer != null) { _highlightThrottleTimer.Interval = delayNeeded > TimeSpan.Zero ? delayNeeded : TimeSpan.FromMilliseconds(1); _highlightThrottleTimer.Start(); } } } else if (e.PropertyName == nameof(ViewModel.DetailFocusTarget)) { await DispatcherQueue_EnqueueAsync(UpdateDetailFocusVisuals); } }
        private void InitializeHighlightThrottleTimer() { var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); if (dq != null) { _highlightThrottleTimer = dq.CreateTimer(); _highlightThrottleTimer.IsRepeating = false; _highlightThrottleTimer.Tick += HighlightThrottleTimer_Tick; } }
        private async void HighlightThrottleTimer_Tick(object? sender, object e) { _highlightThrottleTimer?.Stop(); _isHighlightUpdatePending = false; await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(_pendingHighlightIndex)); _lastHighlightTime = DateTime.UtcNow; }

        private async Task LoadSentenceDataAsync(List<DisplayToken> displayTokens)
        {
            if (!_isSentenceWebViewReady || SentenceWebView?.CoreWebView2 == null) { System.Diagnostics.Debug.WriteLine("[MW LoadSentence] WebView not ready or null."); return; }
            if (displayTokens == null || !displayTokens.Any()) { try { SentenceWebView.CoreWebView2.Navigate("about:blank"); System.Diagnostics.Debug.WriteLine("[MW LoadSentence] No tokens, clearing."); } catch { } return; }
            StringBuilder htmlBuilder = new StringBuilder();
            htmlBuilder.Append(@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"); htmlBuilder.Append(@"html, body { height: 100%; margin: 0; padding: 0; box-sizing: border-box; overflow: hidden; } "); htmlBuilder.Append(@"body { "); htmlBuilder.Append(@"font-family: 'Segoe UI', sans-serif; "); htmlBuilder.Append($"font-size: {this.SentenceFontSize}px; "); htmlBuilder.Append(@"line-height: 1.5; margin: 5px; padding: 0; "); htmlBuilder.Append(@"height: 100%; "); htmlBuilder.Append(@"overflow-wrap: break-word; word-wrap: break-word; "); htmlBuilder.Append(@"background-color: transparent; color: black; "); htmlBuilder.Append(@"-webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; "); htmlBuilder.Append(@"overflow-y: auto; "); htmlBuilder.Append(@"scrollbar-width: none; "); htmlBuilder.Append(@"-ms-overflow-style: none; "); htmlBuilder.Append(@"} "); htmlBuilder.Append(@"body::-webkit-scrollbar { display: none; } "); htmlBuilder.Append(@".token-span { cursor: default; padding: 0 1px; display: inline; border-radius: 3px; transition: background-color 0.1s ease-in-out, color 0.1s ease-in-out; } "); htmlBuilder.Append(@".selectable { }"); htmlBuilder.Append(@".non-selectable { color: #999; }"); htmlBuilder.Append(@".highlighted-sentence { background-color: LightSkyBlue; } "); htmlBuilder.Append(@".highlighted-definition { background-color: #406080; color: white; } "); htmlBuilder.Append(@".focus-sentence .highlighted-sentence { } "); htmlBuilder.Append(@".focus-definition .highlighted-definition { } "); htmlBuilder.Append(@"</style></head><body id='sentence-body' class='focus-sentence'>"); for (int i = 0; i < displayTokens.Count; i++) { var token = displayTokens[i]; string escapedSurface = System.Security.SecurityElement.Escape(token.Surface) ?? string.Empty; string selectableClass = token.IsSelectable ? "selectable" : "non-selectable"; htmlBuilder.Append($"<span id='token-{i}' class='token-span {selectableClass}'>{escapedSurface}</span>"); }
            htmlBuilder.Append(@"<script>let currentHighlightId = null; let currentHighlightClass = ''; function highlightToken(tokenId, focusTarget) { try { const newElement = document.getElementById(tokenId); const body = document.getElementById('sentence-body'); let highlightClass = ''; if (focusTarget === 'Sentence') { highlightClass = 'highlighted-sentence'; body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); } else { highlightClass = 'highlighted-definition'; body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); } if (currentHighlightId) { const oldElement = document.getElementById(currentHighlightId); if (oldElement) { oldElement.classList.remove('highlighted-sentence', 'highlighted-definition'); } } if (newElement) { newElement.classList.add(highlightClass); currentHighlightId = tokenId; currentHighlightClass = highlightClass; if (focusTarget === 'Sentence') { newElement.scrollIntoView({ behavior: 'auto', block: 'nearest', inline: 'nearest' }); } } else { currentHighlightId = null; currentHighlightClass = ''; } } catch(e) {}} function setFocusClass(focusTarget) { try { const body = document.getElementById('sentence-body'); const currentElement = currentHighlightId ? document.getElementById(currentHighlightId) : null; let newHighlightClass = ''; if (focusTarget === 'Sentence') { body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); newHighlightClass = 'highlighted-sentence'; } else { body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); newHighlightClass = 'highlighted-definition'; } if (currentElement && currentHighlightClass !== newHighlightClass) { currentElement.classList.remove(currentHighlightClass); currentElement.classList.add(newHighlightClass); currentHighlightClass = newHighlightClass; } } catch(e) {}}</script></body></html>");

            TaskCompletionSource<bool> navigationCompletedTcs = new TaskCompletionSource<bool>();
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandler = null;
            try
            {
                navigationCompletedHandler = (s, a) => { try { if (s != null) s.NavigationCompleted -= navigationCompletedHandler; navigationCompletedTcs?.TrySetResult(a.IsSuccess); } catch { navigationCompletedTcs?.TrySetResult(false); } };
                SentenceWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandler;
                SentenceWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandler;
                SentenceWebView.CoreWebView2.NavigateToString(htmlBuilder.ToString());
                bool navigationSuccess = await navigationCompletedTcs.Task;
                if (navigationSuccess) { await HighlightTokenInWebViewAsync(ViewModel.SelectedTokenIndex); }
                else { System.Diagnostics.Debug.WriteLine("[MW LoadSentence] Navigation failed."); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MW LoadSentence] Exception: {ex.Message}"); navigationCompletedTcs?.TrySetResult(false); }
        }

        private async Task LoadDefinitionHtmlAsync(string? fullDefinitionHtml)
        {
            _definitionLiCount = 0; _currentDefinitionLiIndex = -1;
            if (!_isDefinitionWebViewReady || DefinitionWebView?.CoreWebView2 == null) { System.Diagnostics.Debug.WriteLine("[MW LoadDef] WebView not ready or null."); return; }
            if (string.IsNullOrWhiteSpace(fullDefinitionHtml)) { try { DefinitionWebView.CoreWebView2.Navigate("about:blank"); System.Diagnostics.Debug.WriteLine("[MW LoadDef] No HTML, clearing."); } catch { } return; }
            TaskCompletionSource<bool> navigationCompletedTcsDef = new TaskCompletionSource<bool>();
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandlerDef = null;
            try
            {
                navigationCompletedHandlerDef = (s, a) => { try { if (s != null) s.NavigationCompleted -= navigationCompletedHandlerDef; navigationCompletedTcsDef?.TrySetResult(a.IsSuccess); } catch { navigationCompletedTcsDef?.TrySetResult(false); } };
                DefinitionWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandlerDef;
                DefinitionWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandlerDef;
                DefinitionWebView.CoreWebView2.NavigateToString(fullDefinitionHtml);
                bool navigationSuccessDef = await navigationCompletedTcsDef.Task;
                if (navigationSuccessDef)
                {
                    string scriptInitNav = "initializeNavigation()"; string result = "0"; try { result = await DefinitionWebView.CoreWebView2.ExecuteScriptAsync(scriptInitNav); } catch (Exception scriptEx) { System.Diagnostics.Debug.WriteLine($"[MW LoadDef] Error executing init script: {scriptEx.Message}"); result = "0"; }
                    if (!string.IsNullOrEmpty(result) && result != "null" && int.TryParse(result, out int count)) { _definitionLiCount = count; } else { _definitionLiCount = 0; }
                    _currentDefinitionLiIndex = -1;
                }
                else { System.Diagnostics.Debug.WriteLine("[MW LoadDef] Navigation failed."); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MW LoadDef] Exception: {ex.Message}"); navigationCompletedTcsDef?.TrySetResult(false); }
        }

        private async Task UpdateDefinitionTokenDetailsAsync(string tokenDetailHtmlSnippet) { if (!_isDefinitionWebViewReady || DefinitionWebView?.CoreWebView2 == null) return; try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"updateTokenDetails({JsonSerializer.Serialize(tokenDetailHtmlSnippet)});"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Error executing token update script: {ex.Message}"); ViewModel.ForceFullDefinitionReload(); await ViewModel.UpdateDefinitionViewAsync(); } }
        private async Task HighlightTokenInWebViewAsync(int displayTokenIndex) { if (!_isSentenceWebViewReady || SentenceWebView?.CoreWebView2 == null) return; string targetTokenId = $"token-{displayTokenIndex}"; if (displayTokenIndex < 0 || displayTokenIndex >= ViewModel.DisplayTokens.Count) { targetTokenId = "null"; } try { await SentenceWebView.CoreWebView2.ExecuteScriptAsync($"highlightToken('{targetTokenId}', '{ViewModel.DetailFocusTarget}');"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HighlightToken JS Error] {ex.Message}"); } }
        private async Task HighlightDefinitionItemAsync(int index) { if (!_isDefinitionWebViewReady || DefinitionWebView?.CoreWebView2 == null || _definitionLiCount <= 0) return; _currentDefinitionLiIndex = Math.Clamp(index, -1, _definitionLiCount - 1); try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"highlightListItem({_currentDefinitionLiIndex});"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HighlightDef JS Error] {ex.Message}"); } }
        private async Task UpdateDetailFocusVisuals() { if (SessionViewContainer?.Visibility != Visibility.Visible || !ViewModel.IsDetailViewActive) return; if (_isSentenceWebViewReady && SentenceWebView?.CoreWebView2 != null) { try { await SentenceWebView.CoreWebView2.ExecuteScriptAsync($"setFocusClass('{ViewModel.DetailFocusTarget}');"); } catch { } } if (_isDefinitionWebViewReady && DefinitionWebView?.CoreWebView2 != null) { try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"setDefinitionFocus({(ViewModel.DetailFocusTarget == "Definition").ToString().ToLowerInvariant()});"); } catch { } } bool focused = SentenceFocusAnchor?.Focus(FocusState.Programmatic) ?? false; if (!focused) { if (DetailViewContentGrid != null) { focused = DetailViewContentGrid.Focus(FocusState.Programmatic); } if (!focused && this.Content is FrameworkElement rootElement) { focused = rootElement.Focus(FocusState.Programmatic); } } }

        private void CheckGamepadLocalInput(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) { if (_localGamepad == null || ViewModel.IsHubViewActive) return; var now = DateTime.UtcNow; if (now - _lastGamepadNavTime < _gamepadDebounce) return; try { GamepadReading reading = _localGamepad.GetCurrentReading(); bool processed = false; const double triggerThr = 0.75; const double deadzone = 0.7; foreach (var kvp in _localButtonToActionId) { GamepadButtons button = kvp.Key; string actionId = kvp.Value; if (reading.Buttons.HasFlag(button)) { ExecuteLocalAction(actionId); processed = true; break; } } if (!processed) { string? triggerId = null; string? ltAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZL").Key; string? rtAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZR").Key; if (ltAct != null && reading.LeftTrigger > triggerThr) triggerId = ltAct; else if (rtAct != null && reading.RightTrigger > triggerThr) triggerId = rtAct; if (triggerId != null) { ExecuteLocalAction(triggerId); processed = true; } } if (!processed) { string? axisId = null; double ly = reading.LeftThumbstickY; double lx = reading.LeftThumbstickX; if (ly > deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_UP"); else if (ly < -deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_DOWN"); else if (lx < -deadzone) axisId = FindActionForAxis("STICK_LEFT_X_LEFT"); else if (lx > deadzone) axisId = FindActionForAxis("STICK_LEFT_X_RIGHT"); if (axisId != null) { ExecuteLocalAction(axisId); processed = true; } } if (processed) { _lastGamepadNavTime = now; } } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CheckGamepadLocalInput Error] {ex.Message}"); _localGamepad = null; _gamepadPollingTimer?.Stop(); TryHookCorrectGamepad(); } }
        private void MainWindow_Global_PreviewKeyDown(object sender, KeyRoutedEventArgs e) { bool isInSessionView = !ViewModel.IsHubViewActive; bool isInDetailSubView = ViewModel.IsDetailViewActive; if (isInSessionView) { if (isInDetailSubView && !ViewModel.CanInteractWithDetailView && e.Key != VirtualKey.Escape) { e.Handled = true; return; } var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control); var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift); bool ctrlPressed = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); bool shiftPressed = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); if (ctrlPressed && shiftPressed && e.Key == VirtualKey.D) { if (_isDefinitionWebViewReady && DefinitionWebView?.CoreWebView2 != null) { DefinitionWebView.CoreWebView2.OpenDevToolsWindow(); e.Handled = true; return; } } if (ctrlPressed && shiftPressed && e.Key == VirtualKey.S) { if (_isSentenceWebViewReady && SentenceWebView?.CoreWebView2 != null) { SentenceWebView.CoreWebView2.OpenDevToolsWindow(); e.Handled = true; return; } } if (e.Handled) return; if (_localKeyToActionId.TryGetValue(e.Key, out string? actionId) && actionId != null) { ExecuteLocalAction(actionId); e.Handled = true; } else if (!isInDetailSubView && e.Key == VirtualKey.Enter) { var focusedElement = FocusManager.GetFocusedElement(this.Content?.XamlRoot); DependencyObject? parent = focusedElement as DependencyObject; while (parent != null && !(parent is ListView)) { parent = VisualTreeHelper.GetParent(parent); } if (parent == HistoryListView) { ExecuteLocalAction("DETAIL_ENTER"); e.Handled = true; } } } if (!e.Handled && e.Key == VirtualKey.Escape) { if (isInDetailSubView) { ExecuteLocalAction("DETAIL_BACK"); e.Handled = true; } else if (isInSessionView) { ExecuteLocalAction("DETAIL_BACK"); e.Handled = true; } } }
        private async void ExecuteLocalAction(string actionId)
        {
            if (ViewModel.IsDetailViewActive && !ViewModel.CanInteractWithDetailView && actionId != "DETAIL_BACK") return;

            bool handled = false;
            bool isInSessionView = !ViewModel.IsHubViewActive;
            bool isInDetailSubView = ViewModel.IsDetailViewActive;

            System.Diagnostics.Debug.WriteLine($"[ExecuteLocalAction] Action: {actionId}, IsInSessionView: {isInSessionView}, IsInDetailSubView: {isInDetailSubView}, DetailFocus: {ViewModel.DetailFocusTarget}");

            if (isInSessionView && !isInDetailSubView)
            {
                switch (actionId)
                {
                    case "NAV_UP": ViewModel.MoveSelection(-1); handled = true; break;
                    case "NAV_DOWN": ViewModel.MoveSelection(1); handled = true; break;
                    case "DETAIL_ENTER":
                        if (ViewModel.SelectedSentenceIndex >= 0 && ViewModel.SelectedSentenceIndex < ViewModel.CurrentSessionEntries.Count)
                        { ViewModel.EnterDetailView(); handled = true; }
                        break;
                    case "DELETE_WORD": System.Diagnostics.Debug.WriteLine($"[ExecuteLocalAction] TODO: Implement Delete Word Action"); handled = true; break;
                    case "DETAIL_BACK":
                        await ViewModel.GoBackToHubCommand.ExecuteAsync(null);
                        handled = true; break;
                }
            }
            else if (isInSessionView && isInDetailSubView)
            {
                if (ViewModel.DetailFocusTarget == "Sentence")
                {
                    switch (actionId)
                    {
                        case "NAV_LEFT": ViewModel.MoveTokenSelection(-1); handled = true; break;
                        case "NAV_RIGHT": ViewModel.MoveTokenSelection(1); handled = true; break;
                        case "NAV_UP": ViewModel.MoveKanjiTokenSelection(-1); handled = true; break;
                        case "NAV_DOWN": ViewModel.MoveKanjiTokenSelection(1); handled = true; break;
                        case "DETAIL_ENTER": ViewModel.FocusDefinitionArea(); handled = true; break;
                        case "DETAIL_BACK":
                            ViewModel.ExitDetailView();
                            await DispatcherQueue_EnqueueAsync(TrySetFocusToListOrRoot, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
                            handled = true; break;
                        case "SAVE_WORD": System.Diagnostics.Debug.WriteLine($"[ExecuteLocalAction] TODO: Implement Save Word Action"); handled = true; break;
                    }
                }
                else if (ViewModel.DetailFocusTarget == "Definition")
                {
                    if (actionId == "DETAIL_BACK") { ViewModel.FocusSentenceArea(); handled = true; }
                    else if (!ViewModel.IsAIModeActive)
                    {
                        switch (actionId) { case "NAV_UP": if (_definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex - 1); } handled = true; break; case "NAV_DOWN": if (_definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex + 1); } handled = true; break; case "DETAIL_ENTER": if (_currentDefinitionLiIndex >= 0 && _isDefinitionWebViewReady && DefinitionWebView?.CoreWebView2 != null) { System.Diagnostics.Debug.WriteLine($"[ExecuteLocalAction] TODO: Implement JMDict internal link logic for index {_currentDefinitionLiIndex}"); } handled = true; break; }
                    }
                }
            }

            if (handled && isInSessionView && !isInDetailSubView && actionId.StartsWith("NAV_") && ViewModel.SelectedSentenceItem != null)
            {
                await DispatcherQueue_EnqueueAsync(() =>
                {
                    if (ViewModel.SelectedSentenceItem != null && HistoryListView != null)
                    { HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem); }
                }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
            }
        }

        private void reset_navigation_state() => _lastGamepadNavTime = DateTime.MinValue;
        private GamepadButtons? MapInternalCodeToGamepadButton(string code) => code.ToUpperInvariant() switch { "BTN_A" => GamepadButtons.A, "BTN_B" => GamepadButtons.B, "BTN_X" => GamepadButtons.X, "BTN_Y" => GamepadButtons.Y, "BTN_TL" => GamepadButtons.LeftShoulder, "BTN_TR" => GamepadButtons.RightShoulder, "BTN_SELECT" => GamepadButtons.View, "BTN_START" => GamepadButtons.Menu, "BTN_THUMBL" => GamepadButtons.LeftThumbstick, "BTN_THUMBR" => GamepadButtons.RightThumbstick, "DPAD_UP" => GamepadButtons.DPadUp, "DPAD_DOWN" => GamepadButtons.DPadDown, "DPAD_LEFT" => GamepadButtons.DPadLeft, "DPAD_RIGHT" => GamepadButtons.DPadRight, _ => null };
        private string? FindActionForAxis(string axis) => _currentSettings.LocalJoystickBindings switch { null => null, var b => axis switch { "STICK_LEFT_Y_UP" => b.FirstOrDefault(kvp => kvp.Value == "NAV_UP").Key, "STICK_LEFT_Y_DOWN" => b.FirstOrDefault(kvp => kvp.Value == "NAV_DOWN").Key, "STICK_LEFT_X_LEFT" => b.FirstOrDefault(kvp => kvp.Value == "NAV_LEFT").Key, "STICK_LEFT_X_RIGHT" => b.FirstOrDefault(kvp => kvp.Value == "NAV_RIGHT").Key, _ => null } };
        private void SetupGamepadPollingTimer() { var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); if (dq == null) return; _gamepadPollingTimer = dq.CreateTimer(); _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50); _gamepadPollingTimer.Tick += CheckGamepadLocalInput; }
        private void TryHookCorrectGamepad() { Gamepad? prev = _localGamepad; _localGamepad = null; Gamepad? pot = null; var gamepads = Gamepad.Gamepads; foreach (var gp in gamepads) { RawGameController? raw = null; try { raw = RawGameController.FromGameController(gp); } catch { } bool isRawPro = raw != null && raw.HardwareVendorId == 0x057E; if (!isRawPro) { pot = gp; break; } else if (pot == null) { pot = gp; } } _localGamepad = pot; if (_localGamepad != null) { try { _ = _localGamepad.GetCurrentReading(); } catch { _localGamepad = null; } } }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) { if (args.WindowActivationState == WindowActivationState.Deactivated) { PauseLocalPolling(); reset_navigation_state(); } else { TryHookCorrectGamepad(); ResumeLocalPolling(); } }
        private void Gamepad_GamepadAdded(object? sender, Gamepad e) { TryHookCorrectGamepad(); ResumeLocalPolling(); }
        private void Gamepad_GamepadRemoved(object? sender, Gamepad e) { TryHookCorrectGamepad(); if (_localGamepad == null) PauseLocalPolling(); }
        private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListView { SelectedItem: not null } lv) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (lv.SelectedItem != null) lv.ScrollIntoView(lv.SelectedItem); }); } }
        private void MainWindow_Closed(object sender, WindowEventArgs args) { _highlightThrottleTimer?.Stop(); if (ViewModel != null) { ViewModel.PropertyChanged -= ViewModel_PropertyChanged; ViewModel.RequestLoadDefinitionHtml -= ViewModel_RequestLoadDefinitionHtml; ViewModel.RequestRenderSentenceWebView -= ViewModel_RequestRenderSentenceWebView; ViewModel.RequestUpdateDefinitionTokenDetails -= ViewModel_RequestUpdateDefinitionTokenDetails; ViewModel.Cleanup(); } PauseLocalPolling(); Gamepad.GamepadAdded -= Gamepad_GamepadAdded; Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved; SentenceWebView?.Close(); DefinitionWebView?.Close(); _localGamepad = null; _gamepadPollingTimer = null; if (rootGrid != null) { rootGrid.PreviewKeyDown -= MainWindow_Global_PreviewKeyDown; rootGrid.Loaded -= MainWindow_Loaded; } }
        private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
        private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args) { if (sender is SettingsWindow closedWindow) { closedWindow.Closed -= SettingsWindow_Instance_Closed; } _settingsWindowInstance = null; ViewModel?.LoadCurrentSettings(); ViewModel?.ForceFullDefinitionReload(); ResumeLocalPolling(); TrySetFocusToListOrRoot(); }

        private void TrySetFocusToListOrRoot() { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { FrameworkElement? target = null; if (ViewModel.IsHubViewActive) { target = contentFrame?.Content as FrameworkElement ?? contentFrame; } else if (!ViewModel.IsDetailViewActive) { target = HistoryListView; } else { target = SentenceFocusAnchor; } if (target != null) { bool fs = target.Focus(FocusState.Programmatic); System.Diagnostics.Debug.WriteLine($"[TrySetFocus] Attempted focus on {target.Name ?? target.GetType().Name}. Result: {fs}"); } else { System.Diagnostics.Debug.WriteLine($"[TrySetFocus] No suitable target found for current view state (IsHub={ViewModel.IsHubViewActive}, IsDetail={ViewModel.IsDetailViewActive})."); } }); }
        public void UpdateLocalBindings(AppSettings settings) { _currentSettings = settings ?? new AppSettings(); _localKeyToActionId.Clear(); _localButtonToActionId.Clear(); if (_currentSettings.LocalKeyboardBindings != null) { foreach (var kvp in _currentSettings.LocalKeyboardBindings) { if (!string.IsNullOrEmpty(kvp.Value) && Enum.TryParse<VirtualKey>(kvp.Value, true, out var vk)) { _localKeyToActionId[vk] = kvp.Key; } } } if (_currentSettings.LocalJoystickBindings != null) { foreach (var kvp in _currentSettings.LocalJoystickBindings) { string actionId = kvp.Key; string? buttonCode = kvp.Value; if (!string.IsNullOrEmpty(buttonCode)) { GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode); if (buttonEnum.HasValue) { _localButtonToActionId[buttonEnum.Value] = actionId; } } } } reset_navigation_state(); }
        public void ToggleVisibility() => DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
        public async void TriggerMenuToggle() => await DispatcherQueue_EnqueueAsync(async () => await ViewModel.GoBackToHubCommand.ExecuteAsync(null));
        public void PauseLocalPolling() { _gamepadPollingTimer?.Stop(); }
        public void ResumeLocalPolling() { DispatcherQueue?.TryEnqueue(() => { bool isActive = false; try { HWND fWnd = PInvoke.GetForegroundWindow(); HWND myWnd = (HWND)WindowNative.GetWindowHandle(this); isActive = fWnd == myWnd; } catch { isActive = false; } if (isActive && _localGamepad != null && _gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning && !ViewModel.IsHubViewActive) { _gamepadPollingTimer.Start(); } }); }
        public void ShowSettingsWindow() { if (_settingsWindowInstance != null) { _settingsWindowInstance.Activate(); return; } _settingsWindowInstance = new SettingsWindow(); _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed; PauseLocalPolling(); _settingsWindowInstance.Activate(); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        private Task DispatcherQueue_EnqueueAsync(Action function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null in MainWindow")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { function(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue action in MainWindow.")); } return tcs.Task; }
        private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null in MainWindow")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue async action in MainWindow.")); } return tcs.Task; }
        private Task<T> DispatcherQueue_EnqueueAsync<T>(Func<T> function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource<T>(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null in MainWindow")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { T result = function(); tcs.SetResult(result); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue function in MainWindow.")); } return tcs.Task; }

    }
}