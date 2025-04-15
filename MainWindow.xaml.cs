// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq; // Required for FirstOrDefault, ContainsValue
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Gaming.Input;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace JpnStudyTool;

public sealed partial class MainWindow : Window
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

    public MainWindow()
    {
        this.InitializeComponent();

        _windowActivationService = new WindowActivationService();
        IntPtr hwndPtr = WindowNative.GetWindowHandle(this);
        _windowActivationService.RegisterWindowHandle(hwndPtr);

        ViewModel = new MainWindowViewModel();
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.DataContext = ViewModel;
            rootElement.KeyDown += MainWindow_KeyDown;
        }

        this.Title = "Jpn Study Tool";
        this.Closed += MainWindow_Closed;
        this.Activated += MainWindow_Activated;

        Gamepad.GamepadAdded += Gamepad_GamepadAdded;
        Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
        TryHookCorrectGamepad();
        SetupGamepadPollingTimer();
    }

    public void UpdateLocalBindings(AppSettings settings)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] === Updating local bindings START ===");
        _currentSettings = settings ?? new AppSettings();

        _localKeyToActionId.Clear();
        _localButtonToActionId.Clear();

        System.Diagnostics.Debug.WriteLine("[MainWindow] Parsing Keyboard Bindings...");
        if (_currentSettings.LocalKeyboardBindings != null)
        {
            foreach (var kvp in _currentSettings.LocalKeyboardBindings)
            {
                if (!string.IsNullOrEmpty(kvp.Value) && Enum.TryParse<VirtualKey>(kvp.Value, true, out var vk))
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Attempting to map Key {vk} to Action {kvp.Key}");
                    _localKeyToActionId[vk] = kvp.Key;
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Successfully mapped Key {vk} to Action {kvp.Key}");
                }
                else { System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Failed to parse local key binding: '{kvp.Value}' for action '{kvp.Key}'"); }
            }
        }
        else { System.Diagnostics.Debug.WriteLine("[MainWindow] LocalKeyboardBindings dictionary is null."); }
        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Final _localKeyToActionId Count = {_localKeyToActionId.Count}");
        foreach (var entry in _localKeyToActionId) { System.Diagnostics.Debug.WriteLine($"  - Key: {entry.Key} -> Action: {entry.Value}"); }


        System.Diagnostics.Debug.WriteLine("[MainWindow] Parsing Joystick Button Bindings...");
        if (_currentSettings.LocalJoystickBindings != null)
        {
            foreach (var kvp in _currentSettings.LocalJoystickBindings)
            {
                string actionId = kvp.Key;
                string? buttonCode = kvp.Value;
                if (!string.IsNullOrEmpty(buttonCode))
                {
                    GamepadButtons? buttonEnum = MapInternalCodeToGamepadButton(buttonCode);
                    if (buttonEnum.HasValue)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Attempting to map Button {buttonEnum.Value} (from code '{buttonCode}') to Action {actionId}");
                        _localButtonToActionId[buttonEnum.Value] = actionId;
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Successfully mapped Button {buttonEnum.Value} to Action {actionId}");
                    }
                    else { System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Failed map internal code '{buttonCode}' for action '{actionId}'"); }
                }
            }
        }
        else { System.Diagnostics.Debug.WriteLine("[MainWindow] LocalJoystickBindings dictionary is null."); }
        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateBindings: Final _localButtonToActionId Count = {_localButtonToActionId.Count}");
        foreach (var entry in _localButtonToActionId) { System.Diagnostics.Debug.WriteLine($"  - Button: {entry.Key} -> Action: {entry.Value}"); }


        System.Diagnostics.Debug.WriteLine("[MainWindow] === Finished updating local bindings ===");
        reset_navigation_state();
    }

    public void ToggleVisibility()
    {
        DispatcherQueue?.TryEnqueue(() => _windowActivationService?.ToggleMainWindowVisibility());
    }

    public void TriggerMenuToggle()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] TriggerMenuToggle called.");
            if (ViewModel.CurrentViewInternal != "menu") ViewModel.EnterMenu();
            else ViewModel.ExitMenu();
        });
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindowInstance == null)
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] Creating new SettingsWindow instance.");
            _settingsWindowInstance = new SettingsWindow();
            _settingsWindowInstance.Closed += SettingsWindow_Instance_Closed;
            _settingsWindowInstance.Activate();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] SettingsWindow already exists. Activating.");
            _settingsWindowInstance.Activate();
        }
    }

    public void PauseLocalPolling()
    {
        _gamepadPollingTimer?.Stop();
        System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling PAUSED explicitly.");
    }

    public void ResumeLocalPolling()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            bool isActive = false;
            try
            {
                HWND foregroundWnd = PInvoke.GetForegroundWindow();
                HWND myWnd = (HWND)WindowNative.GetWindowHandle(this);
                isActive = foregroundWnd == myWnd;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] Error checking foreground window: {ex.Message}. Assuming inactive."); isActive = false; }

            if (isActive && _localGamepad != null && _gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning)
            {
                _gamepadPollingTimer.Start();
                System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling RESUMED explicitly.");
            }
            else if (!isActive) { System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling NOT resumed (Window inactive)."); }
            else if (_localGamepad == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling NOT resumed (No local gamepad)."); }
            else if (_gamepadPollingTimer == null) { System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling NOT resumed (Timer is null)."); }
            else if (_gamepadPollingTimer.IsRunning) { System.Diagnostics.Debug.WriteLine("[MainWindow] Local polling NOT resumed (Already running)."); }
        });
    }

    private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_localKeyToActionId.TryGetValue(e.Key, out string? actionId) && actionId != null)
        {
            System.Diagnostics.Debug.WriteLine($"[Input] Local KeyDown: {e.Key} mapped to Action: {actionId}");
            ExecuteLocalAction(actionId);
            e.Handled = true;
        }
        else
        {
            e.Handled = false;
        }
    }

    private void CheckGamepadLocalInput()
    {
        if (_localGamepad == null) return;

        var now = DateTime.UtcNow;
        if (now - _lastGamepadNavTime < _gamepadNavDebounce) return;

        try
        {
            GamepadReading reading = _localGamepad.GetCurrentReading();
            bool inputProcessed = false;
            const double triggerThreshold = 0.75;
            const double deadzone = 0.7;

            foreach (var kvp in _localButtonToActionId)
            {
                GamepadButtons button = kvp.Key;
                string actionId = kvp.Value;
                bool hasFlag = reading.Buttons.HasFlag(button);

                if (hasFlag) System.Diagnostics.Debug.WriteLine($"[Input] Checking Button: {button} (For Action: {actionId}) -> HasFlag = {hasFlag}");

                if (hasFlag)
                {
                    System.Diagnostics.Debug.WriteLine($"[Input] Local Button: {button} TRIGGERED Action: {actionId}");
                    ExecuteLocalAction(actionId);
                    inputProcessed = true;
                    break;
                }
            }

            if (!inputProcessed)
            {
                string? triggerActionId = null;
                string? leftTriggerAction = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZL").Key;
                string? rightTriggerAction = _currentSettings.LocalJoystickBindings?.FirstOrDefault(kvp => kvp.Value == "BTN_ZR").Key;

                if (leftTriggerAction != null && reading.LeftTrigger > triggerThreshold) triggerActionId = leftTriggerAction;
                else if (rightTriggerAction != null && reading.RightTrigger > triggerThreshold) triggerActionId = rightTriggerAction;

                if (triggerActionId != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Input] Local Trigger mapped to Action: {triggerActionId}");
                    ExecuteLocalAction(triggerActionId);
                    inputProcessed = true;
                }
            }

            if (!inputProcessed)
            {
                string? axisActionId = null;
                double leftY = reading.LeftThumbstickY;
                double leftX = reading.LeftThumbstickX;

                if (leftY > deadzone) axisActionId = FindActionForAxis("STICK_LEFT_Y_UP");
                else if (leftY < -deadzone) axisActionId = FindActionForAxis("STICK_LEFT_Y_DOWN");
                else if (leftX < -deadzone) axisActionId = FindActionForAxis("STICK_LEFT_X_LEFT");
                else if (leftX > deadzone) axisActionId = FindActionForAxis("STICK_LEFT_X_RIGHT");

                if (axisActionId != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Input] Local Axis mapped to Action: {axisActionId}");
                    ExecuteLocalAction(axisActionId);
                    inputProcessed = true;
                }
            }

            if (inputProcessed) { _lastGamepadNavTime = now; }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Input] Error reading local gamepad: {ex.Message}");
            _localGamepad = null;
            _gamepadPollingTimer?.Stop();
        }
    }

    private void ExecuteLocalAction(string actionId)
    {
        switch (actionId)
        {
            case "NAV_UP": ViewModel.MoveSelection(-1); break;
            case "NAV_DOWN": ViewModel.MoveSelection(1); break;
            case "NAV_LEFT": System.Diagnostics.Debug.WriteLine($"[Action] '{actionId}' triggered (Not Implemented Yet)"); break;
            case "NAV_RIGHT": System.Diagnostics.Debug.WriteLine($"[Action] '{actionId}' triggered (Not Implemented Yet)"); break;
            case "DETAIL_ENTER": System.Diagnostics.Debug.WriteLine($"[Action] '{actionId}' triggered (Not Implemented Yet)"); break;
            case "DETAIL_BACK": System.Diagnostics.Debug.WriteLine($"[Action] '{actionId}' triggered (Not Implemented Yet)"); break;
            case "SAVE_WORD": System.Diagnostics.Debug.WriteLine($"[Action] '{actionId}' triggered (Not Implemented Yet)"); break;
            case "DELETE_WORD": System.Diagnostics.Debug.WriteLine($"[Action] '{actionId}' triggered (Not Implemented Yet)"); break;
            default: System.Diagnostics.Debug.WriteLine($"[Action] Unknown local action ID: {actionId}"); break;
        }
        if (actionId.StartsWith("NAV_") && ViewModel.SelectedSentenceItem != null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (ViewModel.SelectedSentenceItem != null && HistoryListView != null)
                    HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem);
            });
        }
    }

    private void reset_navigation_state()
    {
        _lastGamepadNavTime = DateTime.MinValue;
    }

    private GamepadButtons? MapInternalCodeToGamepadButton(string internalCode)
    {
        return internalCode.ToUpperInvariant() switch
        {
            "BTN_A" => GamepadButtons.A,
            "BTN_B" => GamepadButtons.B,
            "BTN_X" => GamepadButtons.X,
            "BTN_Y" => GamepadButtons.Y,
            "BTN_TL" => GamepadButtons.LeftShoulder,
            "BTN_TR" => GamepadButtons.RightShoulder,
            "BTN_SELECT" => GamepadButtons.View,
            "BTN_START" => GamepadButtons.Menu,
            "BTN_THUMBL" => GamepadButtons.LeftThumbstick,
            "BTN_THUMBR" => GamepadButtons.RightThumbstick,
            "DPAD_UP" => GamepadButtons.DPadUp,
            "DPAD_DOWN" => GamepadButtons.DPadDown,
            "DPAD_LEFT" => GamepadButtons.DPadLeft,
            "DPAD_RIGHT" => GamepadButtons.DPadRight,
            _ => null // Return null for unmappable codes (like BTN_ZL/ZR)
        };
    }

    private string? FindActionForAxis(string axisCode)
    {
        if (_currentSettings.LocalJoystickBindings == null) return null;
        switch (axisCode)
        {
            case "STICK_LEFT_Y_UP": return _currentSettings.LocalJoystickBindings.ContainsValue("NAV_UP") ? "NAV_UP" : null;
            case "STICK_LEFT_Y_DOWN": return _currentSettings.LocalJoystickBindings.ContainsValue("NAV_DOWN") ? "NAV_DOWN" : null;
            case "STICK_LEFT_X_LEFT": return _currentSettings.LocalJoystickBindings.ContainsValue("NAV_LEFT") ? "NAV_LEFT" : null;
            case "STICK_LEFT_X_RIGHT": return _currentSettings.LocalJoystickBindings.ContainsValue("NAV_RIGHT") ? "NAV_RIGHT" : null;
            default: return null;
        }
    }

    private void SetupGamepadPollingTimer()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq == null) { System.Diagnostics.Debug.WriteLine("[ERROR] Cannot create DispatcherQueueTimer: No DispatcherQueue."); return; }
        _gamepadPollingTimer = dq.CreateTimer();
        _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50);
        _gamepadPollingTimer.Tick += (s, e) => CheckGamepadLocalInput();
    }

    private void TryHookCorrectGamepad()
    {
        Gamepad? previousGamepad = _localGamepad; _localGamepad = null; Gamepad? potentialGamepad = null;
        var gamepads = Gamepad.Gamepads; System.Diagnostics.Debug.WriteLine($"[Input] Checking Gamepads (WinRT). Count: {gamepads.Count}");
        foreach (var gp in gamepads)
        {
            string gpName = "Unknown"; RawGameController? rawController = null;
            try { rawController = RawGameController.FromGameController(gp); } catch { }
            if (rawController != null) { gpName = rawController.DisplayName; System.Diagnostics.Debug.WriteLine($"[Input] Found (WinRT): {gpName} (VID: {rawController.HardwareVendorId:X4}, PID: {rawController.HardwareProductId:X4})"); }
            else { gpName = gp.GetType().Name; System.Diagnostics.Debug.WriteLine($"[Input] Found (WinRT Generic): {gpName}"); }
            bool isLikelyRawProController = rawController != null && rawController.HardwareVendorId == 0x057E;
            if (!isLikelyRawProController) { potentialGamepad = gp; System.Diagnostics.Debug.WriteLine($"[Input] Prioritizing likely virtual/other gamepad: {gpName}"); break; }
            else if (potentialGamepad == null) { potentialGamepad = gp; }
        }
        _localGamepad = potentialGamepad;
        if (_localGamepad != null)
        {
            if (_localGamepad != previousGamepad) { string finalName = "Unknown"; try { finalName = RawGameController.FromGameController(_localGamepad)?.DisplayName ?? _localGamepad.GetType().Name; } catch { } System.Diagnostics.Debug.WriteLine($"[Input] Local gamepad hooked: {finalName}"); }
            try { _ = _localGamepad.GetCurrentReading(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Input] Initial Read FAILED: {ex.Message}"); _localGamepad = null; } // Check if readable
        }
        else { System.Diagnostics.Debug.WriteLine("[Input] No suitable local gamepad detected after filtering."); }
    }


    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            PauseLocalPolling();
            reset_navigation_state();
        }
        else
        {
            TryHookCorrectGamepad();
            ResumeLocalPolling();
        }
    }

    private void Gamepad_GamepadAdded(object? sender, Gamepad e)
    {
        string gpName = "Unknown"; try { gpName = RawGameController.FromGameController(e)?.DisplayName ?? e.GetType().Name; } catch { }
        System.Diagnostics.Debug.WriteLine($"[Input] Gamepad added (WinRT): {gpName}. Re-evaluating hook.");
        TryHookCorrectGamepad();
        ResumeLocalPolling();
    }

    private void Gamepad_GamepadRemoved(object? sender, Gamepad e)
    {
        string gpName = "Unknown"; try { gpName = RawGameController.FromGameController(e)?.DisplayName ?? e.GetType().Name; } catch { }
        System.Diagnostics.Debug.WriteLine($"[Input] Gamepad removed (WinRT): {gpName}. Re-evaluating hook.");
        TryHookCorrectGamepad();
        if (_localGamepad == null) PauseLocalPolling();
    }

    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: not null } listView)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (listView.SelectedItem != null && HistoryListView != null)
                    HistoryListView.ScrollIntoView(ViewModel.SelectedSentenceItem);
            });
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        PauseLocalPolling();
        Gamepad.GamepadAdded -= Gamepad_GamepadAdded;
        Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved;
        ViewModel?.Cleanup();
        _localGamepad = null;
        _gamepadPollingTimer = null;
        System.Diagnostics.Debug.WriteLine("[MainWindow] Closed event handled.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void SettingsWindow_Instance_Closed(object sender, WindowEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] Detected SettingsWindow closed. Clearing instance reference.");
        if (sender is SettingsWindow closedWindow)
        {
            closedWindow.Closed -= SettingsWindow_Instance_Closed;
        }
        _settingsWindowInstance = null;
    }
}