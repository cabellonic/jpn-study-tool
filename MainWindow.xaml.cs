using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
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
    private readonly TimeSpan _gamepadNavDebounce = TimeSpan.FromMilliseconds(150);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gamepadPollingTimer;
    private AppSettings _currentSettings = new();
    private readonly Dictionary<VirtualKey, string> _localKeyToActionId = new();
    private readonly Dictionary<GamepadButtons, string> _localButtonToActionId = new();
    private SettingsWindow? _settingsWindowInstance = null;

    private const double DEFAULT_SENTENCE_FONT_SIZE = 24;
    private const double DEFAULT_DEFINITION_FONT_SIZE = 18;
    public double SentenceFontSize => DEFAULT_SENTENCE_FONT_SIZE;
    public double DefinitionFontSize => DEFAULT_DEFINITION_FONT_SIZE;
    public double SentenceLineHeight => SentenceFontSize * 1.5;

    private List<(TextPointer Start, TextPointer End)> _selectableTokenRanges = new();

    public MainWindow()
    {
        this.InitializeComponent();
        _windowActivationService = new WindowActivationService();
        IntPtr hwndPtr = WindowNative.GetWindowHandle(this);
        _windowActivationService.RegisterWindowHandle(hwndPtr);
        ViewModel = new MainWindowViewModel();
        if (this.Content is FrameworkElement rootElement) { rootElement.DataContext = ViewModel; rootElement.PreviewKeyDown += MainWindow_Root_PreviewKeyDown; }
        this.Title = "Jpn Study Tool"; this.Closed += MainWindow_Closed; this.Activated += MainWindow_Activated;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged; UpdateViewVisibility(ViewModel.CurrentViewInternal);
        Gamepad.GamepadAdded += Gamepad_GamepadAdded; Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
        TryHookCorrectGamepad(); SetupGamepadPollingTimer();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CurrentViewInternal)) { DispatcherQueue.TryEnqueue(() => UpdateViewVisibility(ViewModel.CurrentViewInternal)); }
        else if (e.PropertyName == nameof(ViewModel.SelectedTokenIndex)) { DispatcherQueue.TryEnqueue(() => { SelectTokenRange(ViewModel.SelectedTokenIndex); DefinitionScrollViewer?.ChangeView(null, 0, null, true); }); }
        else if (e.PropertyName == nameof(ViewModel.DetailFocusTarget)) { DispatcherQueue.TryEnqueue(UpdateDetailFocusVisuals); }
    }

    private void UpdateViewVisibility(string? currentView)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateViewVisibility: State={currentView ?? "null"}");
        switch (currentView)
        {
            case "list": ListViewContentGrid.Visibility = Visibility.Visible; DetailViewContentGrid.Visibility = Visibility.Collapsed; TrySetFocusToListOrRoot(); break;
            case "detail": ListViewContentGrid.Visibility = Visibility.Collapsed; DetailViewContentGrid.Visibility = Visibility.Visible; BuildRichTextBlockContent(); break;
            default: ListViewContentGrid.Visibility = Visibility.Visible; DetailViewContentGrid.Visibility = Visibility.Collapsed; break;
        }
    }

    private void BuildRichTextBlockContent()
    {
        SentenceRichTextBlock.Blocks.Clear(); _selectableTokenRanges.Clear();
        if (ViewModel.Tokens == null || !ViewModel.Tokens.Any()) return;
        var paragraph = new Paragraph { LineHeight = this.SentenceLineHeight, FontSize = this.SentenceFontSize };
        var tempRuns = new List<Run>();
        for (int i = 0; i < ViewModel.Tokens.Count; i++) { var token = ViewModel.Tokens[i]; var run = new Run { Text = token.Surface }; paragraph.Inlines.Add(run); tempRuns.Add(run); }
        SentenceRichTextBlock.Blocks.Add(paragraph);
        System.Diagnostics.Debug.WriteLine($"[MainWindow] Built RichTextBlock with {paragraph.Inlines.Count} runs.");
        SentenceRichTextBlock.Select(SentenceRichTextBlock.ContentStart, SentenceRichTextBlock.ContentStart);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => PopulateSelectableTokenRangesAndSelectInitial(tempRuns));
    }

    private void PopulateSelectableTokenRangesAndSelectInitial(List<Run> runsInParagraph)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] PopulateSelectableTokenRanges: Getting pointers for {runsInParagraph.Count} runs...");
        _selectableTokenRanges.Clear();
        if (ViewModel.Tokens == null || runsInParagraph.Count != ViewModel.Tokens.Count) { System.Diagnostics.Debug.WriteLine($"[MainWindow] Mismatch or null Tokens/Runs during TextPointer retrieval."); return; }
        for (int i = 0; i < ViewModel.Tokens.Count; i++) { if (ViewModel.Tokens[i].IsSelectable) { Run? run = runsInParagraph[i]; if (run != null) { try { _selectableTokenRanges.Add((run.ContentStart, run.ContentEnd)); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] Error getting TextPointer for run {i}: {ex.Message}"); } } } }
        System.Diagnostics.Debug.WriteLine($"[MainWindow] Finished getting TextPointers. Selectable ranges count: {_selectableTokenRanges.Count}");
        if (ViewModel.CurrentViewInternal == "detail") { SelectTokenRange(ViewModel.SelectedTokenIndex); UpdateDetailFocusVisuals(); }
    }

    private void SelectTokenRange(int selectedSelectableIndex)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] SelectTokenRange: Selecting index {selectedSelectableIndex}");
        if (selectedSelectableIndex < 0 || selectedSelectableIndex >= _selectableTokenRanges.Count) { SentenceRichTextBlock.Select(SentenceRichTextBlock.ContentStart, SentenceRichTextBlock.ContentStart); System.Diagnostics.Debug.WriteLine($"[MainWindow] Invalid selectable index {selectedSelectableIndex}. Selection cleared."); return; }
        var (start, end) = _selectableTokenRanges[selectedSelectableIndex];
        try { SentenceRichTextBlock.Select(start, end); System.Diagnostics.Debug.WriteLine($"[MainWindow] Selected text range index {selectedSelectableIndex}."); ScrollRunIntoView(start); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] Error selecting text range: {ex.Message}"); }
    }

    private void ScrollRunIntoView(TextPointer pointer)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (SentenceRichTextBlock == null || SentenceScrollViewer == null || pointer == null) return;
            try
            {
                Rect runRect = pointer.GetCharacterRect(LogicalDirection.Forward); if (runRect == Rect.Empty) return;
                var transform = SentenceRichTextBlock.TransformToVisual(SentenceScrollViewer); Point transformedTopLeft = transform.TransformPoint(new Point(runRect.X, runRect.Y));
                double viewportHeight = SentenceScrollViewer.ViewportHeight; double currentOffset = SentenceScrollViewer.VerticalOffset; double targetOffset = currentOffset;
                double runTop = transformedTopLeft.Y; double runBottom = runTop + runRect.Height;
                if (runTop < 0) { targetOffset = currentOffset + runTop - 10; } else if (runBottom > viewportHeight) { targetOffset = currentOffset + (runBottom - viewportHeight) + 10; }
                targetOffset = Math.Max(0, targetOffset);
                if (Math.Abs(targetOffset - currentOffset) > 1) { SentenceScrollViewer.ChangeView(null, targetOffset, null, false); System.Diagnostics.Debug.WriteLine($"[Scroll] Scrolled SentenceScrollViewer VERTICALLY to offset {targetOffset}."); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Scroll] Error scrolling Run into view: {ex.Message}"); }
        });
    }

    private void UpdateDetailFocusVisuals()
    {
        if (ViewModel.CurrentViewInternal != "detail") return;
        if (ViewModel.DetailFocusTarget == "Definition")
        {
            if (!DefinitionScrollViewer.Focus(FocusState.Programmatic)) { DefinitionTextBlock.Focus(FocusState.Programmatic); }
            System.Diagnostics.Debug.WriteLine("[Focus] Set focus to Definition area.");
        }
        else
        {
            if (!SentenceRichTextBlock.Focus(FocusState.Programmatic)) { SentenceScrollViewer.Focus(FocusState.Programmatic); }
            System.Diagnostics.Debug.WriteLine("[Focus] Set focus to Sentence area.");
        }
    }

    private void ScrollDefinition(double scrollDelta) { if (ViewModel.CurrentViewInternal == "detail" && ViewModel.DetailFocusTarget == "Definition") { double newOffset = DefinitionScrollViewer.VerticalOffset + scrollDelta; DefinitionScrollViewer.ChangeView(null, newOffset, null, false); } }
    public void UpdateLocalBindings(AppSettings settings) { System.Diagnostics.Debug.WriteLine("[MainWindow] === Updating local bindings START ==="); _currentSettings = settings ?? new AppSettings(); _localKeyToActionId.Clear(); _localButtonToActionId.Clear(); if (_currentSettings.LocalKeyboardBindings != null) { foreach (var kvp in _currentSettings.LocalKeyboardBindings) { if (!string.IsNullOrEmpty(kvp.Value) && Enum.TryParse<VirtualKey>(kvp.Value, true, out var vk)) { _localKeyToActionId[vk] = kvp.Key; } } } if (_currentSettings.LocalJoystickBindings != null) { foreach (var kvp in _currentSettings.LocalJoystickBindings) { string actionId = kvp.Key; string? buttonCode = kvp.Value; if (!string.IsNullOrEmpty(buttonCode)) { GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode); if (buttonEnum.HasValue) { _localButtonToActionId[buttonEnum.Value] = actionId; } } } } System.Diagnostics.Debug.WriteLine("[MainWindow] === Finished updating local bindings ==="); reset_navigation_state(); }
    public void ToggleVisibility() => DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
    public void TriggerMenuToggle() => DispatcherQueue?.TryEnqueue(() => { if (ViewModel.CurrentViewInternal != "menu") ViewModel.EnterMenu(); else ViewModel.ExitMenu(); });
    public void ShowSettingsWindow() { if (_settingsWindowInstance == null) { _settingsWindowInstance = new SettingsWindow(); _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed; _settingsWindowInstance.Activate(); } else { _settingsWindowInstance.Activate(); } }
    public void PauseLocalPolling() { _gamepadPollingTimer?.Stop(); System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling PAUSED explicitly."); }
    public void ResumeLocalPolling() { DispatcherQueue?.TryEnqueue(() => { bool isActive = false; try { HWND fWnd = PInvoke.GetForegroundWindow(); HWND myWnd = (HWND)WindowNative.GetWindowHandle(this); isActive = fWnd == myWnd; } catch { isActive = false; } if (isActive && _localGamepad != null && _gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning) { _gamepadPollingTimer.Start(); System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling RESUMED explicitly."); } }); }
    private void MainWindow_Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e) { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Key={e.Key}, Handled={e.Handled}"); if (e.Handled) return; if (_localKeyToActionId.TryGetValue(e.Key, out string? actionId) && actionId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Mapped {e.Key} to Action: {actionId}"); ExecuteLocalAction(actionId); e.Handled = true; } else { bool lvFocus = false; XamlRoot? xr = (this.Content as UIElement)?.XamlRoot; if (xr != null) { var fe = FocusManager.GetFocusedElement(xr); lvFocus = fe is ListView || fe is ListViewItem; } if (e.Key == VirtualKey.Enter && lvFocus) { System.Diagnostics.Debug.WriteLine($"[Input] Root PreviewKeyDown: Enter on ListView(Item). Attempting DETAIL_ENTER."); ExecuteLocalAction("DETAIL_ENTER"); e.Handled = true; } else { e.Handled = false; } } }
    private void CheckGamepadLocalInput() { if (_localGamepad == null) return; var now = DateTime.UtcNow; if (now - _lastGamepadNavTime < _gamepadNavDebounce) return; try { GamepadReading reading = _localGamepad.GetCurrentReading(); bool processed = false; const double triggerThr = 0.75; const double deadzone = 0.7; foreach (var kvp in _localButtonToActionId) { GamepadButtons button = kvp.Key; string actionId = kvp.Value; bool hasFlag = reading.Buttons.HasFlag(button); if (hasFlag) { System.Diagnostics.Debug.WriteLine($"[Input] Local Button: {button} TRIGGERED Action: {actionId}"); ExecuteLocalAction(actionId); processed = true; break; } } if (!processed) { string? triggerId = null; string? ltAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZL").Key; string? rtAct = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZR").Key; if (ltAct != null && reading.LeftTrigger > triggerThr) triggerId = ltAct; else if (rtAct != null && reading.RightTrigger > triggerThr) triggerId = rtAct; if (triggerId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Local Trigger mapped to Action: {triggerId}"); ExecuteLocalAction(triggerId); processed = true; } } if (!processed) { string? axisId = null; double ly = reading.LeftThumbstickY; double lx = reading.LeftThumbstickX; if (ly > deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_UP"); else if (ly < -deadzone) axisId = FindActionForAxis("STICK_LEFT_Y_DOWN"); else if (lx < -deadzone) axisId = FindActionForAxis("STICK_LEFT_X_LEFT"); else if (lx > deadzone) axisId = FindActionForAxis("STICK_LEFT_X_RIGHT"); if (axisId != null) { System.Diagnostics.Debug.WriteLine($"[Input] Local Axis mapped to Action: {axisId}"); ExecuteLocalAction(axisId); processed = true; } } if (processed) { _lastGamepadNavTime = now; } } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Input] Error reading local gamepad: {ex.Message}"); _localGamepad = null; _gamepadPollingTimer?.Stop(); } }
    private void ExecuteLocalAction(string actionId) { System.Diagnostics.Debug.WriteLine($"[Action] Attempting Action: '{actionId}' in View: '{ViewModel.CurrentViewInternal}', Focus: '{ViewModel.DetailFocusTarget}'"); bool handled = false; if (ViewModel.CurrentViewInternal == "list") { switch (actionId) { case "NAV_UP": ViewModel.MoveSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveSelection(1); handled = true; break; case "DETAIL_ENTER": if (ViewModel.SelectedSentenceIndex >= 0 && ViewModel.SelectedSentenceIndex < ViewModel.SentenceHistory.Count) { ViewModel.EnterDetailView(); handled = true; } break; case "DELETE_WORD": handled = true; break; } } else if (ViewModel.CurrentViewInternal == "detail") { if (ViewModel.DetailFocusTarget == "Sentence") { switch (actionId) { case "NAV_LEFT": ViewModel.MoveTokenSelection(-1); handled = true; break; case "NAV_RIGHT": ViewModel.MoveTokenSelection(1); handled = true; break; case "NAV_UP": ViewModel.MoveKanjiTokenSelection(-1); handled = true; break; case "NAV_DOWN": ViewModel.MoveKanjiTokenSelection(1); handled = true; break; case "DETAIL_ENTER": ViewModel.FocusDefinitionArea(); handled = true; break; case "DETAIL_BACK": ViewModel.ExitDetailView(); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TrySetFocusToListOrRoot); handled = true; break; case "SAVE_WORD": handled = true; break; } } else if (ViewModel.DetailFocusTarget == "Definition") { switch (actionId) { case "NAV_UP": ScrollDefinition(-80); handled = true; break; case "NAV_DOWN": ScrollDefinition(80); handled = true; break; case "DETAIL_ENTER": ViewModel.FocusSentenceArea(); handled = true; break; case "DETAIL_BACK": ViewModel.FocusSentenceArea(); handled = true; break; } } } if (!handled) { System.Diagnostics.Debug.WriteLine($"[Action] Action '{actionId}' ignored or unknown."); } if (handled && ViewModel.CurrentViewInternal == "list" && actionId.StartsWith("NAV_") && ViewModel.SelectedSentenceItem != null) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (ViewModel.SelectedSentenceItem != null && HistoryListView != null) HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem); }); } }
    private void reset_navigation_state() => _lastGamepadNavTime = DateTime.MinValue;
    private GamepadButtons? MapInternalCodeToGamepadButton(string code) => code.ToUpperInvariant() switch { "BTN_A" => GamepadButtons.A, "BTN_B" => GamepadButtons.B, "BTN_X" => GamepadButtons.X, "BTN_Y" => GamepadButtons.Y, "BTN_TL" => GamepadButtons.LeftShoulder, "BTN_TR" => GamepadButtons.RightShoulder, "BTN_SELECT" => GamepadButtons.View, "BTN_START" => GamepadButtons.Menu, "BTN_THUMBL" => GamepadButtons.LeftThumbstick, "BTN_THUMBR" => GamepadButtons.RightThumbstick, "DPAD_UP" => GamepadButtons.DPadUp, "DPAD_DOWN" => GamepadButtons.DPadDown, "DPAD_LEFT" => GamepadButtons.DPadLeft, "DPAD_RIGHT" => GamepadButtons.DPadRight, _ => null };
    private string? FindActionForAxis(string axis) => _currentSettings.LocalJoystickBindings switch { null => null, var b => axis switch { "STICK_LEFT_Y_UP" => b.ContainsValue("NAV_UP") ? "NAV_UP" : null, "STICK_LEFT_Y_DOWN" => b.ContainsValue("NAV_DOWN") ? "NAV_DOWN" : null, "STICK_LEFT_X_LEFT" => b.ContainsValue("NAV_LEFT") ? "NAV_LEFT" : null, "STICK_LEFT_X_RIGHT" => b.ContainsValue("NAV_RIGHT") ? "NAV_RIGHT" : null, _ => null } };
    private void SetupGamepadPollingTimer() { var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); if (dq == null) return; _gamepadPollingTimer = dq.CreateTimer(); _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50); _gamepadPollingTimer.Tick += (s, e) => CheckGamepadLocalInput(); }
    private void TryHookCorrectGamepad() { Gamepad? prev = _localGamepad; _localGamepad = null; Gamepad? pot = null; var gamepads = Gamepad.Gamepads; foreach (var gp in gamepads) { RawGameController? raw = null; try { raw = RawGameController.FromGameController(gp); } catch { } bool isRawPro = raw != null && raw.HardwareVendorId == 0x057E; if (!isRawPro) { pot = gp; break; } else if (pot == null) { pot = gp; } } _localGamepad = pot; if (_localGamepad != null) { if (_localGamepad != prev) { try { string name = RawGameController.FromGameController(_localGamepad)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Local gamepad hooked: {name}"); } catch { } } try { _ = _localGamepad.GetCurrentReading(); } catch { _localGamepad = null; } } else { System.Diagnostics.Debug.WriteLine("[Input] No suitable local gamepad detected."); } }
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) { if (args.WindowActivationState == WindowActivationState.Deactivated) { PauseLocalPolling(); reset_navigation_state(); } else { TryHookCorrectGamepad(); ResumeLocalPolling(); } }
    private void Gamepad_GamepadAdded(object? sender, Gamepad e) { try { string name = RawGameController.FromGameController(e)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Gamepad added (WinRT): {name}. Re-evaluating hook."); } catch { } TryHookCorrectGamepad(); ResumeLocalPolling(); }
    private void Gamepad_GamepadRemoved(object? sender, Gamepad e) { try { string name = RawGameController.FromGameController(e)?.DisplayName ?? "?"; System.Diagnostics.Debug.WriteLine($"[Input] Gamepad removed (WinRT): {name}. Re-evaluating hook."); } catch { } TryHookCorrectGamepad(); if (_localGamepad == null) PauseLocalPolling(); }
    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListView { SelectedItem: not null } lv) { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (lv.SelectedItem != null) lv.ScrollIntoView(lv.SelectedItem); }); } }
    private void MainWindow_Closed(object sender, WindowEventArgs args) { ViewModel.PropertyChanged -= ViewModel_PropertyChanged; PauseLocalPolling(); Gamepad.GamepadAdded -= Gamepad_GamepadAdded; Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved; ViewModel?.Cleanup(); _localGamepad = null; _gamepadPollingTimer = null; System.Diagnostics.Debug.WriteLine("[MainWindow] Closed event handled."); }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
    private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args) { System.Diagnostics.Debug.WriteLine("[MainWindow] Detected SettingsWindow closed."); if (sender is SettingsWindow closedWindow) { closedWindow.Closed -= SettingsWindow_Instance_Closed; } _settingsWindowInstance = null; TrySetFocusToListOrRoot(); }
    private void TrySetFocusToListOrRoot() { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { System.Diagnostics.Debug.WriteLine($"[Focus] Attempting to set focus after view change/window close..."); bool fs = HistoryListView.Focus(FocusState.Programmatic); if (!fs && this.Content is FrameworkElement root) { fs = root.Focus(FocusState.Programmatic); } System.Diagnostics.Debug.WriteLine($"[Focus] Focus set attempt result: {fs}"); }); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}