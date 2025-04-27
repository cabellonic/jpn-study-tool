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

        PopulateBindingsCollection();
        ApplyLoadedBindings();

        _currentSelectedVid = _loadedSettings.SelectedGlobalGamepadVid;
        _currentSelectedPid = _loadedSettings.SelectedGlobalGamepadPid;
        DetectAndSelectDefaultGamepad();

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

    partial void OnIsCapturingKeyboardChanged(bool value)
    {
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] IsCapturingKeyboard changed to: {value}");
    }

    partial void OnIsCapturingJoystickChanged(bool value)
    {
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] IsCapturingJoystick changed to: {value}");
        // Polling logic now managed by SettingsWindow.xaml.cs based on this property change
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
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Applying loaded bindings...");
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
            if (joyBinding != null) System.Diagnostics.Debug.WriteLine($"[SettingsVM] Loaded Joy '{binding.ActionId}': {joyBinding}");
            if (keyBinding != null) System.Diagnostics.Debug.WriteLine($"[SettingsVM] Loaded Key '{binding.ActionId}': {keyBinding}");
        }
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Finished applying loaded bindings.");
    }

    private void DetectAndSelectDefaultGamepad()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Detecting gamepads via HidSharp...");
        HidDevice? selectedDevice = null; int? foundVid = null; int? foundPid = null;
        string detectedName = "No supported gamepad detected for hotkeys.";
        try
        {
            selectedDevice = DeviceList.Local.GetHidDeviceOrNull(vendorID: SettingsIDs.NINTENDO_VID, productID: SettingsIDs.PRO_CONTROLLER_PID_USB);
            if (selectedDevice == null) { selectedDevice = DeviceList.Local.GetHidDeviceOrNull(vendorID: SettingsIDs.XBOX_VID, productID: SettingsIDs.XBOX_PID); }
            if (selectedDevice != null)
            {
                foundVid = selectedDevice.VendorID; foundPid = selectedDevice.ProductID;
                try { detectedName = $"{selectedDevice.GetProductName()} ({foundVid:X4}/{foundPid:X4})"; } catch { detectedName = $"Gamepad ({foundVid:X4}/{foundPid:X4})"; }
                System.Diagnostics.Debug.WriteLine($"[SettingsVM] Found supported HID gamepad: {detectedName}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SettingsVM] Error detecting HID gamepads: {ex.Message}"); }

        if (!_currentSelectedVid.HasValue || !_currentSelectedPid.HasValue) { CurrentSelectedVid = foundVid; CurrentSelectedPid = foundPid; DetectedGamepadName = detectedName; System.Diagnostics.Debug.WriteLine($"[SettingsVM] Using detected gamepad as default."); }
        else if (foundVid == _currentSelectedVid && foundPid == _currentSelectedPid) { DetectedGamepadName = detectedName; System.Diagnostics.Debug.WriteLine($"[SettingsVM] Detected gamepad matches saved selection."); }
        else if (foundVid.HasValue) { DetectedGamepadName = $"Saved: ({_currentSelectedVid:X4}/{_currentSelectedPid:X4}), Detected: {detectedName}"; System.Diagnostics.Debug.WriteLine($"[SettingsVM] Detected gamepad differs from saved one. Keeping saved selection."); }
        else { DetectedGamepadName = $"Saved: ({_currentSelectedVid:X4}/{_currentSelectedPid:X4}), Detected: None"; System.Diagnostics.Debug.WriteLine($"[SettingsVM] No supported gamepad detected now, but using saved VID/PID."); }
    }

    private bool CanStartCapture(BindingConfig? binding) => binding != null && !IsCapturingKeyboard && !IsCapturingJoystick;
    private bool CanStartJoystickCapture(BindingConfig? binding) => CanStartCapture(binding) && IsGamepadConnected;

    private void StartKeyboardCapture(BindingConfig? binding)
    {
        if (!CanStartCapture(binding) || binding == null) return;
        BindingToCapture = binding; IsCapturingKeyboard = true; IsCapturingJoystick = false;
        CaptureStatusText = $"Press key(s) for '{BindingToCapture.ActionName}' (ESC to cancel)...";
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Starting keyboard capture for {BindingToCapture.ActionId}");
    }

    private void StartJoystickCapture(BindingConfig? binding)
    {
        if (!CanStartJoystickCapture(binding) || binding == null) return;
        BindingToCapture = binding; IsCapturingJoystick = true; IsCapturingKeyboard = false;
        CaptureStatusText = $"Press button for '{BindingToCapture.ActionName}' (ESC to cancel)...";
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Starting joystick capture for {BindingToCapture.ActionId}");
    }

    public void ApplyKeyboardCapture(string displayValue, object dataValue)
    {
        if (!IsCapturingKeyboard || BindingToCapture == null || string.IsNullOrWhiteSpace(displayValue)) return;
        System.Diagnostics.Debug.WriteLine($"[SettingsVM] Applying Keyboard capture for {BindingToCapture.ActionId}: {displayValue}");
        foreach (var existingBinding in Bindings)
        {
            if (existingBinding != BindingToCapture && !string.IsNullOrEmpty(existingBinding.KeyboardBindingDisplay) && existingBinding.KeyboardBindingDisplay.Equals(displayValue, StringComparison.OrdinalIgnoreCase))
            { System.Diagnostics.Debug.WriteLine($"[SettingsVM] Clearing previous keyboard binding '{displayValue}' from action '{existingBinding.ActionId}'"); existingBinding.KeyboardBindingDisplay = null; }
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
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Applying Joystick capture for {BindingToCapture.ActionId}: Internal='{internalButtonCode}'");
            foreach (var existingBinding in Bindings)
            {
                if (existingBinding != BindingToCapture && !string.IsNullOrEmpty(existingBinding.JoystickBindingDisplay) && existingBinding.JoystickBindingDisplay.Equals(internalButtonCode, StringComparison.OrdinalIgnoreCase))
                { System.Diagnostics.Debug.WriteLine($"[SettingsVM] Clearing previous joystick binding '{internalButtonCode}' from action '{existingBinding.ActionId}'"); existingBinding.JoystickBindingDisplay = null; }
            }
            BindingToCapture.JoystickBindingDisplay = internalButtonCode; BindingToCapture.JoystickBindingData = dataValue;
        }
        else { System.Diagnostics.Debug.WriteLine($"[SettingsVM] Joystick captured button '{displayValue}' has no internal mapping. Ignoring."); }
        CancelCapture();
    }

    private string? MapGamepadButtonToInternalCode(object dataValue) => dataValue is GamepadButtons button ? button switch { GamepadButtons.A => "BTN_A", GamepadButtons.B => "BTN_B", GamepadButtons.X => "BTN_X", GamepadButtons.Y => "BTN_Y", GamepadButtons.LeftShoulder => "BTN_TL", GamepadButtons.RightShoulder => "BTN_TR", GamepadButtons.View => "BTN_SELECT", GamepadButtons.Menu => "BTN_START", GamepadButtons.LeftThumbstick => "BTN_THUMBL", GamepadButtons.RightThumbstick => "BTN_THUMBR", GamepadButtons.DPadUp => "DPAD_UP", GamepadButtons.DPadDown => "DPAD_DOWN", GamepadButtons.DPadLeft => "DPAD_LEFT", GamepadButtons.DPadRight => "DPAD_RIGHT", _ => null } : null;

    private void CancelCapture() { if (!CanCancelCapture) return; System.Diagnostics.Debug.WriteLine("[SettingsVM] Cancelling capture."); IsCapturingKeyboard = false; IsCapturingJoystick = false; BindingToCapture = null; CaptureStatusText = null; }

    private void ResetKeyboardDefaults()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Resetting keyboard bindings to defaults...");
        var defaults = _settingsService.GetDefaultSettings();
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_"); var defaultDict = isGlobal ? defaults.GlobalKeyboardBindings : defaults.LocalKeyboardBindings;
            if (defaultDict.TryGetValue(binding.ActionId, out string? defaultKeyBinding)) { binding.KeyboardBindingDisplay = defaultKeyBinding; if (defaultKeyBinding != null) System.Diagnostics.Debug.WriteLine($"[SettingsVM] Reset Key '{binding.ActionId}' to: {defaultKeyBinding}"); }
            else { binding.KeyboardBindingDisplay = null; System.Diagnostics.Debug.WriteLine($"[SettingsVM] Cleared Key '{binding.ActionId}' (no default)."); }
        }
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Finished resetting keyboard bindings.");
    }

    private void ResetJoystickDefaults()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Resetting joystick bindings to defaults...");
        var defaults = _settingsService.GetDefaultSettings();
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_"); var defaultDict = isGlobal ? defaults.GlobalJoystickBindings : defaults.LocalJoystickBindings;
            if (defaultDict.TryGetValue(binding.ActionId, out string? defaultJoyBinding)) { binding.JoystickBindingDisplay = defaultJoyBinding; if (defaultJoyBinding != null) System.Diagnostics.Debug.WriteLine($"[SettingsVM] Reset Joy '{binding.ActionId}' to: {defaultJoyBinding}"); }
            else { binding.JoystickBindingDisplay = null; System.Diagnostics.Debug.WriteLine($"[SettingsVM] Cleared Joy '{binding.ActionId}' (no default)."); }
        }
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Finished resetting joystick bindings.");
    }

    private void SaveAndClose()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Preparing settings to save...");
        var settingsToSave = new AppSettings { ReadClipboardOnLoad = this.ReadClipboardOnLoad, SelectedGlobalGamepadVid = this.CurrentSelectedVid, SelectedGlobalGamepadPid = this.CurrentSelectedPid, GlobalJoystickBindings = new(), GlobalKeyboardBindings = new(), LocalJoystickBindings = new(), LocalKeyboardBindings = new() };
        foreach (var binding in Bindings)
        {
            bool isGlobal = binding.ActionId.StartsWith("GLOBAL_"); var joyTargetDict = isGlobal ? settingsToSave.GlobalJoystickBindings : settingsToSave.LocalJoystickBindings; var keyTargetDict = isGlobal ? settingsToSave.GlobalKeyboardBindings : settingsToSave.LocalKeyboardBindings;
            if (!string.IsNullOrEmpty(binding.JoystickBindingDisplay)) joyTargetDict[binding.ActionId] = binding.JoystickBindingDisplay;
            if (!string.IsNullOrEmpty(binding.KeyboardBindingDisplay)) keyTargetDict[binding.ActionId] = binding.KeyboardBindingDisplay;
        }
        _settingsService.SaveSettings(settingsToSave);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void CloseWithoutSaving() { System.Diagnostics.Debug.WriteLine("[SettingsVM] Cancelling settings changes."); RequestClose?.Invoke(this, EventArgs.Empty); }

    private void UpdateGamepadStatus()
    {
        bool currentlyConnected = Gamepad.Gamepads.Any();
        if (currentlyConnected != IsGamepadConnected)
        {
            IsGamepadConnected = currentlyConnected;
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Gamepad connected (WinRT): {IsGamepadConnected}");
            _dispatcherQueue?.TryEnqueue(() => { (StartCaptureJoystickCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged(); (ResetJoystickDefaultsCommand as RelayCommand)?.NotifyCanExecuteChanged(); DetectAndSelectDefaultGamepad(); });
        }
    }

    private void OnGamepadAddedOrRemoved(object? sender, object e) => _dispatcherQueue?.TryEnqueue(UpdateGamepadStatus);

    public void CleanupOnClose() { Gamepad.GamepadAdded -= OnGamepadAddedOrRemoved; Gamepad.GamepadRemoved -= OnGamepadAddedOrRemoved; System.Diagnostics.Debug.WriteLine("[SettingsVM] Gamepad event handlers removed."); }
}

public static class SettingsIDs { public const int NINTENDO_VID = 0x057E; public const int PRO_CONTROLLER_PID_USB = 0x2009; public const int XBOX_VID = 0x045E; public const int XBOX_PID = 0x028E; }