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
    public SettingsViewModel ViewModel { get; }
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

    private void Capture_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsCapturingKeyboard && !ViewModel.IsCapturingJoystick && e.Key == VirtualKey.Escape)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Escape pressed outside capture mode. Closing window.");
            this.Close();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsCapturingKeyboard)
        {
            e.Handled = true; // Handle the event immediately
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Capture_KeyDown: Key={e.Key}, Handled=True");

            VirtualKey key = e.Key;

            // Ignore modifier keys pressed alone
            if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu ||
                key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows ||
                key == VirtualKey.LeftControl || key == VirtualKey.RightControl ||
                key == VirtualKey.LeftShift || key == VirtualKey.RightShift ||
                key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu)
            {
                ViewModel.CaptureStatusText = $"Press the main key for '{ViewModel.BindingToCapture?.ActionName}' (Modifiers detected)...";
                System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Capture_KeyDown: Modifier key only ({key}). Waiting for main key.");
                return; // Wait for the actual key press
            }

            // Handle Escape for cancellation
            if (key == VirtualKey.Escape)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Capture_KeyDown: Escape pressed. Cancelling keyboard capture.");
                ViewModel.CancelCaptureCommand.Execute(null);
                return;
            }

            // Determine currently pressed modifier keys
            VirtualKeyModifiers activeModifiers = VirtualKeyModifiers.None;
            try
            {
                // Use static GetKeyStateForCurrentThread for each modifier
                if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    activeModifiers |= VirtualKeyModifiers.Control;
                if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    activeModifiers |= VirtualKeyModifiers.Menu;
                if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    activeModifiers |= VirtualKeyModifiers.Shift;
                if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                    InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    activeModifiers |= VirtualKeyModifiers.Windows;
            }
            catch (Exception keyStateEx)
            {
                // Log error if getting key state fails
                System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Error getting key state via InputKeyboardSource: {keyStateEx.Message}. Modifiers might be inaccurate.");
            }

            // Build the display string (e.g., "Ctrl+Alt+J")
            List<string> parts = new List<string>();
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
            parts.Add(key.ToString()); // Add the main key name

            string fullBindingString = string.Join("+", parts);

            // Apply the captured binding to the ViewModel
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Applying captured keyboard binding: {fullBindingString}");
            ViewModel.ApplyKeyboardCapture(fullBindingString, key); // Pass display string and raw key data
        }
        else if (ViewModel.IsCapturingJoystick && e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow] Capture_KeyDown: Escape pressed during Joy capture, Handled=True");
            ViewModel.CancelCaptureCommand.Execute(null);
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
        catch (ObjectDisposedException) { /* Ignore */ }
        _captureCts = null;
    }

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

        App.MainWin?.PauseLocalPolling();
        App.ReloadAndRestartGlobalServices(default);
        App.MainWin?.ResumeLocalPolling();

        System.Diagnostics.Debug.WriteLine("[SettingsWindow] Closed and global services reloaded.");
    }
}