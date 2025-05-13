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
        private bool _isInitialGamepadHookAttempted = false;
        private int _lastFocusedItemIndexInHubRepeater = 0;
        private string? _lastViewedSessionIdForHubFocus = null;
        private Frame? contentFrame;
        private Grid? rootGrid;
        private Gamepad? _localGamepad = null;
        private DateTime _lastGamepadNavTime = DateTime.MinValue;
        private readonly TimeSpan _gamepadDebounce = TimeSpan.FromMilliseconds(150);
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
            if (this.Content is Grid gridElement)
            {
                rootGrid = gridElement;
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
                rootGrid.KeyDown += MainWindow_Global_KeyDown;
                rootGrid.Loaded += MainWindow_Loaded;
            }
            ViewModel.RequestLoadDefinitionHtml += ViewModel_RequestLoadDefinitionHtml;
            ViewModel.RequestRenderSentenceWebView += ViewModel_RequestRenderSentenceWebView;
            ViewModel.RequestUpdateDefinitionTokenDetails += ViewModel_RequestUpdateDefinitionTokenDetails;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            this.Title = "Jpn Study Tool";
            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated;
            Gamepad.GamepadAdded += Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
            SetupGamepadPollingTimer();
            InitializeHighlightThrottleTimer();
            System.Diagnostics.Debug.WriteLine("[MainWindow Constructor] Initialization complete.");
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
            contentFrame.Navigate(typeof(MainHubPage), _lastViewedSessionIdForHubFocus, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
            System.Diagnostics.Debug.WriteLine("[MainWindow NavigateToHub] Navigation call made to MainHubPage.");
        }
        public async Task NavigateToSessionViewAsync(string sessionIdToDisplay)
        {
            if (contentFrame == null) return;
            _lastViewedSessionIdForHubFocus = sessionIdToDisplay;
            await ViewModel.LoadCurrentSessionDataAsync(sessionIdToDisplay);
            ViewModel.IsHubViewActive = false;
            System.Diagnostics.Debug.WriteLine("[MainWindow] Navigated TO Session View (List)");
            TrySetFocusToListOrRoot();
        }
        private async Task ViewModel_RequestLoadDefinitionHtml(string? fullHtmlContent) { await LoadDefinitionHtmlAsync(fullHtmlContent); }
        private async Task ViewModel_RequestRenderSentenceWebView(List<DisplayToken> displayTokens) { await LoadSentenceDataAsync(displayTokens); }
        private async Task ViewModel_RequestUpdateDefinitionTokenDetails(string tokenDetailHtml) { await UpdateDefinitionTokenDetailsAsync(tokenDetailHtml); }
        private void SentenceWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args) { if (args.Exception != null) { System.Diagnostics.Debug.WriteLine($"[MW SentenceWebView Init Error] {args.Exception.Message}"); _isSentenceWebViewReady = false; return; } _isSentenceWebViewReady = true; if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; } else { _isSentenceWebViewReady = false; return; } System.Diagnostics.Debug.WriteLine("[MW SentenceWebView] Initialized."); }
        private void DefinitionWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args) { if (args.Exception != null) { System.Diagnostics.Debug.WriteLine($"[MW DefinitionWebView Init Error] {args.Exception.Message}"); _isDefinitionWebViewReady = false; return; } _isDefinitionWebViewReady = true; if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; } else { _isDefinitionWebViewReady = false; return; } System.Diagnostics.Debug.WriteLine("[MW DefinitionWebView] Initialized."); }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedTokenIndex))
            {
                int newIndex = ViewModel.SelectedTokenIndex;
                _pendingHighlightIndex = newIndex;
                var now = DateTime.UtcNow;
                var timeSinceLastHighlight = now - _lastHighlightTime;
                if (timeSinceLastHighlight >= _highlightThrottleInterval)
                {
                    _highlightThrottleTimer?.Stop();
                    _isHighlightUpdatePending = false;
                    _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () => await HighlightTokenInWebViewAsync(newIndex));
                    _lastHighlightTime = DateTime.UtcNow;
                }
                else if (!_isHighlightUpdatePending)
                {
                    var delayNeeded = _highlightThrottleInterval - timeSinceLastHighlight;
                    _isHighlightUpdatePending = true;
                    if (_highlightThrottleTimer != null)
                    {
                        _highlightThrottleTimer.Interval = delayNeeded > TimeSpan.Zero ? delayNeeded : TimeSpan.FromMilliseconds(1);
                        _highlightThrottleTimer.Start();
                    }
                }
            }
            else if (e.PropertyName == nameof(ViewModel.DetailFocusTarget))
            {
                _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () => await UpdateDetailFocusVisuals());
            }
            else if (e.PropertyName == nameof(ViewModel.IsHubViewActive))
            {
                System.Diagnostics.Debug.WriteLine($"[MW ViewModel_PropertyChanged] IsHubViewActive changed to: {ViewModel.IsHubViewActive}.");
                ResumeLocalPolling();
            }
        }

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

        private void CheckGamepadLocalInput(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (_localGamepad == null) { if (_gamepadPollingTimer != null && _gamepadPollingTimer.IsRunning) PauseLocalPolling(); return; }
            var now = DateTime.UtcNow;
            if (now - _lastGamepadNavTime < _gamepadDebounce) return;
            try
            {
                GamepadReading reading = _localGamepad.GetCurrentReading();
                bool processedThisTick = false;
                foreach (var kvp in _localButtonToActionId)
                {
                    GamepadButtons buttonToTest = kvp.Key; string actionId = kvp.Value;
                    if (reading.Buttons.HasFlag(buttonToTest))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MW CheckGamepadLocalInput] Button {buttonToTest} (Action: {actionId}) PRESSED. Calling ExecuteLocalAction.");
                        ExecuteLocalAction(actionId);
                        processedThisTick = true; _lastGamepadNavTime = now; break;
                    }
                }
                if (!processedThisTick)
                {
                    const double stickDeadzone = 0.7; FocusNavigationDirection navDirection = FocusNavigationDirection.None; string? debugNavAction = null;
                    if (reading.Buttons.HasFlag(GamepadButtons.DPadUp)) { navDirection = FocusNavigationDirection.Up; debugNavAction = "DPadUp"; }
                    else if (reading.Buttons.HasFlag(GamepadButtons.DPadDown)) { navDirection = FocusNavigationDirection.Down; debugNavAction = "DPadDown"; }
                    else if (reading.Buttons.HasFlag(GamepadButtons.DPadLeft)) { navDirection = FocusNavigationDirection.Left; debugNavAction = "DPadLeft"; }
                    else if (reading.Buttons.HasFlag(GamepadButtons.DPadRight)) { navDirection = FocusNavigationDirection.Right; debugNavAction = "DPadRight"; }
                    else if (reading.LeftThumbstickY > stickDeadzone) { navDirection = FocusNavigationDirection.Up; debugNavAction = "StickUp"; }
                    else if (reading.LeftThumbstickY < -stickDeadzone) { navDirection = FocusNavigationDirection.Down; debugNavAction = "StickDown"; }
                    else if (reading.LeftThumbstickX < -stickDeadzone) { navDirection = FocusNavigationDirection.Left; debugNavAction = "StickLeft"; }
                    else if (reading.LeftThumbstickX > stickDeadzone) { navDirection = FocusNavigationDirection.Right; debugNavAction = "StickRight"; }
                    if (navDirection != FocusNavigationDirection.None)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MW CheckGamepadLocalInput HUB NAV] NavInput: {debugNavAction} -> Direction: {navDirection}. IsHubActive: {ViewModel.IsHubViewActive}");
                        string navActionId = navDirection switch
                        {
                            FocusNavigationDirection.Up => "LOCAL_NAV_UP",
                            FocusNavigationDirection.Down => "LOCAL_NAV_DOWN",
                            FocusNavigationDirection.Left => "LOCAL_NAV_LEFT",
                            FocusNavigationDirection.Right => "LOCAL_NAV_RIGHT",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(navActionId)) ExecuteLocalAction(navActionId);
                        processedThisTick = true; _lastGamepadNavTime = now;
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MW CheckGamepadLocalInput Error] {ex.GetType().Name}: {ex.Message}. Attempting to re-hook."); _localGamepad = null; PauseLocalPolling(); TryHookCorrectGamepad(); }
        }
        private MainHubPage? GetHubPage()
        {
            return ContentFrameElement?.Content as MainHubPage;
        }
        private async void HandleHubNavigation(FocusNavigationDirection direction)
        {
            if (!ViewModel.IsHubViewActive || !(GetHubPage() is MainHubPage hubPage))
            {
                System.Diagnostics.Debug.WriteLine("[HandleHubNavigation] Not in Hub or HubPage not found.");
                return;
            }
            UIElement? currentFocusedUiElement = FocusManager.GetFocusedElement(hubPage.XamlRoot) as UIElement;
            FrameworkElement? currentFocusedElement = currentFocusedUiElement as FrameworkElement;
            string currentFocusName = currentFocusedElement?.Name ?? currentFocusedUiElement?.GetType().Name ?? "null_or_unnamed";
            System.Diagnostics.Debug.WriteLine($"[HandleHubNavigation] Direction: {direction}, Current Focus: {currentFocusName}");
            Button? settingsBtn = hubPage.FindName("SettingsButtonHub") as Button;
            ItemsRepeater? sessionsRepeater = hubPage.FindName("RecentSessionsRepeater") as ItemsRepeater;
            ScrollViewer? sessionsScroller = hubPage.FindName("RecentSessionsScrollViewer") as ScrollViewer;
            Button? newSessionBtn = hubPage.FindName("NewSessionButtonHub") as Button;
            Button? freeSessionBtn = hubPage.FindName("FreeSessionButtonHub") as Button;
            Button? savedWordsBtn = hubPage.FindName("SavedWordsButtonHub") as Button;
            Border? activityGraph = hubPage.FindName("ActivityGraphBorder") as Border;
            Button? endSessionBtn = hubPage.FindName("EndActiveSessionButtonHub") as Button;
            FrameworkElement? elementToFocus = null;
            bool isFocusInsideRepeater = false;
            int currentItemIndexInRepeater = -1;
            if (currentFocusedElement != null && sessionsRepeater != null)
            {
                DependencyObject? parent = VisualTreeHelper.GetParent(currentFocusedElement);
                while (parent != null)
                {
                    if (parent == sessionsRepeater)
                    {
                        isFocusInsideRepeater = true;
                        for (int i = 0; i < (sessionsRepeater.ItemsSourceView?.Count ?? 0); i++)
                        {
                            if (sessionsRepeater.TryGetElement(i) == currentFocusedElement)
                            {
                                currentItemIndexInRepeater = i;
                                _lastFocusedItemIndexInHubRepeater = i;
                                break;
                            }
                        }
                        break;
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
            if (isFocusInsideRepeater && currentItemIndexInRepeater == -1 && _lastFocusedItemIndexInHubRepeater < (sessionsRepeater?.ItemsSourceView?.Count ?? 0) && _lastFocusedItemIndexInHubRepeater >= 0)
            {
                currentItemIndexInRepeater = _lastFocusedItemIndexInHubRepeater;
            }
            int itemCountInRepeater = sessionsRepeater?.ItemsSourceView?.Count ?? 0;
            if (currentFocusedElement == settingsBtn)
            {
                if (direction == FocusNavigationDirection.Down)
                {
                    if (itemCountInRepeater > 0 && sessionsRepeater != null)
                    {
                        int targetIndex = (_lastFocusedItemIndexInHubRepeater >= 0 && _lastFocusedItemIndexInHubRepeater < itemCountInRepeater) ? _lastFocusedItemIndexInHubRepeater : 0;
                        elementToFocus = sessionsRepeater.TryGetElement(targetIndex) as FrameworkElement;
                        if (elementToFocus != null) _lastFocusedItemIndexInHubRepeater = targetIndex;
                        else elementToFocus = sessionsScroller;
                    }
                    else elementToFocus = newSessionBtn;
                }
            }
            else if (isFocusInsideRepeater && sessionsRepeater != null)
            {
                if (direction == FocusNavigationDirection.Up) elementToFocus = settingsBtn;
                else if (direction == FocusNavigationDirection.Down) elementToFocus = newSessionBtn;
                else if (direction == FocusNavigationDirection.Left)
                {
                    if (currentItemIndexInRepeater > 0)
                        elementToFocus = sessionsRepeater.TryGetElement(currentItemIndexInRepeater - 1) as FrameworkElement;
                }
                else if (direction == FocusNavigationDirection.Right)
                {
                    if (currentItemIndexInRepeater < itemCountInRepeater - 1)
                        elementToFocus = sessionsRepeater.TryGetElement(currentItemIndexInRepeater + 1) as FrameworkElement;
                }
            }
            else if (currentFocusedElement == newSessionBtn || currentFocusedElement == freeSessionBtn || currentFocusedElement == savedWordsBtn)
            {
                if (direction == FocusNavigationDirection.Up)
                {
                    if (itemCountInRepeater > 0 && sessionsRepeater != null)
                    {
                        int targetIndex = (_lastFocusedItemIndexInHubRepeater >= 0 && _lastFocusedItemIndexInHubRepeater < itemCountInRepeater) ? _lastFocusedItemIndexInHubRepeater : 0;
                        elementToFocus = sessionsRepeater.TryGetElement(targetIndex) as FrameworkElement;
                        if (elementToFocus != null) _lastFocusedItemIndexInHubRepeater = targetIndex;
                        else elementToFocus = sessionsScroller;
                    }
                    else elementToFocus = settingsBtn;
                }
                else if (direction == FocusNavigationDirection.Down) elementToFocus = activityGraph;
                else if (direction == FocusNavigationDirection.Left)
                {
                    if (currentFocusedElement == freeSessionBtn) elementToFocus = newSessionBtn;
                    else if (currentFocusedElement == savedWordsBtn) elementToFocus = freeSessionBtn;
                }
                else if (direction == FocusNavigationDirection.Right)
                {
                    if (currentFocusedElement == newSessionBtn) elementToFocus = freeSessionBtn;
                    else if (currentFocusedElement == freeSessionBtn) elementToFocus = savedWordsBtn;
                }
            }
            else if (currentFocusedElement == activityGraph)
            {
                if (direction == FocusNavigationDirection.Up) elementToFocus = freeSessionBtn;
                else if (direction == FocusNavigationDirection.Down && endSessionBtn != null && endSessionBtn.Visibility == Visibility.Visible)
                    elementToFocus = endSessionBtn;
            }
            else if (currentFocusedElement == endSessionBtn)
            {
                if (direction == FocusNavigationDirection.Up) elementToFocus = activityGraph;
            }
            else
            {
                elementToFocus = settingsBtn;
            }
            if (elementToFocus is Control controlToFocus && controlToFocus.IsEnabled && controlToFocus.Visibility == Visibility.Visible)
            {
                System.Diagnostics.Debug.WriteLine($"[HandleHubNavigation] Focusing programmatically: {controlToFocus.Name ?? controlToFocus.GetType().Name}");
                controlToFocus.Focus(FocusState.Programmatic);
                if (sessionsRepeater != null)
                {
                    bool newFocusIsInsideRepeater = false;
                    var parent = VisualTreeHelper.GetParent(controlToFocus);
                    while (parent != null) { if (parent == sessionsRepeater) { newFocusIsInsideRepeater = true; break; } parent = VisualTreeHelper.GetParent(parent); }
                    if (newFocusIsInsideRepeater)
                    {
                        for (int i = 0; i < itemCountInRepeater; i++)
                        {
                            if (sessionsRepeater.TryGetElement(i) == controlToFocus)
                            {
                                _lastFocusedItemIndexInHubRepeater = i;
                                System.Diagnostics.Debug.WriteLine($"[HandleHubNavigation] Updated _lastFocusedItemIndexInHubRepeater to {i}");
                                break;
                            }
                        }
                        controlToFocus.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, HorizontalAlignmentRatio = 0.5, VerticalAlignmentRatio = 0.5 });
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[HandleHubNavigation] No valid target element found or target not a Control for direction {direction}. Current focus was {currentFocusName}");
            }
        }

        private async void MainWindow_Global_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool isInHubView = ViewModel.IsHubViewActive;
            bool isInSessionListView = !ViewModel.IsHubViewActive && !ViewModel.IsDetailViewActive;
            bool isInDetailView = ViewModel.IsDetailViewActive;
            bool isInSessionViewOverall = !isInHubView;
            if (isInDetailView && !ViewModel.CanInteractWithDetailView && e.Key != VirtualKey.Escape)
            {
                e.Handled = true;
                return;
            }
            var keyStateCtrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool ctrlPressed = keyStateCtrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var keyStateShift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            bool shiftPressed = keyStateShift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            string? actionIdFromCustomBinding = null;

            List<string> bindingParts = new List<string>();
            if (ctrlPressed) bindingParts.Add("Ctrl");
            if (shiftPressed) bindingParts.Add("Shift");
            bindingParts.Add(e.Key.ToString());
            string currentKeyCombinationString = string.Join("+", bindingParts);
            if (_currentSettings.LocalKeyboardBindings != null)
            {
                var customBindingMatch = _currentSettings.LocalKeyboardBindings
                    .FirstOrDefault(kvp => kvp.Value?.Equals(currentKeyCombinationString, StringComparison.OrdinalIgnoreCase) == true);
                if (!string.IsNullOrEmpty(customBindingMatch.Key))
                {
                    actionIdFromCustomBinding = customBindingMatch.Key;
                }
            }
            if (actionIdFromCustomBinding == null && !ctrlPressed && !shiftPressed)
            {
                if (_localKeyToActionId.TryGetValue(e.Key, out string? simpleKeyActionId))
                {
                    actionIdFromCustomBinding = simpleKeyActionId;
                }
            }
            bool isArrowKey = e.Key == VirtualKey.Up || e.Key == VirtualKey.Down || e.Key == VirtualKey.Left || e.Key == VirtualKey.Right;

            if (actionIdFromCustomBinding != null)
            {
                bool isNavAction = actionIdFromCustomBinding.StartsWith("LOCAL_NAV_");
                if (isInHubView && isNavAction)
                {
                    FocusNavigationDirection direction = actionIdFromCustomBinding switch
                    {
                        "LOCAL_NAV_UP" => FocusNavigationDirection.Up,
                        "LOCAL_NAV_DOWN" => FocusNavigationDirection.Down,
                        "LOCAL_NAV_LEFT" => FocusNavigationDirection.Left,
                        "LOCAL_NAV_RIGHT" => FocusNavigationDirection.Right,
                        _ => FocusNavigationDirection.None
                    };
                    if (direction != FocusNavigationDirection.None)
                    {
                        HandleHubNavigation(direction);
                    }
                }
                else
                {
                    ExecuteLocalAction(actionIdFromCustomBinding);
                }
                e.Handled = true;
            }
            else if (!isArrowKey && !e.Handled)
            {
                string? universalActionId = e.Key switch
                {
                    VirtualKey.Enter => "LOCAL_ACCEPT",
                    VirtualKey.Escape => "LOCAL_CANCEL",
                    VirtualKey.P => "LOCAL_CONTEXT_MENU",
                    VirtualKey.PageUp => "LOCAL_NAV_FAST_PAGE_UP",
                    VirtualKey.Home => "LOCAL_NAV_FAST_PAGE_UP",
                    VirtualKey.PageDown => "LOCAL_NAV_FAST_PAGE_DOWN",
                    VirtualKey.End => "LOCAL_NAV_FAST_PAGE_DOWN",
                    _ => null
                };
                if (universalActionId != null)
                {
                    ExecuteLocalAction(universalActionId);
                    e.Handled = true;
                }
            }
            else if (isArrowKey && !isInHubView && !e.Handled)
            {
                bool arrowActionHandledInternally = false;
                if (e.Key == VirtualKey.Up) { if (isInSessionListView) { ViewModel.MoveSelection(-1); arrowActionHandledInternally = true; } else if (isInDetailView && ViewModel.DetailFocusTarget == "Sentence") { ViewModel.MoveKanjiTokenSelection(-1); arrowActionHandledInternally = true; } else if (isInDetailView && ViewModel.DetailFocusTarget == "Definition" && !ViewModel.IsAIModeActive && _definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex - 1); arrowActionHandledInternally = true; } }
                else if (e.Key == VirtualKey.Down) { if (isInSessionListView) { ViewModel.MoveSelection(1); arrowActionHandledInternally = true; } else if (isInDetailView && ViewModel.DetailFocusTarget == "Sentence") { ViewModel.MoveKanjiTokenSelection(1); arrowActionHandledInternally = true; } else if (isInDetailView && ViewModel.DetailFocusTarget == "Definition" && !ViewModel.IsAIModeActive && _definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex + 1); arrowActionHandledInternally = true; } }
                else if (e.Key == VirtualKey.Left) { if (isInDetailView && ViewModel.DetailFocusTarget == "Sentence") { ViewModel.MoveTokenSelection(-1); arrowActionHandledInternally = true; } }
                else if (e.Key == VirtualKey.Right) { if (isInDetailView && ViewModel.DetailFocusTarget == "Sentence") { ViewModel.MoveTokenSelection(1); arrowActionHandledInternally = true; } }
                if (arrowActionHandledInternally) { e.Handled = true; if (isInSessionListView && ViewModel.SelectedSentenceItem != null) { await DispatcherQueue_EnqueueAsync(() => { if (HistoryListView != null && ViewModel.SelectedSentenceItem != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem); }); } }
            }
            if (!e.Handled && ctrlPressed && shiftPressed) { /* DevTools */ }
        }
        private async void ExecuteLocalAction(string actionId)
        {
            if (ViewModel.IsDetailViewActive && !ViewModel.CanInteractWithDetailView && actionId != "LOCAL_CANCEL") return;
            bool isInHubView = ViewModel.IsHubViewActive;
            bool isInSessionListView = !ViewModel.IsHubViewActive && !ViewModel.IsDetailViewActive;
            bool isInDetailView = ViewModel.IsDetailViewActive;
            string detailFocus = ViewModel.DetailFocusTarget;
            System.Diagnostics.Debug.WriteLine($"[ExecuteLocalAction] Action: {actionId}, Hub: {isInHubView}, List: {isInSessionListView}, Detail: {isInDetailView}, DetailFocus: {detailFocus}");
            switch (actionId)
            {
                case "LOCAL_NAV_UP":
                    if (isInHubView) HandleHubNavigation(FocusNavigationDirection.Up);
                    else if (isInSessionListView) { ViewModel.MoveSelection(-1); if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) await DispatcherQueue_EnqueueAsync(() => HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem, ScrollIntoViewAlignment.Default)); }
                    else if (isInDetailView && detailFocus == "Sentence") ViewModel.MoveKanjiTokenSelection(-1);
                    else if (isInDetailView && detailFocus == "Definition" && !ViewModel.IsAIModeActive && _definitionLiCount > 0) await HighlightDefinitionItemAsync(_currentDefinitionLiIndex - 1);
                    break;
                case "LOCAL_NAV_DOWN":
                    if (isInHubView) HandleHubNavigation(FocusNavigationDirection.Down);
                    else if (isInSessionListView) { ViewModel.MoveSelection(1); if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) await DispatcherQueue_EnqueueAsync(() => HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem, ScrollIntoViewAlignment.Default)); }
                    else if (isInDetailView && detailFocus == "Sentence") ViewModel.MoveKanjiTokenSelection(1);
                    else if (isInDetailView && detailFocus == "Definition" && !ViewModel.IsAIModeActive && _definitionLiCount > 0) await HighlightDefinitionItemAsync(_currentDefinitionLiIndex + 1);
                    break;
                case "LOCAL_NAV_LEFT":
                    if (isInHubView) HandleHubNavigation(FocusNavigationDirection.Left);
                    else if (isInDetailView && detailFocus == "Sentence") ViewModel.MoveTokenSelection(-1);
                    break;
                case "LOCAL_NAV_RIGHT":
                    if (isInHubView) HandleHubNavigation(FocusNavigationDirection.Right);
                    else if (isInDetailView && detailFocus == "Sentence") ViewModel.MoveTokenSelection(1);
                    break;
                case "LOCAL_ACCEPT":
                    if (isInHubView) { var focusedElement = FocusManager.GetFocusedElement(this.Content?.XamlRoot) as FrameworkElement; if (App.MainWin?.ContentFrameElement?.Content is MainHubPage hubPage && hubPage.ViewModel != null && focusedElement != null) { if (focusedElement is Button focusedButton && focusedButton.Command != null) { focusedButton.Command.Execute(focusedButton.CommandParameter); } else if (focusedElement.DataContext is SessionDisplayItem sdi) { hubPage.ViewModel.ContinueSessionCommand.Execute(sdi.SessionId); } } }
                    else if (isInSessionListView) { if (ViewModel.SelectedSentenceIndex >= 0) ViewModel.EnterDetailView(); }
                    else if (isInDetailView) { if (detailFocus == "Sentence") ViewModel.FocusDefinitionArea(); else if (detailFocus == "Definition" && !ViewModel.IsAIModeActive && _currentDefinitionLiIndex >= 0) System.Diagnostics.Debug.WriteLine($"TODO: JMDict link for index {_currentDefinitionLiIndex}"); }
                    break;
                case "LOCAL_CANCEL":
                    if (isInDetailView) { if (detailFocus == "Definition") ViewModel.FocusSentenceArea(); else ViewModel.ExitDetailView(); }
                    else if (isInSessionListView) await ViewModel.GoBackToHubCommand.ExecuteAsync(null);
                    else if (isInHubView) { /* No default cancel action for Hub unless a dialog is open */ }
                    break;
                case "LOCAL_CONTEXT_MENU":
                    var focusedForMenu = FocusManager.GetFocusedElement(this.Content?.XamlRoot) as FrameworkElement; if (focusedForMenu != null) System.Diagnostics.Debug.WriteLine($"TODO: ContextMenu for {focusedForMenu.Name}");
                    break;
                case "LOCAL_SAVE_WORD":
                    if (isInDetailView && ViewModel.SelectedToken != null) System.Diagnostics.Debug.WriteLine($"TODO: Save Word {ViewModel.SelectedToken.Surface}");
                    break;
                case "LOCAL_NAV_FAST_PAGE_UP":
                    if (isInSessionListView && ViewModel.CurrentSessionEntries.Count > 0) { ViewModel.SelectedSentenceIndex = 0; if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem, ScrollIntoViewAlignment.Leading); }
                    break;
                case "LOCAL_NAV_FAST_PAGE_DOWN":
                    if (isInSessionListView && ViewModel.CurrentSessionEntries.Count > 0) { ViewModel.SelectedSentenceIndex = ViewModel.CurrentSessionEntries.Count - 1; if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem, ScrollIntoViewAlignment.Leading); }
                    break;
                case "LOCAL_SETTINGS_HUB": if (isInHubView) App.MainWin?.ShowSettingsWindow(); break;
                case "LOCAL_ANKI_LINK": if (isInDetailView && ViewModel.SelectedToken != null) System.Diagnostics.Debug.WriteLine($"TODO: Anki Link {ViewModel.SelectedToken.Surface}"); break;
                default: System.Diagnostics.Debug.WriteLine($"[ExecuteLocalAction] Unknown or unhandled ActionId: {actionId}"); break;
            }
        }






        private FindNextElementOptions GetFindNextElementOptions()
        {
            var options = new FindNextElementOptions();
            UIElement? searchRootCandidate = null;
            if (ViewModel.IsHubViewActive)
            {
                if (ContentFrameElement?.Content is UIElement hubPageContent && hubPageContent is FrameworkElement feHubPage && feHubPage.IsLoaded) searchRootCandidate = hubPageContent;
                else if (ContentFrameElement is FrameworkElement feFrame && feFrame.IsLoaded) searchRootCandidate = ContentFrameElement;
            }
            else if (!ViewModel.IsHubViewActive && SessionViewContainer is FrameworkElement feSession && feSession.IsLoaded) searchRootCandidate = SessionViewContainer;
            if (searchRootCandidate == null || (searchRootCandidate is FrameworkElement fe && !fe.IsLoaded))
            {
                if (this.Content is FrameworkElement rootGridElement && rootGridElement.IsLoaded && rootGridElement.ActualHeight > 0 && rootGridElement.ActualWidth > 0) searchRootCandidate = rootGridElement;
            }
            options.SearchRoot = searchRootCandidate;
            if (options.SearchRoot != null) System.Diagnostics.Debug.WriteLine($"[GetFindNextElementOptions] Using SearchRoot: {options.SearchRoot.GetType().Name}, Name: {(options.SearchRoot as FrameworkElement)?.Name ?? "N/A"}, IsLoaded: {(options.SearchRoot as FrameworkElement)?.IsLoaded}");
            else
            {
                System.Diagnostics.Debug.WriteLine($"[GetFindNextElementOptions] CRITICAL: options.SearchRoot is NULL.");
                if (rootGrid != null && rootGrid.IsLoaded) options.SearchRoot = rootGrid; else if (this.Content is UIElement contentElement) options.SearchRoot = contentElement;
                if (options.SearchRoot == null) System.Diagnostics.Debug.WriteLine($"[GetFindNextElementOptions] FATAL: SearchRoot still NULL after all fallbacks.");
            }
            return options;
        }
        private void reset_navigation_state() => _lastGamepadNavTime = DateTime.MinValue;
        private GamepadButtons? MapInternalCodeToGamepadButton(string code) => code.ToUpperInvariant() switch { "BTN_A" => GamepadButtons.A, "BTN_B" => GamepadButtons.B, "BTN_X" => GamepadButtons.X, "BTN_Y" => GamepadButtons.Y, "BTN_TL" => GamepadButtons.LeftShoulder, "BTN_TR" => GamepadButtons.RightShoulder, "BTN_SELECT" => GamepadButtons.View, "BTN_START" => GamepadButtons.Menu, "BTN_THUMBL" => GamepadButtons.LeftThumbstick, "BTN_THUMBR" => GamepadButtons.RightThumbstick, "DPAD_UP" => GamepadButtons.DPadUp, "DPAD_DOWN" => GamepadButtons.DPadDown, "DPAD_LEFT" => GamepadButtons.DPadLeft, "DPAD_RIGHT" => GamepadButtons.DPadRight, _ => null };
        private string? FindActionForAxis(string axis) => _currentSettings.LocalJoystickBindings switch { null => null, var b => axis switch { "STICK_LEFT_Y_UP" => b.FirstOrDefault(kvp => kvp.Value == "NAV_UP").Key, "STICK_LEFT_Y_DOWN" => b.FirstOrDefault(kvp => kvp.Value == "NAV_DOWN").Key, "STICK_LEFT_X_LEFT" => b.FirstOrDefault(kvp => kvp.Value == "NAV_LEFT").Key, "STICK_LEFT_X_RIGHT" => b.FirstOrDefault(kvp => kvp.Value == "NAV_RIGHT").Key, _ => null } };
        private void SetupGamepadPollingTimer()
        {
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dq == null)
            {
                System.Diagnostics.Debug.WriteLine("[MW SetupGamepadPollingTimer] CRITICAL: DispatcherQueue is null. Cannot create timer.");
                return;
            }
            _gamepadPollingTimer = dq.CreateTimer();
            _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50);
            _gamepadPollingTimer.Tick += CheckGamepadLocalInput;
            System.Diagnostics.Debug.WriteLine("[MW SetupGamepadPollingTimer] Gamepad polling timer CREATED.");
        }


        private void TryHookCorrectGamepad()
        {
            _localGamepad = null;
            var gamepads = Gamepad.Gamepads;
            System.Diagnostics.Debug.WriteLine($"[MW TryHookLocal] Found {gamepads.Count} gamepads via Windows.Gaming.Input.");
            _localGamepad = gamepads.FirstOrDefault();
            if (_localGamepad != null)
            {
                var rgCtrl = RawGameController.FromGameController(_localGamepad);
                System.Diagnostics.Debug.WriteLine($"[MW TryHookLocal] Hooked Local Gamepad: {rgCtrl?.DisplayName ?? "Unknown"} (VID: {rgCtrl?.HardwareVendorId:X4}, PID: {rgCtrl?.HardwareProductId:X4})");
                try { _ = _localGamepad.GetCurrentReading(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MW TryHookLocal] Failed initial read: {ex.Message}. Unhooking."); _localGamepad = null; }
            }
            else { System.Diagnostics.Debug.WriteLine($"[MW TryHookLocal] No suitable local gamepad found to hook."); }
        }


        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                System.Diagnostics.Debug.WriteLine("[MW MainWindow_Activated] Window DEACTIVATED. Pausing local polling.");
                PauseLocalPolling();
                reset_navigation_state();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MW MainWindow_Activated] Window ACTIVATED.");
                if (!_isInitialGamepadHookAttempted || _localGamepad == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MW MainWindow_Activated] Attempting to hook gamepad...");
                    TryHookCorrectGamepad();
                    _isInitialGamepadHookAttempted = true;
                }
                System.Diagnostics.Debug.WriteLine("[MW MainWindow_Activated] Attempting to resume local polling.");
                ResumeLocalPolling();
            }
        }


        private void Gamepad_GamepadAdded(object? sender, Gamepad e) { System.Diagnostics.Debug.WriteLine("[MW Gamepad_GamepadAdded] Attempting hook & resume."); TryHookCorrectGamepad(); ResumeLocalPolling(); }
        private void Gamepad_GamepadRemoved(object? sender, Gamepad e) { System.Diagnostics.Debug.WriteLine("[MW Gamepad_GamepadRemoved] Attempting re-hook & pause."); TryHookCorrectGamepad(); PauseLocalPolling(); }

        private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListView { SelectedItem: not null } lv) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (lv.SelectedItem != null) lv.ScrollIntoView(lv.SelectedItem); }); } }
        private void MainWindow_Closed(object sender, WindowEventArgs args) { _highlightThrottleTimer?.Stop(); if (ViewModel != null) { ViewModel.PropertyChanged -= ViewModel_PropertyChanged; ViewModel.RequestLoadDefinitionHtml -= ViewModel_RequestLoadDefinitionHtml; ViewModel.RequestRenderSentenceWebView -= ViewModel_RequestRenderSentenceWebView; ViewModel.RequestUpdateDefinitionTokenDetails -= ViewModel_RequestUpdateDefinitionTokenDetails; ViewModel.Cleanup(); } PauseLocalPolling(); Gamepad.GamepadAdded -= Gamepad_GamepadAdded; Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved; SentenceWebView?.Close(); DefinitionWebView?.Close(); _localGamepad = null; _gamepadPollingTimer = null; if (rootGrid != null) { rootGrid.PreviewKeyDown -= MainWindow_Global_KeyDown; rootGrid.Loaded -= MainWindow_Loaded; } }
        private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
        private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args) { if (sender is SettingsWindow closedWindow) { closedWindow.Closed -= SettingsWindow_Instance_Closed; } _settingsWindowInstance = null; ViewModel?.LoadCurrentSettings(); ViewModel?.ForceFullDefinitionReload(); ResumeLocalPolling(); TrySetFocusToListOrRoot(); }

        private void TrySetFocusToListOrRoot(string? targetSessionId = null)
        {
            _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                FrameworkElement? elementToFocus = null;
                string focusTargetName = "Unknown";
                if (ViewModel.IsHubViewActive)
                {
                    if (ContentFrameElement?.Content is MainHubPage hubPage)
                    {
                        await Task.Delay(150);
                        ItemsRepeater? sessionsRepeater = hubPage.FindName("RecentSessionsRepeater") as ItemsRepeater;
                        if (!string.IsNullOrEmpty(targetSessionId) && sessionsRepeater != null && hubPage.ViewModel.RecentSessions.Any())
                        {
                            System.Diagnostics.Debug.WriteLine($"[TrySetFocus Hub] Attempting to find session ID: {targetSessionId}");
                            var sessionItem = hubPage.ViewModel.RecentSessions.FirstOrDefault(s => s.SessionId == targetSessionId);
                            if (sessionItem != null)
                            {
                                int index = hubPage.ViewModel.RecentSessions.IndexOf(sessionItem);
                                if (index >= 0)
                                {
                                    for (int attempt = 0; attempt < 5; attempt++)
                                    {
                                        elementToFocus = sessionsRepeater.TryGetElement(index) as FrameworkElement;
                                        if (elementToFocus != null) break;
                                        await Task.Delay(50);
                                    }
                                    if (elementToFocus != null)
                                    {
                                        focusTargetName = $"SessionCard (ID: {targetSessionId})";
                                        (elementToFocus as Control)?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, HorizontalAlignmentRatio = 0.5 });
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[TrySetFocus Hub] Could not realize UI element for session ID: {targetSessionId}");
                                    }
                                }
                            }
                        }
                        if (elementToFocus == null)
                        {
                            elementToFocus = hubPage.FindName("SettingsButtonHub") as Button;
                            if (elementToFocus != null)
                            {
                                focusTargetName = "SettingsButtonHub";
                            }
                            else
                            {
                                elementToFocus = hubPage;
                                focusTargetName = "HubPage";
                                System.Diagnostics.Debug.WriteLine($"[TrySetFocus Hub] Fallback: SettingsButtonHub not found, attempting to focus HubPage itself.");
                            }
                        }
                    }
                    else
                    {
                        elementToFocus = contentFrame;
                        focusTargetName = "ContentFrame";
                    }
                }
                else if (!ViewModel.IsDetailViewActive)
                {
                    elementToFocus = HistoryListView;
                    focusTargetName = "HistoryListView";
                }
                else
                {
                    elementToFocus = SentenceFocusAnchor;
                    focusTargetName = "SentenceFocusAnchor";
                }
                if (elementToFocus != null)
                {
                    bool focusSet = elementToFocus.Focus(FocusState.Programmatic);
                    System.Diagnostics.Debug.WriteLine($"[TrySetFocus] Attempted focus on {focusTargetName}. Result: {focusSet}. IsLoaded: {(elementToFocus as FrameworkElement)?.IsLoaded}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[TrySetFocus] No suitable target found. IsHub={ViewModel.IsHubViewActive}, IsDetail={ViewModel.IsDetailViewActive}");
                }
            });
        }

        public void UpdateLocalBindings(AppSettings settings)
        {
            _currentSettings = settings ?? new AppSettings();
            _localKeyToActionId.Clear();
            _localButtonToActionId.Clear();

            if (_currentSettings.LocalJoystickBindings != null)
            {
                System.Diagnostics.Debug.WriteLine("[MW UpdateLocalBindings] Processing LocalJoystickBindings...");
                foreach (var kvp in _currentSettings.LocalJoystickBindings)
                {
                    string actionId = kvp.Key;
                    string? buttonCode = kvp.Value;
                    System.Diagnostics.Debug.WriteLine($"  - ActionID: {actionId}, ButtonCode: {buttonCode ?? "NULL"}");
                    if (!string.IsNullOrEmpty(buttonCode))
                    {
                        GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode);
                        if (buttonEnum.HasValue)
                        {
                            _localButtonToActionId[buttonEnum.Value] = actionId;
                            System.Diagnostics.Debug.WriteLine($"    Mapped GamepadButton.{buttonEnum.Value} to ActionID '{actionId}'");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"    Could not map ButtonCode '{buttonCode}' to GamepadButtons enum.");
                        }
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MW UpdateLocalBindings] LocalJoystickBindings is null in settings.");
            }
            reset_navigation_state();
        }
        public void ToggleVisibility() => DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
        public async void TriggerMenuToggle() => await DispatcherQueue_EnqueueAsync(async () => await ViewModel.GoBackToHubCommand.ExecuteAsync(null));
        public void PauseLocalPolling()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_gamepadPollingTimer != null && _gamepadPollingTimer.IsRunning)
                {
                    _gamepadPollingTimer.Stop();
                    System.Diagnostics.Debug.WriteLine("[MW PauseLocalPolling] Gamepad polling timer STOPPED.");
                }
            });
        }

        public void ResumeLocalPolling()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                bool isActiveWindow = PInvoke.GetForegroundWindow() == (HWND)WindowNative.GetWindowHandle(this);
                if (isActiveWindow && _localGamepad != null && _gamepadPollingTimer != null)
                {
                    if (!_gamepadPollingTimer.IsRunning)
                    {
                        System.Diagnostics.Debug.WriteLine("[MW ResumeLocalPolling] Conditions MET. Attempting to START polling timer...");
                        _gamepadPollingTimer.Start();
                        System.Diagnostics.Debug.WriteLine($"[MW ResumeLocalPolling] Gamepad polling timer {(_gamepadPollingTimer.IsRunning ? "STARTED" : "FAILED TO START")}. IsHubActive: {ViewModel.IsHubViewActive}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MW ResumeLocalPolling] Polling NOT started/resumed. IsActiveWindow: {isActiveWindow}, HasLocalGamepad: {_localGamepad != null}, TimerOK: {_gamepadPollingTimer != null}, IsHubActive (for context): {ViewModel.IsHubViewActive}");
                    if (_gamepadPollingTimer != null && _gamepadPollingTimer.IsRunning && (!isActiveWindow || _localGamepad == null))
                    {
                        PauseLocalPolling();
                    }
                }
            });
        }

        public void ShowSettingsWindow() { if (_settingsWindowInstance != null) { _settingsWindowInstance.Activate(); return; } _settingsWindowInstance = new SettingsWindow(); _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed; PauseLocalPolling(); _settingsWindowInstance.Activate(); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        private Task DispatcherQueue_EnqueueAsync(Action function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null in MainWindow")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { function(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue action in MainWindow.")); } return tcs.Task; }
        private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null in MainWindow")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue async action in MainWindow.")); } return tcs.Task; }
        private Task<T> DispatcherQueue_EnqueueAsync<T>(Func<T> function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource<T>(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null in MainWindow")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { T result = function(); tcs.SetResult(result); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue function in MainWindow.")); } return tcs.Task; }
    }
}