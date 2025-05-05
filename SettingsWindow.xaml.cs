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
    public SettingsViewModel ViewModel { get; }
    private CancellationTokenSource? _captureCts;

    public SettingsWindow()
    {
        this.InitializeComponent();
        ViewModel = new SettingsViewModel();

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.DataContext = ViewModel;
            rootElement.PreviewKeyDown += SettingsRoot_PreviewKeyDown;
        }

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.RequestClose += ViewModel_RequestClose;

        this.Activated += SettingsWindow_Activated;
        this.Closed += SettingsWindow_Closed;

        App.PauseGlobalHotkeys();
    }

    private void SettingsRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.IsCapturingKeyboard || ViewModel.IsCapturingJoystick)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow PreviewKeyDown] Capturing Active. Key={e.Key}. Setting Handled=True.");
            e.Handled = true;

            if (ViewModel.IsCapturingKeyboard)
            {
                ProcessKeyboardCapture(e.Key);
            }
            else if (ViewModel.IsCapturingJoystick && e.Key == VirtualKey.Escape)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsWindow PreviewKeyDown] Escape pressed during Joy capture.");
                ViewModel.CancelCaptureCommand.Execute(null);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow PreviewKeyDown] Escape pressed outside capture mode. Closing window.");
            this.Close();
            e.Handled = true;
        }
    }

    private void ProcessKeyboardCapture(VirtualKey key)
    {
        System.Diagnostics.Debug.WriteLine($"[SettingsWindow ProcessKeyboardCapture] Key={key}");

        if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu || key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows || key == VirtualKey.LeftControl || key == VirtualKey.RightControl || key == VirtualKey.LeftShift || key == VirtualKey.RightShift || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu)
        {
            ViewModel.CaptureStatusText = $"Press the main key for '{ViewModel.BindingToCapture?.ActionName}' (Modifiers detected)...";
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow ProcessKeyboardCapture] Modifier key only ({key}). Waiting for main key.");
            return;
        }

        if (key == VirtualKey.Escape)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow ProcessKeyboardCapture] Escape pressed during keyboard capture. Cancelling.");
            ViewModel.CancelCaptureCommand.Execute(null);
            return;
        }

        VirtualKeyModifiers activeModifiers = VirtualKeyModifiers.None;
        try
        {
            var keyState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            if (keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Control;

            keyState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu); // Alt
            if (keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Menu;

            keyState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if (keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Shift;

            var winStateL = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows);
            var winStateR = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows);
            if (winStateL.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) || winStateR.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Windows;
        }
        catch (Exception keyStateEx)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow ProcessKeyboardCapture] Warning: Error getting key state: {keyStateEx.Message}. Modifiers might be inaccurate.");
        }

        List<string> parts = new List<string>();
        if (activeModifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (activeModifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (activeModifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        if (activeModifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        string fullBindingString = string.Join("+", parts);

        System.Diagnostics.Debug.WriteLine($"[SettingsWindow ProcessKeyboardCapture] Applying captured keyboard binding: {fullBindingString}");
        ViewModel.ApplyKeyboardCapture(fullBindingString, key);
    }


    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsCapturingKeyboard) || e.PropertyName == nameof(ViewModel.IsCapturingJoystick))
        {
            bool isCapturing = ViewModel.IsCapturingKeyboard || ViewModel.IsCapturingJoystick;
            if (isCapturing)
            {
                SettingsRootGrid.Focus(FocusState.Programmatic);
                System.Diagnostics.Debug.WriteLine("[SettingsWindow] Attempted to focus root grid for capture.");
            }
            if (e.PropertyName == nameof(ViewModel.IsCapturingJoystick))
            {
                if (ViewModel.IsCapturingJoystick) StartJoystickPolling();
                else StopJoystickPolling();
            }
        }
    }

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
                            this.DispatcherQueue?.TryEnqueue(() => { if (ViewModel.IsCapturingJoystick) { ViewModel.ApplyJoystickCapture(buttonName, buttonToApply); } });
                            break;
                        }
                    }
                    previousReading = currentReading;
                    await Task.Delay(50, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Capture] Error during joystick poll: {ex.Message}"); this.DispatcherQueue?.TryEnqueue(() => ViewModel.CancelCaptureCommand.Execute(null)); break; }
            }
            System.Diagnostics.Debug.WriteLine("[Capture] Joystick polling stopped.");
        }, token);
    }

    private void StopJoystickPolling() { try { _captureCts?.Cancel(); _captureCts?.Dispose(); } catch (ObjectDisposedException) { } _captureCts = null; }

    private GamepadButtons GetFirstPressedButton(GamepadButtons buttons) { foreach (GamepadButtons flag in (GamepadButtons[])Enum.GetValues(typeof(GamepadButtons))) { if (flag != GamepadButtons.None && buttons.HasFlag(flag)) { return flag; } } return GamepadButtons.None; }

    private void ViewModel_RequestClose(object? sender, EventArgs e) => this.Close();

    private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args) { if (args.WindowActivationState != WindowActivationState.Deactivated) { App.PauseGlobalHotkeys(); } }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        this.Activated -= SettingsWindow_Activated;
        this.Closed -= SettingsWindow_Closed;
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.RequestClose -= ViewModel_RequestClose;
            ViewModel.CleanupOnClose();
        }
        StopJoystickPolling();
        App.MainWin?.PauseLocalPolling();
        App.ReloadAndRestartGlobalServices(default);
        App.MainWin?.ResumeLocalPolling();
        System.Diagnostics.Debug.WriteLine("[SettingsWindow] Closed and global services reloaded.");
    }
}