using System;
using System.Collections.Generic;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
namespace JpnStudyTool
{
    public sealed partial class SettingsWindow : Window
    {
        public SettingsViewModel ViewModel { get; }
        public SettingsWindow()
        {
            this.InitializeComponent();
            ViewModel = new SettingsViewModel();
            SettingsRootGrid.DataContext = ViewModel;
            ViewModel.RequestClose += ViewModel_RequestClose;
            this.Activated += SettingsWindow_Activated;
            this.Closed += SettingsWindow_Closed;
            SettingsRootGrid.KeyDown += SettingsRootGrid_KeyDown;
            App.PauseGlobalHotkeys();
        }
        private void SettingsRootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (ViewModel.IsCapturingKeyboard)
            {
                ProcessKeyboardCaptureForViewModel(e.Key);
                e.Handled = true;
            }
            else if (ViewModel.IsCapturingJoystick && e.Key == VirtualKey.Escape)
            {
                ViewModel.CancelCaptureCommand.Execute(null);
                e.Handled = true;
            }
            else if (!ViewModel.IsCapturingKeyboard && !ViewModel.IsCapturingJoystick && e.Key == VirtualKey.Escape)
            {
                ViewModel.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }
        private void ProcessKeyboardCaptureForViewModel(VirtualKey key)
        {
            if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu ||
                key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows ||
                key == VirtualKey.LeftControl || key == VirtualKey.RightControl ||
                key == VirtualKey.LeftShift || key == VirtualKey.RightShift ||
                key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu)
            {
                ViewModel.UpdateKeyboardCaptureStatusForModifier();
                return;
            }
            if (key == VirtualKey.Escape)
            {
                ViewModel.CancelCaptureCommand.Execute(null);
                return;
            }
            VirtualKeyModifiers activeModifiers = VirtualKeyModifiers.None;
            try
            {
                var keyState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                if (keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Control;
                keyState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
                if (keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Menu;
                keyState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                if (keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Shift;
                var winStateL = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows);
                var winStateR = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows);
                if (winStateL.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) || winStateR.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) activeModifiers |= VirtualKeyModifiers.Windows;
            }
            catch (Exception keyStateEx)
            {
                System.Diagnostics.Debug.WriteLine($"[SW ProcessKeyboardCaptureVM] Warning: Error getting key state: {keyStateEx.Message}");
            }
            List<string> parts = new List<string>();
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
            if (activeModifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
            parts.Add(key.ToString());
            string fullBindingString = string.Join("+", parts);
            ViewModel.ApplyKeyboardCapture(fullBindingString, key);
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
                ViewModel.UpdateGamepadStatus();
            }
        }
        private void SettingsWindow_Closed(object sender, WindowEventArgs args)
        {
            this.Activated -= SettingsWindow_Activated;
            this.Closed -= SettingsWindow_Closed;
            SettingsRootGrid.KeyDown -= SettingsRootGrid_KeyDown;
            if (ViewModel != null)
            {
                ViewModel.RequestClose -= ViewModel_RequestClose;
                ViewModel.CleanupOnClose();
            }
            App.MainWin?.PauseLocalPolling();
            App.ReloadAndRestartGlobalServices(default);
            App.MainWin?.ResumeLocalPolling();
            System.Diagnostics.Debug.WriteLine("[SettingsWindow] Closed and global services reloaded.");
        }
    }
}