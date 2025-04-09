// MainWindow.xaml.cs (v0.2.2 - Fixes for Windows.Gaming.Input + View State)
using System;
using JpnStudyTool.Services;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Gaming.Input;
using Windows.System;
using WinRT.Interop;

namespace JpnStudyTool;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    private Gamepad? _localGamepad = null;
    private DateTime _lastGamepadNavTime = DateTime.MinValue;
    private readonly TimeSpan _gamepadNavDebounce = TimeSpan.FromMilliseconds(150);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gamepadPollingTimer;

    private WindowActivationService _windowActivationService;

    public MainWindow()
    {
        this.InitializeComponent();
        _windowActivationService = new WindowActivationService();
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        _windowActivationService.RegisterWindowHandle(hwnd);

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

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _gamepadPollingTimer?.Stop();
            System.Diagnostics.Debug.WriteLine("[Input] Local Gamepad polling stopped (Window Deactivated).");
            reset_navigation_state();
        }
        else
        {
            TryHookCorrectGamepad();
            if (_localGamepad != null)
            {
                if (_gamepadPollingTimer != null && !_gamepadPollingTimer.IsRunning)
                {
                    _gamepadPollingTimer.Start();
                    System.Diagnostics.Debug.WriteLine("[Input] Local Gamepad polling started (Window Activated).");
                }
            }
            else { System.Diagnostics.Debug.WriteLine("[Input] No suitable gamepad found on activation, polling not started."); }
        }
    }

    private void SetupGamepadPollingTimer()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq == null) { System.Diagnostics.Debug.WriteLine("[ERROR] Cannot create DispatcherQueueTimer: No DispatcherQueue on this thread."); return; }

        _gamepadPollingTimer = dq.CreateTimer();
        _gamepadPollingTimer.Interval = TimeSpan.FromMilliseconds(50);
        _gamepadPollingTimer.Tick += (s, e) => CheckGamepadLocalInput();
    }


    private void TryHookCorrectGamepad()
    {
        Gamepad? previousGamepad = _localGamepad;
        _localGamepad = null;
        Gamepad? potentialGamepad = null;
        var gamepads = Gamepad.Gamepads;
        System.Diagnostics.Debug.WriteLine($"[Input] Checking Gamepads. Count: {gamepads.Count}");

        foreach (var gp in gamepads)
        {
            string gpName = "Unknown";
            RawGameController? rawController = null;
            try { rawController = RawGameController.FromGameController(gp); } catch { }
            if (rawController != null)
            {
                gpName = rawController.DisplayName;
                System.Diagnostics.Debug.WriteLine($"[Input] Found: {gpName} (VID: {rawController.HardwareVendorId:X4}, PID: {rawController.HardwareProductId:X4})");
            }
            else
            {
                gpName = gp.GetType().Name;
                System.Diagnostics.Debug.WriteLine($"[Input] Found (Generic): {gpName}");
            }
            // Nintendo VID is 0x057E
            bool isLikelyRawProController = rawController != null && rawController.HardwareVendorId == 0x057E;

            if (!isLikelyRawProController)
            {
                potentialGamepad = gp;
                System.Diagnostics.Debug.WriteLine($"[Input] Prioritizing likely virtual/other gamepad: {gpName}");
                break;
            }
            else if (potentialGamepad == null) { potentialGamepad = gp; }
        }

        _localGamepad = potentialGamepad;

        if (_localGamepad != null)
        {
            if (_localGamepad != previousGamepad)
            {
                string finalName = "Unknown"; try { finalName = RawGameController.FromGameController(_localGamepad)?.DisplayName ?? _localGamepad.GetType().Name; } catch { }
                System.Diagnostics.Debug.WriteLine($"[Input] Gamepad hooked: {finalName}");
            }
            try { _ = _localGamepad.GetCurrentReading(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Input] Initial Read FAILED: {ex.Message}"); _localGamepad = null; }
        }
        else { System.Diagnostics.Debug.WriteLine("[Input] No suitable gamepad detected after filtering."); }
    }

    private void Gamepad_GamepadAdded(object? sender, Gamepad e)
    {
        string gpName = "Unknown"; try { gpName = RawGameController.FromGameController(e)?.DisplayName ?? e.GetType().Name; } catch { }
        System.Diagnostics.Debug.WriteLine($"[Input] Gamepad added by system: {gpName}. Re-evaluating hook.");
        TryHookCorrectGamepad();
    }

    private void Gamepad_GamepadRemoved(object? sender, Gamepad e)
    {
        string gpName = "Unknown"; try { gpName = RawGameController.FromGameController(e)?.DisplayName ?? e.GetType().Name; } catch { }
        System.Diagnostics.Debug.WriteLine($"[Input] Gamepad removed by system: {gpName}. Re-evaluating hook.");
        TryHookCorrectGamepad();
    }


    private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool handled = false;
        {
            switch (e.Key)
            {
                case VirtualKey.Up:
                case VirtualKey.GamepadDPadUp:
                    ViewModel.MoveSelection(-1); handled = true; break;
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadDown:
                    ViewModel.MoveSelection(1); handled = true; break;
            }
        }
        e.Handled = handled;
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
            {
                if (reading.Buttons.HasFlag(GamepadButtons.DPadUp) || reading.LeftThumbstickY > 0.7)
                { ViewModel.MoveSelection(-1); inputProcessed = true; }
                else if (reading.Buttons.HasFlag(GamepadButtons.DPadDown) || reading.LeftThumbstickY < -0.7)
                { ViewModel.MoveSelection(1); inputProcessed = true; }
            }

            if (inputProcessed) { _lastGamepadNavTime = now; }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Input] Error reading local gamepad: {ex.Message}"); _localGamepad = null; _gamepadPollingTimer?.Stop(); }
    }

    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem != null)
        { listView.ScrollIntoView(listView.SelectedItem); }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _gamepadPollingTimer?.Stop(); _gamepadPollingTimer = null;
        Gamepad.GamepadAdded -= Gamepad_GamepadAdded;
        Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved;
        _localGamepad = null;
        ViewModel?.Cleanup();
        System.Diagnostics.Debug.WriteLine("[MainWindow] Closed event handled.");
    }

    public void ToggleVisibility()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dq?.TryEnqueue(() => { _windowActivationService?.ToggleMainWindowVisibility(); });
    }

    private void reset_navigation_state() { _lastGamepadNavTime = DateTime.MinValue; }
}