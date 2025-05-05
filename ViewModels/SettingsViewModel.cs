// ViewModels/SettingsViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HidSharp;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;
using Windows.Gaming.Input;

namespace JpnStudyTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private AppSettings _loadedSettings;

    [ObservableProperty]
    private bool _readClipboardOnLoad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeyInputVisible))]
    [NotifyPropertyChangedFor(nameof(IsAiAnalysisOptionsVisible))]
    private bool _useAIMode;

    [ObservableProperty]
    private string? _geminiApiKey;

    [ObservableProperty]
    private AiAnalysisTrigger _aiAnalysisTriggerMode;

    public bool IsApiKeyInputVisible => UseAIMode;
    public bool IsAiAnalysisOptionsVisible => UseAIMode;
    public string ApiKeyWarningText => UseAIMode ? "API Key will be saved locally in configuration file." : string.Empty;


    public ObservableCollection<BindingConfig> Bindings { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureKeyboardCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureJoystickCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCaptureCommand))]
    [NotifyPropertyChangedFor(nameof(CanCancelCapture))]
    private bool _isCapturingKeyboard;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureKeyboardCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureJoystickCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCaptureCommand))]
    [NotifyPropertyChangedFor(nameof(CanCancelCapture))]
    private bool _isCapturingJoystick;

    [ObservableProperty]
    private BindingConfig? _bindingToCapture;
    [ObservableProperty]
    private string? _captureStatusText;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureJoystickCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetJoystickDefaultsCommand))]
    private bool _isGamepadConnected;

    [ObservableProperty]
    private int? _currentSelectedVid = null;
    [ObservableProperty]
    private int? _currentSelectedPid = null;
    [ObservableProperty]
    private string? _detectedGamepadName = "No supported gamepad detected.";

    public RelayCommand<BindingConfig> StartCaptureKeyboardCommand { get; }
    public RelayCommand<BindingConfig> StartCaptureJoystickCommand { get; }
    public RelayCommand CancelCaptureCommand { get; }
    public RelayCommand ResetKeyboardDefaultsCommand { get; }
    public RelayCommand ResetJoystickDefaultsCommand { get; }
    public RelayCommand SaveAndCloseCommand { get; }
    public RelayCommand CancelCommand { get; }
    public event EventHandler? RequestClose;
    private readonly DispatcherQueue? _dispatcherQueue;

    public bool CanCancelCapture => IsCapturingKeyboard || IsCapturingJoystick;

    public SettingsViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = new SettingsService();
        _loadedSettings = _settingsService.LoadSettings();

        _readClipboardOnLoad = _loadedSettings.ReadClipboardOnLoad;
        _useAIMode = _loadedSettings.UseAIMode;
        _geminiApiKey = _loadedSettings.GeminiApiKey;
        _aiAnalysisTriggerMode = _loadedSettings.AiAnalysisTriggerMode;

        PopulateBindingsCollection();
        ApplyLoadedBindings();

        _currentSelectedVid = _loadedSettings.SelectedGlobalGamepadVid;
        _currentSelectedPid = _loadedSettings.SelectedGlobalGamepadPid;


        StartCaptureKeyboardCommand = new RelayCommand<BindingConfig>(StartKeyboardCapture, CanStartCapture);
        StartCaptureJoystickCommand = new RelayCommand<BindingConfig>(StartJoystickCapture, CanStartJoystickCapture);
        CancelCaptureCommand = new RelayCommand(CancelCapture, () => CanCancelCapture);
        ResetKeyboardDefaultsCommand = new RelayCommand(ResetKeyboardDefaults);
        ResetJoystickDefaultsCommand = new RelayCommand(ResetJoystickDefaults, () => IsGamepadConnected);
        SaveAndCloseCommand = new RelayCommand(SaveAndClose);
        CancelCommand = new RelayCommand(CloseWithoutSaving);

        UpdateGamepadStatus();
        Gamepad.GamepadAdded += OnGamepadAddedOrRemoved;
        Gamepad.GamepadRemoved += OnGamepadAddedOrRemoved;
    }

    partial void OnUseAIModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsApiKeyInputVisible));
        OnPropertyChanged(nameof(ApiKeyWarningText));
        OnPropertyChanged(nameof(IsAiAnalysisOptionsVisible));
    }


    private void PopulateBindingsCollection()
    {
        Bindings.Clear();
        Bindings.Add(new BindingConfig("NAV_UP", "Go Up"));
        Bindings.Add(new BindingConfig("NAV_DOWN", "Go Down"));
        Bindings.Add(new BindingConfig("NAV_LEFT", "Go Left"));
        Bindings.Add(new BindingConfig("NAV_RIGHT", "Go Right"));
        Bindings.Add(new BindingConfig("DETAIL_ENTER", "Show Details"));
        Bindings.Add(new BindingConfig("DETAIL_BACK", "Go Back"));
        Bindings.Add(new BindingConfig("SAVE_WORD", "Save Word"));
        Bindings.Add(new BindingConfig("DELETE_WORD", "Delete Word"));
        Bindings.Add(new BindingConfig("GLOBAL_TOGGLE", "Toggle Window (Global)"));
        Bindings.Add(new BindingConfig("GLOBAL_MENU", "Open Menu (Global)"));
    }

    private void ApplyLoadedBindings()
    {
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_");
            string? joyBinding = null; string? keyBinding = null;
            var joySourceDict = isGlobal ? _loadedSettings.GlobalJoystickBindings : _loadedSettings.LocalJoystickBindings;
            var keySourceDict = isGlobal ? _loadedSettings.GlobalKeyboardBindings : _loadedSettings.LocalKeyboardBindings;
            joySourceDict?.TryGetValue(binding.ActionId, out joyBinding);
            keySourceDict?.TryGetValue(binding.ActionId, out keyBinding);
            binding.JoystickBindingDisplay = joyBinding;
            binding.KeyboardBindingDisplay = keyBinding;
        }
    }

    private void DetectAndSelectDefaultGamepad()
    {
        HidDevice? selectedDevice = null;
        int? foundVid = null;
        int? foundPid = null;
        string detectedName = "No supported gamepad detected for hotkeys.";
        try
        {
            selectedDevice = DeviceList.Local.GetHidDeviceOrNull(vendorID: SettingsIDs.NINTENDO_VID, productID: SettingsIDs.PRO_CONTROLLER_PID_USB);
            if (selectedDevice == null)
            {
                selectedDevice = DeviceList.Local.GetHidDeviceOrNull(vendorID: SettingsIDs.XBOX_VID, productID: SettingsIDs.XBOX_PID);
            }

            if (selectedDevice != null)
            {
                foundVid = selectedDevice.VendorID;
                foundPid = selectedDevice.ProductID;
                try { detectedName = $"{selectedDevice.GetProductName()} ({foundVid:X4}/{foundPid:X4})"; }
                catch { detectedName = $"Gamepad ({foundVid:X4}/{foundPid:X4})"; }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Error detecting gamepad: {ex.Message}");
            detectedName = "Error detecting gamepad.";
        }

        if (!_currentSelectedVid.HasValue || !_currentSelectedPid.HasValue)
        {
            CurrentSelectedVid = foundVid;
            CurrentSelectedPid = foundPid;
            DetectedGamepadName = detectedName;
        }
        else
        {
            if (foundVid.HasValue && foundVid == _currentSelectedVid && foundPid == _currentSelectedPid)
            {
                DetectedGamepadName = detectedName;
            }
            else if (foundVid.HasValue)
            {
                DetectedGamepadName = $"Saved: ({_currentSelectedVid:X4}/{_currentSelectedPid:X4}), Current: {detectedName}";
            }
            else
            {
                DetectedGamepadName = $"Saved: ({_currentSelectedVid:X4}/{_currentSelectedPid:X4}), Current: None";
            }
        }
    }

    private bool CanStartCapture(BindingConfig? binding) => binding != null && !IsCapturingKeyboard && !IsCapturingJoystick;
    private bool CanStartJoystickCapture(BindingConfig? binding) => CanStartCapture(binding) && IsGamepadConnected;

    private void StartKeyboardCapture(BindingConfig? binding)
    {
        if (!CanStartCapture(binding) || binding == null) return;
        BindingToCapture = binding; IsCapturingKeyboard = true; IsCapturingJoystick = false;
        CaptureStatusText = $"Press key(s) for '{BindingToCapture.ActionName}' (ESC to cancel)...";
    }

    private void StartJoystickCapture(BindingConfig? binding)
    {
        if (!CanStartJoystickCapture(binding) || binding == null) return;
        BindingToCapture = binding; IsCapturingJoystick = true; IsCapturingKeyboard = false;
        CaptureStatusText = $"Press button for '{BindingToCapture.ActionName}' (ESC to cancel)...";
    }

    public void ApplyKeyboardCapture(string displayValue, object dataValue)
    {
        if (!IsCapturingKeyboard || BindingToCapture == null || string.IsNullOrWhiteSpace(displayValue)) return;
        foreach (var existingBinding in Bindings)
        {
            if (existingBinding != BindingToCapture && !string.IsNullOrEmpty(existingBinding.KeyboardBindingDisplay) && existingBinding.KeyboardBindingDisplay.Equals(displayValue, StringComparison.OrdinalIgnoreCase))
            { existingBinding.KeyboardBindingDisplay = null; }
        }
        BindingToCapture.KeyboardBindingDisplay = displayValue; BindingToCapture.KeyboardBindingData = dataValue;
        CancelCapture();
    }

    public void ApplyJoystickCapture(string displayValue, object dataValue)
    {
        if (!IsCapturingJoystick || BindingToCapture == null) return;
        string? internalButtonCode = MapGamepadButtonToInternalCode(dataValue);
        if (internalButtonCode != null)
        {
            foreach (var existingBinding in Bindings)
            {
                if (existingBinding != BindingToCapture && !string.IsNullOrEmpty(existingBinding.JoystickBindingDisplay) && existingBinding.JoystickBindingDisplay.Equals(internalButtonCode, StringComparison.OrdinalIgnoreCase))
                { existingBinding.JoystickBindingDisplay = null; }
            }
            BindingToCapture.JoystickBindingDisplay = internalButtonCode; BindingToCapture.JoystickBindingData = dataValue;
        }
        CancelCapture();
    }

    private string? MapGamepadButtonToInternalCode(object dataValue) => dataValue is GamepadButtons button ? button switch
    {
        GamepadButtons.A => "BTN_A",
        GamepadButtons.B => "BTN_B",
        GamepadButtons.X => "BTN_X",
        GamepadButtons.Y => "BTN_Y",
        GamepadButtons.LeftShoulder => "BTN_TL",
        GamepadButtons.RightShoulder => "BTN_TR",
        GamepadButtons.View => "BTN_SELECT",
        GamepadButtons.Menu => "BTN_START",
        GamepadButtons.LeftThumbstick => "BTN_THUMBL",
        GamepadButtons.RightThumbstick => "BTN_THUMBR",
        GamepadButtons.DPadUp => "DPAD_UP",
        GamepadButtons.DPadDown => "DPAD_DOWN",
        GamepadButtons.DPadLeft => "DPAD_LEFT",
        GamepadButtons.DPadRight => "DPAD_RIGHT",
        _ => null
    } : null;

    private void CancelCapture() { if (!CanCancelCapture) return; IsCapturingKeyboard = false; IsCapturingJoystick = false; BindingToCapture = null; CaptureStatusText = null; }

    private void ResetKeyboardDefaults()
    {
        var defaults = _settingsService.GetDefaultSettings();
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_"); var defaultDict = isGlobal ? defaults.GlobalKeyboardBindings : defaults.LocalKeyboardBindings;
            if (defaultDict.TryGetValue(binding.ActionId, out string? defaultKeyBinding)) { binding.KeyboardBindingDisplay = defaultKeyBinding; }
            else { binding.KeyboardBindingDisplay = null; }
        }
    }

    private void ResetJoystickDefaults()
    {
        var defaults = _settingsService.GetDefaultSettings();
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_"); var defaultDict = isGlobal ? defaults.GlobalJoystickBindings : defaults.LocalJoystickBindings;
            if (defaultDict.TryGetValue(binding.ActionId, out string? defaultJoyBinding)) { binding.JoystickBindingDisplay = defaultJoyBinding; }
            else { binding.JoystickBindingDisplay = null; }
        }
    }

    private void SaveAndClose()
    {
        var settingsToSave = new AppSettings
        {
            ReadClipboardOnLoad = this.ReadClipboardOnLoad,
            UseAIMode = this.UseAIMode,
            GeminiApiKey = this.GeminiApiKey,
            AiAnalysisTriggerMode = this.AiAnalysisTriggerMode,
            SelectedGlobalGamepadVid = this.CurrentSelectedVid,
            SelectedGlobalGamepadPid = this.CurrentSelectedPid,
            GlobalJoystickBindings = new(),
            GlobalKeyboardBindings = new(),
            LocalJoystickBindings = new(),
            LocalKeyboardBindings = new()
        };
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_");
            var joyTargetDict = isGlobal ? settingsToSave.GlobalJoystickBindings : settingsToSave.LocalJoystickBindings;
            var keyTargetDict = isGlobal ? settingsToSave.GlobalKeyboardBindings : settingsToSave.LocalKeyboardBindings;
            if (!string.IsNullOrEmpty(binding.JoystickBindingDisplay)) joyTargetDict[binding.ActionId] = binding.JoystickBindingDisplay;
            if (!string.IsNullOrEmpty(binding.KeyboardBindingDisplay)) keyTargetDict[binding.ActionId] = binding.KeyboardBindingDisplay;
        }
        _settingsService.SaveSettings(settingsToSave);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void CloseWithoutSaving() { RequestClose?.Invoke(this, EventArgs.Empty); }

    private void UpdateGamepadStatus()
    {
        bool currentlyConnected = Gamepad.Gamepads.Any();
        if (currentlyConnected != IsGamepadConnected)
        {
            IsGamepadConnected = currentlyConnected;
            _dispatcherQueue?.TryEnqueue(() =>
            {
                DetectAndSelectDefaultGamepad();
                (StartCaptureJoystickCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged();
                (ResetJoystickDefaultsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
        }
        else if (IsGamepadConnected)
        {
            DetectAndSelectDefaultGamepad();
        }
    }

    private void OnGamepadAddedOrRemoved(object? sender, object e) => UpdateGamepadStatus();

    partial void OnAiAnalysisTriggerModeChanged(AiAnalysisTrigger value)
    {
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] OnAiAnalysisTriggerModeChanged called with value: {value}");
    }

    public void CleanupOnClose() { Gamepad.GamepadAdded -= OnGamepadAddedOrRemoved; Gamepad.GamepadRemoved -= OnGamepadAddedOrRemoved; }
}