// SettingsWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Gaming.Input;
using Windows.System;

namespace JpnStudyTool;

public sealed partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel
    {
        get;
    }
    private CancellationTokenSource? _captureCts;

    public SettingsWindow()
    {
        this.InitializeComponent();
        ViewModel = new SettingsViewModel();

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.DataContext = ViewModel;
            rootElement.KeyDown += Capture_KeyDown;
        }

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.RequestClose += ViewModel_RequestClose;

        this.Activated += SettingsWindow_Activated;
        this.Closed += SettingsWindow_Closed;

        App.PauseGlobalHotkeys();
    }

    // Start/Stop Joystick polling when ViewModel capture state changes
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsCapturingJoystick))
        {
            if (ViewModel.IsCapturingJoystick) StartJoystickPolling();
            else StopJoystickPolling();
        }
    }

    // Handle keyboard input when capturing
    private void Capture_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.IsCapturingKeyboard)
        {
            e.Handled = true;

            VirtualKey key = e.Key;
            string keyDisplay = key.ToString();

            if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu ||
                key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows)
            {
                ViewModel.CaptureStatusText = $"Press key for '{ViewModel.BindingToCapture?.ActionName}' (Modifiers detected)...";
                return;
            }

            if (key == VirtualKey.Escape)
            {
                ViewModel.CancelCaptureCommand.Execute(null);
                return;
            }

            var modifiers = VirtualKeyModifiers.None;
            try
            {
                var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                if (ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Control;

                var altState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
                if (altState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Menu;

                var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Shift;

                var lWinState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows);
                if (lWinState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Windows;
                var rWinState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows);
                if (rWinState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Windows;

            }
            catch (Exception keyStateEx)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Error getting key state: {keyStateEx.Message}");
            }

            List<string> parts = new List<string>();
            if (modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
            if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
            parts.Add(keyDisplay);

            string fullBindingString = string.Join("+", parts);

            ViewModel.ApplyKeyboardCapture(fullBindingString, e.Key);
        }
        else if (ViewModel.IsCapturingJoystick && e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelCaptureCommand.Execute(null);
        }
    }

    // --- Joystick Polling for Capture ---
    private void StartJoystickPolling()
    {
        StopJoystickPolling();
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        Task.Run(async () =>
        {
            System.Diagnostics.Debug.WriteLine("[Capture] Joystick polling started...");
            Gamepad? gamepad = Gamepad.Gamepads.FirstOrDefault();
            GamepadReading previousReading = default;

            while (!token.IsCancellationRequested && gamepad != null)
            {
                try
                {
                    var currentReading = gamepad.GetCurrentReading();
                    GamepadButtons justPressed = (currentReading.Buttons ^ previousReading.Buttons) & currentReading.Buttons;

                    if (justPressed != GamepadButtons.None)
                    {
                        GamepadButtons buttonToApply = GetFirstPressedButton(justPressed);
                        if (buttonToApply != GamepadButtons.None)
                        {
                            string buttonName = buttonToApply.ToString();
                            System.Diagnostics.Debug.WriteLine($"[Capture] Joystick button detected: {buttonName}");
                            this.DispatcherQueue?.TryEnqueue(() =>
                            {
                                if (ViewModel.IsCapturingJoystick)
                                {
                                    ViewModel.ApplyJoystickCapture(buttonName, buttonToApply);
                                }
                            });
                            break;
                        }
                    }

                    previousReading = currentReading;
                    await Task.Delay(50, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Capture] Error during joystick poll: {ex.Message}");
                    this.DispatcherQueue?.TryEnqueue(() => ViewModel.CancelCaptureCommand.Execute(null));
                    break;
                }
            }
            System.Diagnostics.Debug.WriteLine("[Capture] Joystick polling stopped.");
        }, token);
    }

    private void StopJoystickPolling()
    {
        try
        {
            _captureCts?.Cancel();
            _captureCts?.Dispose();
        }
        catch (ObjectDisposedException) { /* Ignore if already disposed */ }
        _captureCts = null;
    }

    // Helper to get a single pressed button flag
    private GamepadButtons GetFirstPressedButton(GamepadButtons buttons)
    {
        foreach (GamepadButtons flag in (GamepadButtons[])Enum.GetValues(typeof(GamepadButtons)))
        {
            if (flag != GamepadButtons.None && buttons.HasFlag(flag))
            {
                return flag;
            }
        }
        return GamepadButtons.None;
    }

    // Handler to close the window when requested by ViewModel
    private void ViewModel_RequestClose(object? sender, EventArgs e)
    {
        this.Close();
    }

    private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            App.PauseGlobalHotkeys();
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        this.Activated -= SettingsWindow_Activated;
        this.Closed -= SettingsWindow_Closed;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.RequestClose -= ViewModel_RequestClose;

        StopJoystickPolling();
        ViewModel.CleanupOnClose();

        App.ReloadAndRestartGlobalServices(default);

        System.Diagnostics.Debug.WriteLine("[SettingsWindow] Closed and global services reloaded.");
    }
}