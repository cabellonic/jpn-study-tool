// ViewModels/SettingsViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JpnStudyTool.Models;
using Microsoft.UI.Dispatching;
using Windows.Gaming.Input;

namespace JpnStudyTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _readClipboardOnLoad;
    public ObservableCollection<BindingConfig> Bindings { get; } = new();
    [ObservableProperty]
    private bool _isCapturingKeyboard;
    [ObservableProperty]
    private bool _isCapturingJoystick;
    [ObservableProperty]
    private BindingConfig? _bindingToCapture;
    [ObservableProperty]
    private string? _captureStatusText;
    [ObservableProperty]
    private bool _isGamepadConnected;

    public ICommand StartCaptureKeyboardCommand { get; }
    public ICommand StartCaptureJoystickCommand { get; }
    public ICommand CancelCaptureCommand { get; }
    public ICommand ResetKeyboardDefaultsCommand { get; }
    public ICommand ResetJoystickDefaultsCommand { get; }
    public ICommand SaveAndCloseCommand { get; }
    public ICommand CancelCommand { get; }
    public event EventHandler? RequestClose;
    private readonly DispatcherQueue? _dispatcherQueue;

    public SettingsViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (_dispatcherQueue == null)
        {
            System.Diagnostics.Debug.WriteLine("[ERROR] SettingsViewModel created without a valid DispatcherQueue!");
        }
        LoadSettings();
        StartCaptureKeyboardCommand = new RelayCommand<BindingConfig>(StartKeyboardCapture, CanStartCapture);
        StartCaptureJoystickCommand = new RelayCommand<BindingConfig>(StartJoystickCapture, CanStartJoystickCapture);
        CancelCaptureCommand = new RelayCommand(CancelCapture, CanCancelCapture);
        ResetKeyboardDefaultsCommand = new RelayCommand(ResetKeyboardDefaults);
        ResetJoystickDefaultsCommand = new RelayCommand(ResetJoystickDefaults, () => IsGamepadConnected);
        SaveAndCloseCommand = new RelayCommand(SaveAndClose);
        CancelCommand = new RelayCommand(CloseWithoutSaving);
        UpdateGamepadStatus();
        Gamepad.GamepadAdded += OnGamepadAddedOrRemoved;
        Gamepad.GamepadRemoved += OnGamepadAddedOrRemoved;
    }

    private void LoadSettings()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Loading settings...");
        ReadClipboardOnLoad = false;
        Bindings.Clear();
        Bindings.Add(new BindingConfig("NAV_UP", "Go Up", "Up"));
        Bindings.Add(new BindingConfig("NAV_DOWN", "Go Down", "Down"));
        Bindings.Add(new BindingConfig("NAV_LEFT", "Go Left", "Left"));
        Bindings.Add(new BindingConfig("NAV_RIGHT", "Go Right", "Right"));
        Bindings.Add(new BindingConfig("DETAIL_ENTER", "Show Details", "Enter"));
        Bindings.Add(new BindingConfig("DETAIL_BACK", "Go Back", "Backspace"));
        Bindings.Add(new BindingConfig("SAVE_WORD", "Save Word", "S"));
        Bindings.Add(new BindingConfig("DELETE_WORD", "Delete Word", "Delete"));
        Bindings.Add(new BindingConfig("GLOBAL_TOGGLE", "Toggle Window", "Ctrl+Alt+J"));
        Bindings.Add(new BindingConfig("GLOBAL_MENU", "Open Menu", "Ctrl+Alt+Q"));
    }

    private bool CanStartCapture(BindingConfig? binding) => binding != null && !IsCapturingKeyboard && !IsCapturingJoystick;
    private bool CanStartJoystickCapture(BindingConfig? binding) => CanStartCapture(binding) && IsGamepadConnected;
    private bool CanCancelCapture() => IsCapturingKeyboard || IsCapturingJoystick;

    private void StartKeyboardCapture(BindingConfig? binding)
    {
        if (!CanStartCapture(binding)) return;
        BindingToCapture = binding!;
        IsCapturingKeyboard = true;
        IsCapturingJoystick = false;
        CaptureStatusText = $"Press key(s) for '{BindingToCapture.ActionName}' (ESC to cancel)...";
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Starting keyboard capture for {BindingToCapture.ActionId}");
    }

    private void StartJoystickCapture(BindingConfig? binding)
    {
        if (!CanStartJoystickCapture(binding)) return;
        BindingToCapture = binding!;
        IsCapturingJoystick = true;
        IsCapturingKeyboard = false;
        CaptureStatusText = $"Press button/axis for '{BindingToCapture.ActionName}' (ESC or Back to cancel)...";
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Starting joystick capture for {BindingToCapture.ActionId}");
    }

    public void ApplyKeyboardCapture(string displayValue, object dataValue)
    {
        if (!IsCapturingKeyboard || BindingToCapture == null) return;
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Keyboard captured for {BindingToCapture.ActionId}: {displayValue}");
        BindingToCapture.KeyboardBindingDisplay = displayValue;
        BindingToCapture.KeyboardBindingData = dataValue;
        CancelCapture();
    }

    public void ApplyJoystickCapture(string displayValue, object dataValue)
    {
        if (!IsCapturingJoystick || BindingToCapture == null) return;
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Joystick captured for {BindingToCapture.ActionId}: {displayValue}");
        BindingToCapture.JoystickBindingDisplay = displayValue;
        BindingToCapture.JoystickBindingData = dataValue;
        CancelCapture();
    }

    private void CancelCapture()
    {
        if (!CanCancelCapture()) return;
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Cancelling capture.");
        IsCapturingKeyboard = false;
        IsCapturingJoystick = false;
        BindingToCapture = null;
        CaptureStatusText = null;
    }

    private void ResetKeyboardDefaults()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Resetting keyboard defaults...");
        LoadSettings();
    }

    private void ResetJoystickDefaults()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Resetting joystick defaults...");
        foreach (var binding in Bindings)
        {
            binding.JoystickBindingDisplay = null;
            binding.JoystickBindingData = null;
        }
    }

    private void SaveAndClose()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Saving settings...");
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void CloseWithoutSaving()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Cancelling settings changes.");
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateGamepadStatus()
    {
        bool currentlyConnected = Gamepad.Gamepads.Any();
        if (currentlyConnected != IsGamepadConnected)
        {
            IsGamepadConnected = currentlyConnected;
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Gamepad connected: {IsGamepadConnected}");
            _dispatcherQueue?.TryEnqueue(() =>
            {
                (StartCaptureJoystickCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged();
                (ResetJoystickDefaultsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
        }
    }

    private void OnGamepadAddedOrRemoved(object? sender, object e)
    {
        _dispatcherQueue?.TryEnqueue(UpdateGamepadStatus);
    }

    public void CleanupOnClose()
    {
        Gamepad.GamepadAdded -= OnGamepadAddedOrRemoved;
        Gamepad.GamepadRemoved -= OnGamepadAddedOrRemoved;
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Gamepad event handlers removed.");
    }
}