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
using Microsoft.UI.Input;
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

    public MainWindow()
    {
        this.InitializeComponent();
        _windowActivationService = new WindowActivationService();
        IntPtr hwndPtr = WindowNative.GetWindowHandle(this);
        _windowActivationService.RegisterWindowHandle(hwndPtr);

        ViewModel = new MainWindowViewModel();
        ViewModel.RequestLoadDefinitionHtml += ViewModel_RequestLoadDefinitionHtml;
        ViewModel.RequestRenderSentenceWebView += ViewModel_RequestRenderSentenceWebView; // Corregido a List
        ViewModel.RequestUpdateDefinitionTokenDetails += ViewModel_RequestUpdateDefinitionTokenDetails;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (this.Content is FrameworkElement rootElement) { rootElement.DataContext = ViewModel; rootElement.PreviewKeyDown += MainWindow_Root_PreviewKeyDown; }
        this.Title = "Jpn Study Tool"; this.Closed += MainWindow_Closed; this.Activated += MainWindow_Activated;
        UpdateViewVisibilityUI(ViewModel.CurrentViewInternal);
        Gamepad.GamepadAdded += Gamepad_GamepadAdded; Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
        TryHookCorrectGamepad(); SetupGamepadPollingTimer(); InitializeHighlightThrottleTimer();
    }

    private async Task ViewModel_RequestLoadDefinitionHtml(string? fullHtmlContent) { await LoadDefinitionHtmlAsync(fullHtmlContent); }
    private async Task ViewModel_RequestRenderSentenceWebView(List<DisplayToken> displayTokens) { await LoadSentenceDataAsync(displayTokens); } // Corregido a List
    private async Task ViewModel_RequestUpdateDefinitionTokenDetails(string tokenDetailHtml) { await UpdateDefinitionTokenDetailsAsync(tokenDetailHtml); }

    private async void SentenceWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args) { if (args.Exception != null) { _isSentenceWebViewReady = false; return; } _isSentenceWebViewReady = true; if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; } else { _isSentenceWebViewReady = false; return; } }
    private void DefinitionWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args) { if (args.Exception != null) { _isDefinitionWebViewReady = false; return; } _isDefinitionWebViewReady = true; if (sender.CoreWebView2 != null) { sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; sender.CoreWebView2.Settings.AreDevToolsEnabled = true; sender.CoreWebView2.Settings.IsStatusBarEnabled = false; } else { _isDefinitionWebViewReady = false; return; } }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CurrentViewInternal)) { DispatcherQueue.TryEnqueue(() => UpdateViewVisibilityUI(ViewModel.CurrentViewInternal)); if (ViewModel.CurrentViewInternal == "list") { await ClearWebViewsAsync(); } }
        else if (e.PropertyName == nameof(ViewModel.SelectedTokenIndex))
        {
            int newIndex = ViewModel.SelectedTokenIndex; _pendingHighlightIndex = newIndex; var now = DateTime.UtcNow; var timeSinceLastHighlight = now - _lastHighlightTime;
            if (timeSinceLastHighlight >= _highlightThrottleInterval) { _highlightThrottleTimer?.Stop(); _isHighlightUpdatePending = false; await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(newIndex)); _lastHighlightTime = DateTime.UtcNow; }
            else if (!_isHighlightUpdatePending) { var delayNeeded = _highlightThrottleInterval - timeSinceLastHighlight; _isHighlightUpdatePending = true; if (_highlightThrottleTimer != null) { _highlightThrottleTimer.Interval = delayNeeded > TimeSpan.Zero ? delayNeeded : TimeSpan.FromMilliseconds(1); _highlightThrottleTimer.Start(); } }
        }
        else if (e.PropertyName == nameof(ViewModel.DetailFocusTarget)) { await DispatcherQueue_EnqueueAsync(UpdateDetailFocusVisuals); }
        else if (e.PropertyName == nameof(ViewModel.CanInteractWithDetailView)) { }
    }

    private void InitializeHighlightThrottleTimer() { var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); if (dq != null) { _highlightThrottleTimer = dq.CreateTimer(); _highlightThrottleTimer.IsRepeating = false; _highlightThrottleTimer.Tick += HighlightThrottleTimer_Tick; } }
    private async void HighlightThrottleTimer_Tick(object? sender, object e) { _highlightThrottleTimer?.Stop(); _isHighlightUpdatePending = false; await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(_pendingHighlightIndex)); _lastHighlightTime = DateTime.UtcNow; }

    private void UpdateViewVisibilityUI(string? currentView) { if (currentView == "list") { ListViewContentGrid.Visibility = Visibility.Visible; DetailViewContentGrid.Visibility = Visibility.Collapsed; SentenceWebView.Visibility = Visibility.Collapsed; DefinitionWebView.Visibility = Visibility.Collapsed; TrySetFocusToListOrRoot(); } else if (currentView == "detail") { ListViewContentGrid.Visibility = Visibility.Collapsed; DetailViewContentGrid.Visibility = Visibility.Visible; DispatcherQueue.TryEnqueue(() => SentenceFocusAnchor.Focus(FocusState.Programmatic)); } else { ListViewContentGrid.Visibility = Visibility.Visible; DetailViewContentGrid.Visibility = Visibility.Collapsed; SentenceWebView.Visibility = Visibility.Collapsed; DefinitionWebView.Visibility = Visibility.Collapsed; } }
    private async Task ClearWebViewsAsync() { if (_isSentenceWebViewReady && SentenceWebView.CoreWebView2 != null) { try { SentenceWebView.CoreWebView2.Navigate("about:blank"); } catch { } } if (_isDefinitionWebViewReady && DefinitionWebView.CoreWebView2 != null) { try { DefinitionWebView.CoreWebView2.Navigate("about:blank"); } catch { } } SentenceWebView.Visibility = Visibility.Collapsed; DefinitionWebView.Visibility = Visibility.Collapsed; await Task.CompletedTask; }

    private async Task LoadSentenceDataAsync(List<DisplayToken> displayTokens) // Corregido a List
    {
        SentenceWebView.Visibility = Visibility.Collapsed; if (!_isSentenceWebViewReady || SentenceWebView.CoreWebView2 == null) { return; }
        if (displayTokens == null || !displayTokens.Any()) { SentenceWebView.CoreWebView2.Navigate("about:blank"); return; }
        StringBuilder htmlBuilder = new StringBuilder();
        htmlBuilder.Append(@"<!DOCTYPE html><html><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"); htmlBuilder.Append(@"html, body { height: 100%; margin: 0; padding: 0; box-sizing: border-box; overflow: hidden; } "); htmlBuilder.Append(@"body { "); htmlBuilder.Append(@"font-family: 'Segoe UI', sans-serif; "); htmlBuilder.Append($"font-size: {this.SentenceFontSize}px; "); htmlBuilder.Append(@"line-height: 1.5; margin: 5px; padding: 0; "); htmlBuilder.Append(@"height: 100%; "); htmlBuilder.Append(@"overflow-wrap: break-word; word-wrap: break-word; "); htmlBuilder.Append(@"background-color: transparent; color: black; "); htmlBuilder.Append(@"-webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; "); htmlBuilder.Append(@"overflow-y: auto; "); htmlBuilder.Append(@"scrollbar-width: none; "); htmlBuilder.Append(@"-ms-overflow-style: none; "); htmlBuilder.Append(@"} "); htmlBuilder.Append(@"body::-webkit-scrollbar { display: none; } "); htmlBuilder.Append(@".token-span { cursor: default; padding: 0 1px; display: inline; border-radius: 3px; transition: background-color 0.1s ease-in-out, color 0.1s ease-in-out; } "); htmlBuilder.Append(@".selectable { }"); htmlBuilder.Append(@".non-selectable { color: #999; }"); htmlBuilder.Append(@".highlighted-sentence { background-color: LightSkyBlue; } "); htmlBuilder.Append(@".highlighted-definition { background-color: #406080; color: white; } "); htmlBuilder.Append(@".focus-sentence .highlighted-sentence { } "); htmlBuilder.Append(@".focus-definition .highlighted-definition { } "); htmlBuilder.Append(@"</style></head><body id='sentence-body' class='focus-sentence'>");
        for (int i = 0; i < displayTokens.Count; i++) { var token = displayTokens[i]; string escapedSurface = System.Security.SecurityElement.Escape(token.Surface) ?? string.Empty; string selectableClass = token.IsSelectable ? "selectable" : "non-selectable"; htmlBuilder.Append($"<span id='token-{i}' class='token-span {selectableClass}'>{escapedSurface}</span>"); }
        htmlBuilder.Append(@"<script>let currentHighlightId = null; let currentHighlightClass = ''; function highlightToken(tokenId, focusTarget) { try { const newElement = document.getElementById(tokenId); const body = document.getElementById('sentence-body'); let highlightClass = ''; if (focusTarget === 'Sentence') { highlightClass = 'highlighted-sentence'; body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); } else { highlightClass = 'highlighted-definition'; body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); } if (currentHighlightId) { const oldElement = document.getElementById(currentHighlightId); if (oldElement) { oldElement.classList.remove('highlighted-sentence', 'highlighted-definition'); } } if (newElement) { newElement.classList.add(highlightClass); currentHighlightId = tokenId; currentHighlightClass = highlightClass; if (focusTarget === 'Sentence') { newElement.scrollIntoView({ behavior: 'auto', block: 'nearest', inline: 'nearest' }); } } else { currentHighlightId = null; currentHighlightClass = ''; } } catch(e) {}} function setFocusClass(focusTarget) { try { const body = document.getElementById('sentence-body'); const currentElement = currentHighlightId ? document.getElementById(currentHighlightId) : null; let newHighlightClass = ''; if (focusTarget === 'Sentence') { body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); newHighlightClass = 'highlighted-sentence'; } else { body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); newHighlightClass = 'highlighted-definition'; } if (currentElement && currentHighlightClass !== newHighlightClass) { currentElement.classList.remove(currentHighlightClass); currentElement.classList.add(newHighlightClass); currentHighlightClass = newHighlightClass; } } catch(e) {}}</script></body></html>");
        TaskCompletionSource<bool> navigationCompletedTcs = new TaskCompletionSource<bool>(); TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandler = null;
        try { navigationCompletedHandler = (s, a) => { if (SentenceWebView?.CoreWebView2 != null) SentenceWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandler; navigationCompletedTcs.TrySetResult(a.IsSuccess); }; try { SentenceWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandler; } catch { } SentenceWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandler; SentenceWebView.CoreWebView2.NavigateToString(htmlBuilder.ToString()); bool navigationSuccess = await navigationCompletedTcs.Task; if (navigationSuccess) { await HighlightTokenInWebViewAsync(ViewModel.SelectedTokenIndex); SentenceWebView.Visibility = Visibility.Visible; } else { SentenceWebView.Visibility = Visibility.Collapsed; } } catch (Exception) { SentenceWebView.Visibility = Visibility.Collapsed; navigationCompletedTcs.TrySetResult(false); }
    }

    private async Task LoadDefinitionHtmlAsync(string? fullDefinitionHtml)
    {
        DefinitionWebView.Visibility = Visibility.Collapsed; _definitionLiCount = 0; _currentDefinitionLiIndex = -1; if (!_isDefinitionWebViewReady || DefinitionWebView.CoreWebView2 == null) { return; }
        if (string.IsNullOrWhiteSpace(fullDefinitionHtml)) { DefinitionWebView.CoreWebView2.Navigate("about:blank"); return; }
        TaskCompletionSource<bool> navigationCompletedTcsDef = new TaskCompletionSource<bool>(); TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandlerDef = null;
        try { navigationCompletedHandlerDef = (s, a) => { if (DefinitionWebView?.CoreWebView2 != null) DefinitionWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandlerDef; navigationCompletedTcsDef.TrySetResult(a.IsSuccess); }; try { DefinitionWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandlerDef; } catch { } DefinitionWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandlerDef; DefinitionWebView.CoreWebView2.NavigateToString(fullDefinitionHtml); bool navigationSuccessDef = await navigationCompletedTcsDef.Task; if (navigationSuccessDef) { string scriptInitNav = "initializeNavigation()"; string result = "0"; try { result = await DefinitionWebView.CoreWebView2.ExecuteScriptAsync(scriptInitNav); } catch { } if (!string.IsNullOrEmpty(result) && result != "null" && int.TryParse(result, out int count)) { _definitionLiCount = count; } else { _definitionLiCount = 0; } _currentDefinitionLiIndex = -1; DefinitionWebView.Visibility = Visibility.Visible; } else { DefinitionWebView.Visibility = Visibility.Collapsed; } } catch (Exception) { DefinitionWebView.Visibility = Visibility.Collapsed; navigationCompletedTcsDef.TrySetResult(false); }
    }

    private async Task UpdateDefinitionTokenDetailsAsync(string tokenDetailHtmlSnippet) // Re-added
    {
        if (!_isDefinitionWebViewReady || DefinitionWebView.CoreWebView2 == null) return; string escapedSnippet = JsonSerializer.Serialize(tokenDetailHtmlSnippet); string script = $"updateTokenDetails({escapedSnippet});";
        try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WebView2 Definition] Error executing token update script: {ex.Message}"); ViewModel.ForceFullDefinitionReload(); await ViewModel.UpdateDefinitionViewAsync(); } // Fallback call
    }

    private async Task HighlightTokenInWebViewAsync(int displayTokenIndex) { if (!_isSentenceWebViewReady || SentenceWebView.CoreWebView2 == null) return; string targetTokenId = $"token-{displayTokenIndex}"; if (displayTokenIndex < 0 || displayTokenIndex >= ViewModel.DisplayTokens.Count) { targetTokenId = "null"; } try { await SentenceWebView.CoreWebView2.ExecuteScriptAsync($"highlightToken('{targetTokenId}', '{ViewModel.DetailFocusTarget}');"); } catch { } }
    private async Task HighlightDefinitionItemAsync(int index) { if (!_isDefinitionWebViewReady || DefinitionWebView.CoreWebView2 == null || _definitionLiCount <= 0) return; _currentDefinitionLiIndex = Math.Clamp(index, -1, _definitionLiCount - 1); try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"highlightListItem({_currentDefinitionLiIndex});"); } catch { } }
    private async Task UpdateDetailFocusVisuals() { if (ViewModel.CurrentViewInternal != "detail") return; if (_isSentenceWebViewReady && SentenceWebView.CoreWebView2 != null) { try { await SentenceWebView.CoreWebView2.ExecuteScriptAsync($"setFocusClass('{ViewModel.DetailFocusTarget}');"); } catch { } } if (_isDefinitionWebViewReady && DefinitionWebView.CoreWebView2 != null) { try { await DefinitionWebView.CoreWebView2.ExecuteScriptAsync($"setDefinitionFocus({(ViewModel.DetailFocusTarget == "Definition").ToString().ToLowerInvariant()});"); } catch { } } bool focused = SentenceFocusAnchor.Focus(FocusState.Programmatic); if (!focused) { if (DetailViewContentGrid != null) { focused = DetailViewContentGrid.Focus(FocusState.Programmatic); } if (!focused && this.Content is FrameworkElement rootElement) { focused = rootElement.Focus(FocusState.Programmatic); } } }

    private void CheckGamepadLocalInput(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) { if (_localGamepad == null || !ViewModel.CanInteractWithDetailView) return; var now = DateTime.UtcNow; if (now - _lastGamepadNavTime < _gamepadNavDebounce) return; try { GamepadReading reading = _localGamepad.GetCurrentReading(); bool processed = false; const double triggerThr = 0.75; const double deadzone = 0.7; foreach (var kvp in _localButtonToActionId) { GamepadButtons button = kvp.Key; string actionId = kvp.Value; if (reading.Buttons.HasFlag(button)) { ExecuteLocalAction(actionId); processed = true; break; } } if (!processed) { string? triggerId = null; string? ltAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZL").Key; string? rtAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZR").Key; if (ltAct != null && reading.LeftTrigger > triggerThr) triggerId = ltAct; else if (rtAct != null && reading.RightTrigger > triggerThr) triggerId = rtAct; if (triggerId != null) { ExecuteLocalAction(triggerId); processed = true; } } if (!processed) { string? axisId = null; double ly = reading.LeftThumbstickY; double lx = reading.LeftThumbstickX; if (ly > deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_UP"); else if (ly < -deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_DOWN"); else if (lx < -deadzone) axisId = FindActionForAxis("STICK_LEFT_X_LEFT"); else if (lx > deadzone) axisId = FindActionForAxis("STICK_LEFT_X_RIGHT"); if (axisId != null) { ExecuteLocalAction(axisId); processed = true; } } if (processed) { _lastGamepadNavTime = now; } } catch { _localGamepad = null; _gamepadPollingTimer?.Stop(); } }
    private void MainWindow_Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e) { if (!ViewModel.CanInteractWithDetailView && e.Key != VirtualKey.Escape && ViewModel.CurrentViewInternal == "detail") { e.Handled = true; return; } var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control); var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift); bool ctrlPressed = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); bool shiftPressed = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); if (ctrlPressed && shiftPressed && e.Key == VirtualKey.D) { if (_isDefinitionWebViewReady && DefinitionWebView?.CoreWebView2 != null) { DefinitionWebView.CoreWebView2.OpenDevToolsWindow(); e.Handled = true; return; } } if (ctrlPressed && shiftPressed && e.Key == VirtualKey.S) { if (_isSentenceWebViewReady && SentenceWebView?.CoreWebView2 != null) { SentenceWebView.CoreWebView2.OpenDevToolsWindow(); e.Handled = true; return; } } if (e.Handled) { return; } if (_localKeyToActionId.TryGetValue(e.Key, out string? actionId) && actionId != null) { ExecuteLocalAction(actionId); e.Handled = true; } else { bool lvFocus = false; XamlRoot? xr = (this.Content as UIElement)?.XamlRoot; if (xr != null) { var fe = FocusManager.GetFocusedElement(xr); lvFocus = fe is ListView || fe is ListViewItem; } if (e.Key == VirtualKey.Enter && lvFocus) { ExecuteLocalAction("DETAIL_ENTER"); e.Handled = true; } } }
    private async void ExecuteLocalAction(string actionId) { if (!ViewModel.CanInteractWithDetailView && actionId != "DETAIL_BACK" && ViewModel.CurrentViewInternal == "detail") return; bool handled = false; if (ViewModel.CurrentViewInternal == "list") { switch (actionId) { case "NAV_UP": ViewModel.MoveSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveSelection(1); handled = true; break; case "DETAIL_ENTER": if (ViewModel.SelectedSentenceIndex >= 0 && ViewModel.SelectedSentenceIndex < ViewModel.SentenceHistory.Count) { ViewModel.EnterDetailView(); handled = true; } break; case "DELETE_WORD": handled = true; break; } } else if (ViewModel.CurrentViewInternal == "detail") { if (ViewModel.DetailFocusTarget == "Sentence") { switch (actionId) { case "NAV_LEFT": ViewModel.MoveTokenSelection(-1); handled = true; break; case "NAV_RIGHT": ViewModel.MoveTokenSelection(1); handled = true; break; case "NAV_UP": ViewModel.MoveKanjiTokenSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveKanjiTokenSelection(1); handled = true; break; case "DETAIL_ENTER": ViewModel.FocusDefinitionArea(); handled = true; break; case "DETAIL_BACK": ViewModel.ExitDetailView(); await DispatcherQueue_EnqueueAsync(TrySetFocusToListOrRoot, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low); handled = true; break; case "SAVE_WORD": handled = true; break; } } else if (ViewModel.DetailFocusTarget == "Definition") { if (actionId == "DETAIL_BACK") { ViewModel.FocusSentenceArea(); handled = true; } else if (ViewModel.SelectedToken?.MecabData != null && !ViewModel.IsAIModeActive) { switch (actionId) { case "NAV_UP": if (_definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex - 1); } handled = true; break; case "NAV_DOWN": if (_definitionLiCount > 0) { await HighlightDefinitionItemAsync(_currentDefinitionLiIndex + 1); } handled = true; break; case "DETAIL_ENTER": if (_currentDefinitionLiIndex >= 0 && _isDefinitionWebViewReady && DefinitionWebView.CoreWebView2 != null) { try { /* ... JMDict link logic ... */ } catch { } } handled = true; break; } } } } if (handled && ViewModel.CurrentViewInternal == "list" && actionId.StartsWith("NAV_") && ViewModel.SelectedSentenceItem != null) { await DispatcherQueue_EnqueueAsync(() => { if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem); }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low); } }

    private void reset_navigation_state() => _lastGamepadNavTime = DateTime.MinValue;
    private GamepadButtons? MapInternalCodeToGamepadButton(string code) => code.ToUpperInvariant() switch { "BTN_A" => GamepadButtons.A, "BTN_B" => GamepadButtons.B, "BTN_X" => GamepadButtons.X, "BTN_Y" => GamepadButtons.Y, "BTN_TL" => GamepadButtons.LeftShoulder, "BTN_TR" => GamepadButtons.RightShoulder, "BTN_SELECT" => GamepadButtons.View, "BTN_START" => GamepadButtons.Menu, "BTN_THUMBL" => GamepadButtons.LeftThumbstick, "BTN_THUMBR" => GamepadButtons.RightThumbstick, "DPAD_UP" => GamepadButtons.DPadUp, "DPAD_DOWN" => GamepadButtons.DPadDown, "DPAD_LEFT" => GamepadButtons.DPadLeft, "DPAD_RIGHT" => GamepadButtons.DPadRight, _ => null };
    private string? FindActionForAxis(string axis) => _currentSettings.LocalJoystickBindings switch { null => null, var b => axis switch { "STICK_LEFT_Y_UP" => b.ContainsValue("NAV_UP") ? "NAV_UP" : null, "STICK_LEFT_Y_DOWN" => b.ContainsValue("NAV_DOWN") ? "NAV_DOWN" : null, "STICK_LEFT_X_LEFT" => b.ContainsValue("NAV_LEFT") ? "NAV_LEFT" : null, "STICK_LEFT_X_RIGHT" => b.ContainsValue("NAV_RIGHT") ? "NAV_RIGHT" : null, _ => null } };
    private void SetupGamepadPollingTimer() { var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); if (dq == null) return; _gamepadPollingTimer = dq.CreateTimer(); _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50); _gamepadPollingTimer.Tick += CheckGamepadLocalInput; }
    private void TryHookCorrectGamepad() { Gamepad? prev = _localGamepad; _localGamepad = null; Gamepad? pot = null; var gamepads = Gamepad.Gamepads; foreach (var gp in gamepads) { RawGameController? raw = null; try { raw = RawGameController.FromGameController(gp); } catch { } bool isRawPro = raw != null && raw.HardwareVendorId == 0x057E; if (!isRawPro) { pot = gp; break; } else if (pot == null) { pot = gp; } } _localGamepad = pot; if (_localGamepad != null) { try { _ = _localGamepad.GetCurrentReading(); } catch { _localGamepad = null; } } }
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) { if (args.WindowActivationState == WindowActivationState.Deactivated) { PauseLocalPolling(); reset_navigation_state(); } else { TryHookCorrectGamepad(); ResumeLocalPolling(); } }
    private void Gamepad_GamepadAdded(object? sender, Gamepad e) { TryHookCorrectGamepad(); ResumeLocalPolling(); }
    private void Gamepad_GamepadRemoved(object? sender, Gamepad e) { TryHookCorrectGamepad(); if (_localGamepad == null) PauseLocalPolling(); }
    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListView { SelectedItem: not null } lv) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (lv.SelectedItem != null) lv.ScrollIntoView(lv.SelectedItem); }); } }
    private void MainWindow_Closed(object sender, WindowEventArgs args) { _highlightThrottleTimer?.Stop(); if (ViewModel != null) { ViewModel.PropertyChanged -= ViewModel_PropertyChanged; ViewModel.RequestLoadDefinitionHtml -= ViewModel_RequestLoadDefinitionHtml; ViewModel.RequestRenderSentenceWebView -= ViewModel_RequestRenderSentenceWebView; ViewModel.RequestUpdateDefinitionTokenDetails -= ViewModel_RequestUpdateDefinitionTokenDetails; ViewModel.Cleanup(); } PauseLocalPolling(); Gamepad.GamepadAdded -= Gamepad_GamepadAdded; Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved; SentenceWebView?.Close(); DefinitionWebView?.Close(); _localGamepad = null; _gamepadPollingTimer = null; }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
    private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args) { if (sender is SettingsWindow closedWindow) { closedWindow.Closed -= SettingsWindow_Instance_Closed; } _settingsWindowInstance = null; ViewModel?.LoadCurrentSettings(); ViewModel?.ForceFullDefinitionReload(); ResumeLocalPolling(); TrySetFocusToListOrRoot(); } // Corregido
    private void TrySetFocusToListOrRoot() { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { bool fs = HistoryListView.Focus(FocusState.Programmatic); if (!fs && this.Content is FrameworkElement root) { fs = root.Focus(FocusState.Programmatic); } }); }
    public void UpdateLocalBindings(AppSettings settings) { _currentSettings = settings ?? new AppSettings(); _localKeyToActionId.Clear(); _localButtonToActionId.Clear(); if (_currentSettings.LocalKeyboardBindings != null) { foreach (var kvp in _currentSettings.LocalKeyboardBindings) { if (!string.IsNullOrEmpty(kvp.Value) && Enum.TryParse<VirtualKey>(kvp.Value, true, out var vk)) { _localKeyToActionId[vk] = kvp.Key; } } } if (_currentSettings.LocalJoystickBindings != null) { foreach (var kvp in _currentSettings.LocalJoystickBindings) { string actionId = kvp.Key; string? buttonCode = kvp.Value; if (!string.IsNullOrEmpty(buttonCode)) { GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode); if (buttonEnum.HasValue) { _localButtonToActionId[buttonEnum.Value] = actionId; } } } } reset_navigation_state(); }
    public void ToggleVisibility() => DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
    public void TriggerMenuToggle() => DispatcherQueue?.TryEnqueue(() => { if (ViewModel != null) { if (ViewModel.CurrentViewInternal != "menu") ViewModel.EnterMenu(); else ViewModel.ExitMenu(); } });
    public void PauseLocalPolling() { _gamepadPollingTimer?.Stop(); }
    public void ResumeLocalPolling() { DispatcherQueue?.TryEnqueue(() => { bool isActive = false; try { HWND fWnd = PInvoke.GetForegroundWindow(); HWND myWnd = (HWND)WindowNative.GetWindowHandle(this); isActive = fWnd == myWnd; } catch { isActive = false; } if (isActive && _localGamepad != null && _gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning) { _gamepadPollingTimer.Start(); } }); }
    public void ShowSettingsWindow() { if (_settingsWindowInstance != null) { _settingsWindowInstance.Activate(); return; } _settingsWindowInstance = new SettingsWindow(); _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed; PauseLocalPolling(); _settingsWindowInstance.Activate(); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private Task DispatcherQueue_EnqueueAsync(Action function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (DispatcherQueue.TryEnqueue(priority, () => { try { function(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { } else { tcs.SetException(new InvalidOperationException("Failed to enqueue action.")); } return tcs.Task; }
    private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (DispatcherQueue.TryEnqueue(priority, async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { } else { tcs.SetException(new InvalidOperationException("Failed to enqueue async action.")); } return tcs.Task; }
}