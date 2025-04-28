using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
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

    private bool _isWebViewReady = false;
    private int _lastSelectedActualIndex = -1;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _highlightThrottleTimer;
    private readonly TimeSpan _highlightThrottleInterval = TimeSpan.FromMilliseconds(100);
    private DateTime _lastHighlightTime = DateTime.MinValue;
    private bool _isHighlightUpdatePending = false;
    private int _pendingHighlightIndex = -1;

    public MainWindow()
    {
        this.InitializeComponent();
        _windowActivationService = new WindowActivationService();
        IntPtr hwndPtr = WindowNative.GetWindowHandle(this);
        _windowActivationService.RegisterWindowHandle(hwndPtr);
        ViewModel = new MainWindowViewModel();
        if (this.Content is FrameworkElement rootElement) { rootElement.DataContext = ViewModel; rootElement.PreviewKeyDown += MainWindow_Root_PreviewKeyDown; }
        this.Title = "Jpn Study Tool"; this.Closed += MainWindow_Closed; this.Activated += MainWindow_Activated;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateViewVisibility(ViewModel.CurrentViewInternal);
        Gamepad.GamepadAdded += Gamepad_GamepadAdded;
        Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
        TryHookCorrectGamepad();
        SetupGamepadPollingTimer();
        InitializeHighlightThrottleTimer();
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
        else
        {
            System.Diagnostics.Debug.WriteLine("[Throttle] CRITICAL: Failed to get DispatcherQueue for throttle timer.");
        }
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
        if (args.Exception != null)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2] CoreWebView2 Initialization failed: {args.Exception.Message}");
            return;
        }
        System.Diagnostics.Debug.WriteLine("[WebView2] CoreWebView2 Initialized successfully.");
        _isWebViewReady = true;

        if (sender.CoreWebView2 != null)
        {
            sender.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            sender.CoreWebView2.Settings.AreDevToolsEnabled = true;
            sender.CoreWebView2.Settings.IsStatusBarEnabled = false;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[WebView2] CoreWebView2 was null immediately after initialization check. Unexpected.");
            _isWebViewReady = false;
            return;
        }


        if (ViewModel.CurrentViewInternal == "detail")
        {
            await BuildAndLoadSentenceHtmlAsync();
        }
    }


    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CurrentViewInternal))
        {
            await DispatcherQueue_EnqueueAsync(async () => await UpdateViewVisibility(ViewModel.CurrentViewInternal));
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
                _highlightThrottleTimer?.Stop();
                _isHighlightUpdatePending = false;

                await DispatcherQueue_EnqueueAsync(async () => await HighlightTokenInWebViewAsync(newIndex));
                _lastHighlightTime = DateTime.UtcNow;
            }
            else if (!_isHighlightUpdatePending)
            {
                var delayNeeded = _highlightThrottleInterval - timeSinceLastHighlight;
                System.Diagnostics.Debug.WriteLine($"[Throttle] Throttling active. Scheduling highlight for index {newIndex} in {delayNeeded.TotalMilliseconds}ms.");
                _isHighlightUpdatePending = true;
                if (_highlightThrottleTimer != null)
                {
                    _highlightThrottleTimer.Interval = delayNeeded > TimeSpan.Zero ? delayNeeded : TimeSpan.FromMilliseconds(1);
                    _highlightThrottleTimer.Start();
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Throttle] Throttling active. Update already pending for latest index ({_pendingHighlightIndex}). Ignoring request for {newIndex}.");
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                DefinitionScrollViewer?.ChangeView(null, 0, null, true);
            });
        }
        else if (e.PropertyName == nameof(ViewModel.DetailFocusTarget))
        {
            await DispatcherQueue_EnqueueAsync(async () => await UpdateDetailFocusVisuals());
        }
    }

    private async Task UpdateViewVisibility(string? currentView)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateViewVisibility: State={currentView ?? "null"}");
        switch (currentView)
        {
            case "list":
                ListViewContentGrid.Visibility = Visibility.Visible;
                DetailViewContentGrid.Visibility = Visibility.Collapsed;
                if (_isWebViewReady && SentenceWebView.CoreWebView2 != null)
                {
                    SentenceWebView.CoreWebView2.Navigate("about:blank");
                }
                SentenceWebView.Visibility = Visibility.Collapsed;
                TrySetFocusToListOrRoot();
                break;
            case "detail":
                ListViewContentGrid.Visibility = Visibility.Collapsed;
                DetailViewContentGrid.Visibility = Visibility.Visible;
                SentenceWebView.Visibility = Visibility.Collapsed;
                await BuildAndLoadSentenceHtmlAsync();
                break;
            default:
                ListViewContentGrid.Visibility = Visibility.Visible;
                DetailViewContentGrid.Visibility = Visibility.Collapsed;
                SentenceWebView.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async Task BuildAndLoadSentenceHtmlAsync()
    {
        if (!_isWebViewReady || SentenceWebView.CoreWebView2 == null)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2] Cannot build HTML: WebView not ready or CoreWebView2 is null.");
            SentenceWebView.Visibility = Visibility.Collapsed;
            return;
        }

        if (ViewModel.Tokens == null || !ViewModel.Tokens.Any())
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2] No tokens to display.");
            SentenceWebView.CoreWebView2.Navigate("about:blank");
            SentenceWebView.Visibility = Visibility.Collapsed;
            return;
        }

        StringBuilder htmlBuilder = new StringBuilder();
        htmlBuilder.Append(@"
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset='UTF-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <style>
                    body { font-family: 'Segoe UI', sans-serif; font-size: ");
        htmlBuilder.Append(this.SentenceFontSize); htmlBuilder.Append(@"px;
                        line-height: 1.5; margin: 5px; padding: 0; overflow-wrap: break-word; word-wrap: break-word;
                        background-color: transparent; color: black;
                        -webkit-user-select: none; -moz-user-select: none; -ms-user-select: none; user-select: none; }
                    .token-span { cursor: default; padding: 0 1px; display: inline; border-radius: 3px; transition: background-color 0.1s ease-in-out, color 0.1s ease-in-out; }
                    .highlighted-sentence { background-color: LightSkyBlue; }
                    .highlighted-definition { background-color: #406080; color: white; }
                    .focus-sentence .highlighted-sentence { }
                    .focus-definition .highlighted-definition { }
                    html, body { height: 100%; box-sizing: border-box; }
                </style>
            </head>
            <body id='sentence-body' class='focus-sentence'>");

        int actualIndex = 0;
        foreach (var token in ViewModel.Tokens)
        {
            string escapedSurface = System.Security.SecurityElement.Escape(token.Surface) ?? string.Empty;
            htmlBuilder.Append($"<span id='token-{actualIndex}' class='token-span {(token.IsSelectable ? "selectable" : "")}'>");
            htmlBuilder.Append(escapedSurface);
            htmlBuilder.Append("</span>");
            actualIndex++;
        }

        htmlBuilder.Append(@"
            <script>
                let currentHighlightId = null; let currentHighlightClass = '';
                function highlightToken(tokenId, focusTarget) {
                    const newElement = document.getElementById(tokenId); const body = document.getElementById('sentence-body');
                    let highlightClass = '';
                    if (focusTarget === 'Sentence') {
                        highlightClass = 'highlighted-sentence'; body.classList.remove('focus-definition'); body.classList.add('focus-sentence');
                    } else {
                        highlightClass = 'highlighted-definition'; body.classList.remove('focus-sentence'); body.classList.add('focus-definition');
                    }
                    if (currentHighlightId) {
                        const oldElement = document.getElementById(currentHighlightId);
                        if (oldElement) { oldElement.classList.remove('highlighted-sentence', 'highlighted-definition'); }
                    }
                    if (newElement) {
                        newElement.classList.add(highlightClass); currentHighlightId = tokenId; currentHighlightClass = highlightClass;
                         if (focusTarget === 'Sentence') { newElement.scrollIntoView({ behavior: 'auto', block: 'nearest', inline: 'nearest' }); }
                    } else { currentHighlightId = null; currentHighlightClass = ''; }
                }
                 function setFocusClass(focusTarget) {
                     const body = document.getElementById('sentence-body'); const currentElement = currentHighlightId ? document.getElementById(currentHighlightId) : null;
                     let newHighlightClass = '';
                     if (focusTarget === 'Sentence') {
                         body.classList.remove('focus-definition'); body.classList.add('focus-sentence'); newHighlightClass = 'highlighted-sentence';
                     } else {
                         body.classList.remove('focus-sentence'); body.classList.add('focus-definition'); newHighlightClass = 'highlighted-definition';
                     }
                     if (currentElement && currentHighlightClass !== newHighlightClass) {
                         currentElement.classList.remove(currentHighlightClass); currentElement.classList.add(newHighlightClass); currentHighlightClass = newHighlightClass;
                     }
                 }
            </script>
            </body></html>");

        try
        {
            TaskCompletionSource<bool> navigationCompletedTcs = new TaskCompletionSource<bool>();
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandler = null;

            navigationCompletedHandler = (sender, args) =>
            {
                if (sender is CoreWebView2 coreSender)
                {
                    coreSender.NavigationCompleted -= navigationCompletedHandler;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[WebView2] NavigationCompleted sender was not CoreWebView2.");
                    SentenceWebView.CoreWebView2.NavigationCompleted -= navigationCompletedHandler;
                }

                if (args.IsSuccess)
                {
                    navigationCompletedTcs.TrySetResult(true);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WebView2] Navigation failed with error code: {args.WebErrorStatus}");
                    navigationCompletedTcs.TrySetResult(false);
                }
            };

            SentenceWebView.CoreWebView2.NavigationCompleted += navigationCompletedHandler;

            System.Diagnostics.Debug.WriteLine("[WebView2] Navigating WebView2 to generated HTML string...");
            SentenceWebView.CoreWebView2.NavigateToString(htmlBuilder.ToString());

            bool navigationSuccess = await navigationCompletedTcs.Task;

            if (navigationSuccess)
            {
                System.Diagnostics.Debug.WriteLine("[WebView2] Navigation successful. Highlighting initial token...");
                await HighlightTokenInWebViewAsync(ViewModel.SelectedTokenIndex);
                SentenceWebView.Visibility = Visibility.Visible;
                if (ViewModel.DetailFocusTarget == "Sentence")
                {
                    SentenceFocusAnchor.Focus(FocusState.Programmatic);
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[WebView2] Navigation failed. WebView remains hidden.");
                SentenceWebView.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2] Error navigating/loading HTML or setting visibility: {ex.Message}");
            SentenceWebView.Visibility = Visibility.Collapsed;
        }
    }


    private async Task HighlightTokenInWebViewAsync(int selectedSelectableIndex)
    {
        if (!_isWebViewReady || ViewModel.Tokens == null || !ViewModel.Tokens.Any() || SentenceWebView.CoreWebView2 == null) return;

        int actualIndex = ViewModel.GetActualTokenIndex(selectedSelectableIndex);
        string targetTokenId = "null";

        if (actualIndex < 0 || actualIndex >= ViewModel.Tokens.Count)
        {
            actualIndex = -1;
            System.Diagnostics.Debug.WriteLine($"[WebView2] Invalid selectable index {selectedSelectableIndex}. Clearing highlight.");
        }
        else
        {
            targetTokenId = $"token-{actualIndex}";
            System.Diagnostics.Debug.WriteLine($"[WebView2] Highlighting selectable index {selectedSelectableIndex} (actual index {actualIndex}). Focus: {ViewModel.DetailFocusTarget}");
        }

        try
        {
            string script = $"highlightToken('{targetTokenId}', '{ViewModel.DetailFocusTarget}');";
            await SentenceWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2] Error executing highlight script: {ex.Message}");
        }
    }


    private async Task UpdateDetailFocusVisuals()
    {
        if (ViewModel.CurrentViewInternal != "detail") return;

        System.Diagnostics.Debug.WriteLine($"[Focus] Updating focus visuals. Target: {ViewModel.DetailFocusTarget}");

        if (_isWebViewReady && SentenceWebView.CoreWebView2 != null)
        {
            try
            {
                string script = $"setFocusClass('{ViewModel.DetailFocusTarget}');";
                await SentenceWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebView2] Error executing focus update script: {ex.Message}");
            }
        }

        if (ViewModel.DetailFocusTarget == "Definition")
        {
            if (!DefinitionScrollViewer.Focus(FocusState.Programmatic))
            {
                DefinitionTextBlock.Focus(FocusState.Programmatic);
            }
            System.Diagnostics.Debug.WriteLine("[Focus] Set UI focus to Definition area.");
        }
        else
        {
            bool focused = SentenceFocusAnchor.Focus(FocusState.Programmatic);
            System.Diagnostics.Debug.WriteLine($"[Focus] Attempted to set UI focus to SentenceFocusAnchor. Success: {focused}");
            if (!focused)
            {
                if (DetailViewContentGrid != null)
                {
                    focused = DetailViewContentGrid.Focus(FocusState.Programmatic);
                    System.Diagnostics.Debug.WriteLine($"[Focus] Fallback: Attempted to set UI focus to DetailViewContentGrid. Success: {focused}");
                }
                if (!focused && this.Content is FrameworkElement rootElement)
                {
                    focused = rootElement.Focus(FocusState.Programmatic);
                    System.Diagnostics.Debug.WriteLine($"[Focus] Final Fallback: Attempted to set UI focus to Root Element. Success: {focused}");
                }
            }
        }
    }


    private void ScrollDefinition(double scrollDelta)
    {
        if (ViewModel.CurrentViewInternal == "detail" && ViewModel.DetailFocusTarget == "Definition")
        {
            double newOffset = DefinitionScrollViewer.VerticalOffset + scrollDelta;
            DefinitionScrollViewer.ChangeView(null, newOffset, null, false);
        }
    }

    public void UpdateLocalBindings(AppSettings settings) { System.Diagnostics.Debug.WriteLine("[MainWindow] === Updating local bindings START ==="); _currentSettings = settings ?? new AppSettings(); _localKeyToActionId.Clear(); _localButtonToActionId.Clear(); if (_currentSettings.LocalKeyboardBindings != null) { foreach (var kvp in _currentSettings.LocalKeyboardBindings) { if (!string.IsNullOrEmpty(kvp.Value) && Enum.TryParse<VirtualKey>(kvp.Value, true, out var vk)) { _localKeyToActionId[vk] = kvp.Key; } } } if (_currentSettings.LocalJoystickBindings != null) { foreach (var kvp in _currentSettings.LocalJoystickBindings) { string actionId = kvp.Key; string? buttonCode = kvp.Value; if (!string.IsNullOrEmpty(buttonCode)) { GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode); if (buttonEnum.HasValue) { _localButtonToActionId[buttonEnum.Value] = actionId; } } } } System.Diagnostics.Debug.WriteLine("[MainWindow] === Finished updating local bindings ==="); reset_navigation_state(); }
    public void ToggleVisibility() => DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
    public void TriggerMenuToggle() => DispatcherQueue?.TryEnqueue(() => { if (ViewModel.CurrentViewInternal != "menu") ViewModel.EnterMenu(); else ViewModel.ExitMenu(); });
    public void ShowSettingsWindow() { if (_settingsWindowInstance == null) { _settingsWindowInstance = new SettingsWindow(); _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed; _settingsWindowInstance.Activate(); } else { _settingsWindowInstance.Activate(); } }
    public void PauseLocalPolling() { _gamepadPollingTimer?.Stop(); System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling PAUSED explicitly."); }
    public void ResumeLocalPolling() { DispatcherQueue?.TryEnqueue(() => { bool isActive = false; try { HWND fWnd = PInvoke.GetForegroundWindow(); HWND myWnd = (HWND)WindowNative.GetWindowHandle(this); isActive = fWnd == myWnd; } catch { isActive = false; } if (isActive && _localGamepad != null && _gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning) { _gamepadPollingTimer.Start(); System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling RESUMED explicitly."); } }); }
    private void MainWindow_Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e) { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Key={e.Key}, OriginalSource={e.OriginalSource?.GetType().Name}, Handled={e.Handled}"); if (e.Handled) { System.Diagnostics.Debug.WriteLine($"[Input] Event already handled by {e.OriginalSource?.GetType().Name}. Skipping."); return; } if (_localKeyToActionId.TryGetValue(e.Key, out string? actionId) && actionId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Mapped {e.Key} to Action: {actionId}"); ExecuteLocalAction(actionId); e.Handled = true; } else { bool lvFocus = false; XamlRoot? xr = (this.Content as UIElement)?.XamlRoot; if (xr != null) { var fe = FocusManager.GetFocusedElement(xr); lvFocus = fe is ListView || fe is ListViewItem; } if (e.Key == VirtualKey.Enter && lvFocus) { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Enter on ListView(Item). Attempting DETAIL_ENTER."); ExecuteLocalAction("DETAIL_ENTER"); e.Handled = true; } else { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Key {e.Key} not mapped or handled by specific logic."); e.Handled = false; } } }
    private void CheckGamepadLocalInput() { if (_localGamepad == null) return; var now = DateTime.UtcNow; if (now - _lastGamepadNavTime < _gamepadNavDebounce) return; try { GamepadReading reading = _localGamepad.GetCurrentReading(); bool processed = false; const double triggerThr = 0.75; const double deadzone = 0.7; foreach (var kvp in _localButtonToActionId) { GamepadButtons button = kvp.Key; string actionId = kvp.Value; bool hasFlag = reading.Buttons.HasFlag(button); if (hasFlag) { System.Diagnostics.Debug.WriteLine($"[Input] Local Button: {button} TRIGGERED Action: {actionId}"); ExecuteLocalAction(actionId); processed = true; break; } } if (!processed) { string? triggerId = null; string? ltAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZL").Key; string? rtAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZR").Key; if (ltAct != null && reading.LeftTrigger > triggerThr) triggerId = ltAct; else if (rtAct != null && reading.RightTrigger > triggerThr) triggerId = rtAct; if (triggerId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Local Trigger mapped to Action: {triggerId}"); ExecuteLocalAction(triggerId); processed = true; } } if (!processed) { string? axisId = null; double ly = reading.LeftThumbstickY; double lx = reading.LeftThumbstickX; if (ly > deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_UP"); else if (ly < -deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_DOWN"); else if (lx < -deadzone) axisId = FindActionForAxis("STICK_LEFT_X_LEFT"); else if (lx > deadzone) axisId = FindActionForAxis("STICK_LEFT_X_RIGHT"); if (axisId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Local Axis mapped to Action: {axisId}"); ExecuteLocalAction(axisId); processed = true; } } if (processed) { _lastGamepadNavTime = now; } } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Input] Error reading local gamepad: {ex.Message}"); _localGamepad = null; _gamepadPollingTimer?.Stop(); } }
    private void ExecuteLocalAction(string actionId) { System.Diagnostics.Debug.WriteLine($"[Action] Attempting Action: '{actionId}' in View: '{ViewModel.CurrentViewInternal}', Focus: '{ViewModel.DetailFocusTarget}'"); bool handled = false; if (ViewModel.CurrentViewInternal == "list") { switch (actionId) { case "NAV_UP": ViewModel.MoveSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveSelection(1); handled = true; break; case "DETAIL_ENTER": if (ViewModel.SelectedSentenceIndex >= 0 && ViewModel.SelectedSentenceIndex < ViewModel.SentenceHistory.Count) { ViewModel.EnterDetailView(); handled = true; } break; case "DELETE_WORD": handled = true; break; } } else if (ViewModel.CurrentViewInternal == "detail") { if (ViewModel.DetailFocusTarget == "Sentence") { switch (actionId) { case "NAV_LEFT": ViewModel.MoveTokenSelection(-1); handled = true; break; case "NAV_RIGHT": ViewModel.MoveTokenSelection(1); handled = true; break; case "NAV_UP": ViewModel.MoveKanjiTokenSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveKanjiTokenSelection(1); handled = true; break; case "DETAIL_ENTER": ViewModel.FocusDefinitionArea(); handled = true; break; case "DETAIL_BACK": ViewModel.ExitDetailView(); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TrySetFocusToListOrRoot); handled = true; break; case "SAVE_WORD": handled = true; break; } } else if (ViewModel.DetailFocusTarget == "Definition") { switch (actionId) { case "NAV_UP": ScrollDefinition(-80); handled = true; break; case "NAV_DOWN": ScrollDefinition(80); handled = true; break; case "DETAIL_ENTER": ViewModel.FocusSentenceArea(); handled = true; break; case "DETAIL_BACK": ViewModel.FocusSentenceArea(); handled = true; break; } } } if (!handled) { System.Diagnostics.Debug.WriteLine($"[Action] Action '{actionId}' ignored or unknown."); } if (handled && ViewModel.CurrentViewInternal == "list" && actionId.StartsWith("NAV_") && ViewModel.SelectedSentenceItem != null) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem); }); } }
    private void reset_navigation_state() => _lastGamepadNavTime = DateTime.MinValue;
    private GamepadButtons? MapInternalCodeToGamepadButton(string code) => code.ToUpperInvariant() switch { "BTN_A" => GamepadButtons.A, "BTN_B" => GamepadButtons.B, "BTN_X" => GamepadButtons.X, "BTN_Y" => GamepadButtons.Y, "BTN_TL" => GamepadButtons.LeftShoulder, "BTN_TR" => GamepadButtons.RightShoulder, "BTN_SELECT" => GamepadButtons.View, "BTN_START" => GamepadButtons.Menu, "BTN_THUMBL" => GamepadButtons.LeftThumbstick, "BTN_THUMBR" => GamepadButtons.RightThumbstick, "DPAD_UP" => GamepadButtons.DPadUp, "DPAD_DOWN" => GamepadButtons.DPadDown, "DPAD_LEFT" => GamepadButtons.DPadLeft, "DPAD_RIGHT" => GamepadButtons.DPadRight, _ => null };
    private string? FindActionForAxis(string axis) => _currentSettings.LocalJoystickBindings switch { null => null, var b => axis switch { "STICK_LEFT_Y_UP" => b.ContainsValue("NAV_UP") ? "NAV_UP" : null, "STICK_LEFT_Y_DOWN" => b.ContainsValue("NAV_DOWN") ? "NAV_DOWN" : null, "STICK_LEFT_X_LEFT" => b.ContainsValue("NAV_LEFT") ? "NAV_LEFT" : null, "STICK_LEFT_X_RIGHT" => b.ContainsValue("NAV_RIGHT") ? "NAV_RIGHT" : null, _ => null } };
    private void SetupGamepadPollingTimer()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq == null) return;
        _gamepadPollingTimer = dq.CreateTimer();
        _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50);
        _gamepadPollingTimer.Tick += (s, e) => CheckGamepadLocalInput();
    }
    private void TryHookCorrectGamepad() { Gamepad? prev = _localGamepad; _localGamepad = null; Gamepad? pot = null; var gamepads = Gamepad.Gamepads; foreach (var gp in gamepads) { RawGameController? raw = null; try { raw = RawGameController.FromGameController(gp); } catch { } bool isRawPro = raw != null && raw.HardwareVendorId == 0x057E; if (!isRawPro) { pot = gp; break; } else if (pot == null) { pot = gp; } } _localGamepad = pot; if (_localGamepad != null) { if (_localGamepad != prev) { try { string name = RawGameController.FromGameController(_localGamepad)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Local gamepad hooked: {name}"); } catch { } } try { _ = _localGamepad.GetCurrentReading(); } catch { _localGamepad = null; } } else { System.Diagnostics.Debug.WriteLine("[Input] No suitable local gamepad detected."); } }
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) { if (args.WindowActivationState == WindowActivationState.Deactivated) { PauseLocalPolling(); reset_navigation_state(); } else { TryHookCorrectGamepad(); ResumeLocalPolling(); } }
    private void Gamepad_GamepadAdded(object? sender, Gamepad e) { try { string name = RawGameController.FromGameController(e)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Gamepad added (WinRT): {name}. Re-evaluating hook."); } catch { } TryHookCorrectGamepad(); ResumeLocalPolling(); }
    private void Gamepad_GamepadRemoved(object? sender, Gamepad e) { try { string name = RawGameController.FromGameController(e)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Gamepad removed (WinRT): {name}. Re-evaluating hook."); } catch { } TryHookCorrectGamepad(); if (_localGamepad == null) PauseLocalPolling(); }
    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListView { SelectedItem: not null } lv) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (lv.SelectedItem != null) lv.ScrollIntoView(lv.SelectedItem); }); } }
    private void MainWindow_Closed(object sender, WindowEventArgs args) { _highlightThrottleTimer?.Stop(); ViewModel.PropertyChanged -= ViewModel_PropertyChanged; PauseLocalPolling(); Gamepad.GamepadAdded -= Gamepad_GamepadAdded; Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved; SentenceWebView?.Close(); ViewModel?.Cleanup(); _localGamepad = null; _gamepadPollingTimer = null; System.Diagnostics.Debug.WriteLine("[MainWindow] Closed event handled."); }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
    private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args) { System.Diagnostics.Debug.WriteLine("[MainWindow] Detected SettingsWindow closed."); if (sender is SettingsWindow closedWindow) { closedWindow.Closed -= SettingsWindow_Instance_Closed; } _settingsWindowInstance = null; TrySetFocusToListOrRoot(); }
    private void TrySetFocusToListOrRoot() { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { System.Diagnostics.Debug.WriteLine($"[Focus] Attempting to set focus after view change/window close..."); bool fs = HistoryListView.Focus(FocusState.Programmatic); if (!fs && this.Content is FrameworkElement root) { fs = root.Focus(FocusState.Programmatic); } System.Diagnostics.Debug.WriteLine($"[Focus] Focus set attempt result: {fs}"); }); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private Task DispatcherQueue_EnqueueAsync(Action function, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal)
    {
        var taskCompletionSource = new TaskCompletionSource();
        if (DispatcherQueue.TryEnqueue(priority, () =>
        {
            try { function(); taskCompletionSource.SetResult(); }
            catch (Exception ex) { taskCompletionSource.SetException(ex); }
        }))
        { }
        else { taskCompletionSource.SetException(new InvalidOperationException("Failed to enqueue action.")); }
        return taskCompletionSource.Task;
    }
    private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal)
    {
        var taskCompletionSource = new TaskCompletionSource();
        if (DispatcherQueue.TryEnqueue(priority, async () =>
        {
            try { await asyncFunction(); taskCompletionSource.SetResult(); }
            catch (Exception ex) { taskCompletionSource.SetException(ex); }
        }))
        { }
        else { taskCompletionSource.SetException(new InvalidOperationException("Failed to enqueue async action.")); }
        return taskCompletionSource.Task;
    }
}