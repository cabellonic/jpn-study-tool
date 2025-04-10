// ViewModels/SettingsViewModel.cs
// ViewModels/SettingsViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HidSharp;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;
using Windows.Gaming.Input; // Para Gamepad


namespace JpnStudyTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService; // Instancia del servicio
    private AppSettings _loadedSettings; // Para mantener la configuración cargada

    // --- Propiedades Observable ---
    [ObservableProperty]
    private bool _readClipboardOnLoad;

    public ObservableCollection<BindingConfig> Bindings { get; } = new();

    [ObservableProperty] private bool _isCapturingKeyboard;
    [ObservableProperty] private bool _isCapturingJoystick;
    [ObservableProperty] private BindingConfig? _bindingToCapture;
    [ObservableProperty] private string? _captureStatusText;
    [ObservableProperty] private bool _isGamepadConnected;
    [ObservableProperty]
    private int? _currentSelectedVid = null;
    [ObservableProperty]
    private int? _currentSelectedPid = null;
    [ObservableProperty]
    private string? _detectedGamepadName = "No supported gamepad detected."; // Para UI

    // --- Comandos ---
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
        CancelCaptureCommand = new RelayCommand(CancelCapture, CanCancelCapture);
        ResetKeyboardDefaultsCommand = new RelayCommand(ResetKeyboardDefaults);
        ResetJoystickDefaultsCommand = new RelayCommand(ResetJoystickDefaults, () => IsGamepadConnected);
        SaveAndCloseCommand = new RelayCommand(SaveAndClose); // Método modificado abajo
        CancelCommand = new RelayCommand(CloseWithoutSaving);

        UpdateGamepadStatus();
        Gamepad.GamepadAdded += OnGamepadAddedOrRemoved;
        Gamepad.GamepadRemoved += OnGamepadAddedOrRemoved;
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
            if (_loadedSettings.GlobalJoystickBindings.TryGetValue(binding.ActionId, out string? joyButton) && joyButton != null)
            {
                binding.JoystickBindingDisplay = joyButton;
                System.Diagnostics.Debug.WriteLine($"[SettingsVM] Loaded Joy '{binding.ActionId}': {joyButton}");
            }
            else
            {
                binding.JoystickBindingDisplay = null;
            }

            if (_loadedSettings.GlobalKeyboardBindings.TryGetValue(binding.ActionId, out string? keyBinding) && keyBinding != null)
            {
                binding.KeyboardBindingDisplay = keyBinding;
                System.Diagnostics.Debug.WriteLine($"[SettingsVM] Loaded Key '{binding.ActionId}': {keyBinding}");
            }
            else
            {
                binding.KeyboardBindingDisplay = null;
            }
        }
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Finished applying loaded bindings.");
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
    public bool CanCancelCapture() => IsCapturingKeyboard || IsCapturingJoystick;

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

        string? internalButtonCode = MapGamepadButtonToInternalCode(dataValue);

        if (internalButtonCode != null)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Joystick captured for {BindingToCapture.ActionId}: Display='{displayValue}', Internal='{internalButtonCode}'");
            BindingToCapture.JoystickBindingDisplay = internalButtonCode;
            BindingToCapture.JoystickBindingData = dataValue;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Joystick captured button '{displayValue}' has no internal mapping. Ignoring.");
        }
        CancelCapture();
    }

    private string? MapGamepadButtonToInternalCode(object dataValue)
    {
        if (dataValue is GamepadButtons button)
        {
            return button switch
            {
                GamepadButtons.LeftThumbstick => "BTN_THUMBL",
                GamepadButtons.RightThumbstick => "BTN_THUMBR",
                GamepadButtons.Menu => "BTN_START",
                GamepadButtons.View => "BTN_SELECT",
                GamepadButtons.A => "BTN_A",
                GamepadButtons.B => "BTN_B",
                GamepadButtons.X => "BTN_X",
                GamepadButtons.Y => "BTN_Y",
                GamepadButtons.DPadUp => "DPAD_UP",
                GamepadButtons.DPadDown => "DPAD_DOWN",
                GamepadButtons.DPadLeft => "DPAD_LEFT",
                GamepadButtons.DPadRight => "DPAD_RIGHT",
                GamepadButtons.LeftShoulder => "BTN_TL",
                GamepadButtons.RightShoulder => "BTN_TR",
                //GamepadButtons.LeftTrigger => "BTN_ZL",   // LT
                //GamepadButtons.RightTrigger => "BTN_ZR",  // RT
                _ => null // Not mapped button
            };
        }
        return null;
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
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Resetting keyboard defaults (Not fully implemented - loading defaults)");
        _loadedSettings = new AppSettings();
        ApplyLoadedBindings();
    }

    private void ResetJoystickDefaults()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Resetting joystick defaults (Not fully implemented - loading defaults)");
        _loadedSettings = new AppSettings();
        ApplyLoadedBindings();
    }

    private void DetectAndSelectDefaultGamepad()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Detecting gamepads via HidSharp...");
        HidDevice? selectedDevice = null;
        int? foundVid = null;
        int? foundPid = null;
        string detectedName = "No supported gamepad detected.";

        try
        {
            var proControllers = DeviceList.Local.GetHidDevices(vendorID: SettingsIDs.NINTENDO_VID)
                                       .Where(d => d.ProductID == SettingsIDs.PRO_CONTROLLER_PID_USB)
                                       .ToList();

            if (proControllers.Any())
            {
                selectedDevice = proControllers.First();
                foundVid = selectedDevice.VendorID;
                foundPid = selectedDevice.ProductID;
                detectedName = $"Pro Controller ({foundVid:X4}/{foundPid:X4})";
                System.Diagnostics.Debug.WriteLine($"[SettingsVM] Found Pro Controller: {selectedDevice.GetProductName()}");
            }
            else
            {
                var xboxControllers = DeviceList.Local.GetHidDevices(vendorID: SettingsIDs.XBOX_VID, productID: SettingsIDs.XBOX_PID).ToList();
                if (xboxControllers.Any())
                {
                    selectedDevice = xboxControllers.First();
                    foundVid = selectedDevice.VendorID;
                    foundPid = selectedDevice.ProductID;
                    detectedName = $"Xbox Controller ({foundVid:X4}/{foundPid:X4})";
                    System.Diagnostics.Debug.WriteLine($"[SettingsVM] Found Xbox Controller: {selectedDevice.GetProductName()}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Error detecting HID gamepads: {ex.Message}");
        }

        if (!_currentSelectedVid.HasValue || !_currentSelectedPid.HasValue)
        {
            CurrentSelectedVid = foundVid;
            CurrentSelectedPid = foundPid;
            DetectedGamepadName = detectedName;
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Using detected gamepad as default.");
        }
        else if (foundVid == _currentSelectedVid && foundPid == _currentSelectedPid)
        {
            DetectedGamepadName = detectedName;
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Detected gamepad matches saved selection.");
        }
        else if (foundVid.HasValue)
        {
            DetectedGamepadName = $"Saved: ({_currentSelectedVid:X4}/{_currentSelectedPid:X4}), Detected: {detectedName}";
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] Detected gamepad differs from saved one.");
        }
        else
        {
            DetectedGamepadName = $"Saved: ({_currentSelectedVid:X4}/{_currentSelectedPid:X4}), Detected: None";
            System.Diagnostics.Debug.WriteLine($"[SettingsVM] No supported gamepad detected now, but using saved VID/PID.");
        }
    }


    private void SaveAndClose()
    {
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Preparing settings to save...");

        var settingsToSave = new AppSettings
        {
            ReadClipboardOnLoad = this.ReadClipboardOnLoad,

            SelectedGlobalGamepadVid = this.CurrentSelectedVid,
            SelectedGlobalGamepadPid = this.CurrentSelectedPid,

            GlobalJoystickBindings = new Dictionary<string, string?>(),
            GlobalKeyboardBindings = new Dictionary<string, string?>()
        };

        // Poblar los diccionarios (como antes)
        foreach (var binding in Bindings)
        {
            if (binding.ActionId.StartsWith("GLOBAL_"))
            {
                if (!string.IsNullOrEmpty(binding.JoystickBindingDisplay))
                    settingsToSave.GlobalJoystickBindings[binding.ActionId] = binding.JoystickBindingDisplay;
                else
                    settingsToSave.GlobalJoystickBindings.Remove(binding.ActionId);

                if (!string.IsNullOrEmpty(binding.KeyboardBindingDisplay))
                    settingsToSave.GlobalKeyboardBindings[binding.ActionId] = binding.KeyboardBindingDisplay;
                else
                    settingsToSave.GlobalKeyboardBindings.Remove(binding.ActionId);
            }
        }

        _settingsService.SaveSettings(settingsToSave);
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

    partial void OnIsCapturingKeyboardChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelCapture));
        (StartCaptureKeyboardCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged();
        (StartCaptureJoystickCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged();
        (CancelCaptureCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    partial void OnIsCapturingJoystickChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelCapture));
        (StartCaptureKeyboardCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged();
        (StartCaptureJoystickCommand as RelayCommand<BindingConfig>)?.NotifyCanExecuteChanged();
        (CancelCaptureCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    public void CleanupOnClose()
    {
        Gamepad.GamepadAdded -= OnGamepadAddedOrRemoved;
        Gamepad.GamepadRemoved -= OnGamepadAddedOrRemoved;
        System.Diagnostics.Debug.WriteLine("[SettingsVM] Gamepad event handlers removed.");
    }
}