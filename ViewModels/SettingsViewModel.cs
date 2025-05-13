// ViewModels/SettingsViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Windows.Gaming.Input;
namespace JpnStudyTool.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsService _settingsService;
        private AppSettings _loadedSettings;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private CancellationTokenSource? _captureCts;
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
        private int? _currentSelectedVid;
        [ObservableProperty]
        private int? _currentSelectedPid;
        [ObservableProperty]
        private string? _detectedGamepadName = "No supported gamepad detected.";
        public event EventHandler? RequestClose;
        public bool CanCancelCapture => IsCapturingKeyboard || IsCapturingJoystick;
        public SettingsViewModel()
        {
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _settingsService = new SettingsService();
            _loadedSettings = _settingsService.LoadSettings();
            ReadClipboardOnLoad = _loadedSettings.ReadClipboardOnLoad;
            UseAIMode = _loadedSettings.UseAIMode;
            GeminiApiKey = _loadedSettings.GeminiApiKey;
            AiAnalysisTriggerMode = _loadedSettings.AiAnalysisTriggerMode;
            CurrentSelectedVid = _loadedSettings.SelectedGlobalGamepadVid;
            CurrentSelectedPid = _loadedSettings.SelectedGlobalGamepadPid;
            PopulateBindingsCollection();
            ApplyLoadedBindings();
            UpdateGamepadStatus();
            Windows.Gaming.Input.Gamepad.GamepadAdded += OnGamepadConnectionChanged;
            Windows.Gaming.Input.Gamepad.GamepadRemoved += OnGamepadConnectionChanged;
        }
        [RelayCommand(CanExecute = nameof(CanStartCaptureKeyboard))]
        private void StartCaptureKeyboard(BindingConfig? binding)
        {
            if (binding == null) return;
            BindingToCapture = binding;
            IsCapturingKeyboard = true;
            IsCapturingJoystick = false;
            CaptureStatusText = $"Press key(s) for '{BindingToCapture.ActionName}' (ESC to cancel from keyboard)...";
        }
        private bool CanStartCaptureKeyboard(BindingConfig? binding) => binding != null && !IsCapturingKeyboard && !IsCapturingJoystick;
        [RelayCommand(CanExecute = nameof(CanStartCaptureJoystick))]
        private void StartCaptureJoystick(BindingConfig? binding)
        {
            if (binding == null) return;
            BindingToCapture = binding;
            IsCapturingJoystick = true;
            IsCapturingKeyboard = false;
            CaptureStatusText = $"Press button on joystick for '{BindingToCapture.ActionName}' (ESC on keyboard to cancel)...";
            StartJoystickPolling();
        }
        private bool CanStartCaptureJoystick(BindingConfig? binding) => binding != null && !IsCapturingKeyboard && !IsCapturingJoystick && IsGamepadConnected;
        [RelayCommand(CanExecute = nameof(CanCancelCapture))]
        private void CancelCapture()
        {
            var callStack = Environment.StackTrace;
            System.Diagnostics.Debug.WriteLine($"[SettingsVM CancelCapture] CALLED. IsCapturingKeyboard: {IsCapturingKeyboard}, IsCapturingJoystick: {IsCapturingJoystick}. Caller: {callStack.Replace("\r\n", " ").Substring(0, Math.Min(500, callStack.Length))}");
            bool wasCapturingKeyboard = IsCapturingKeyboard;
            bool wasCapturingJoystick = IsCapturingJoystick;
            IsCapturingKeyboard = false;
            IsCapturingJoystick = false;
            if (wasCapturingKeyboard || wasCapturingJoystick)
            {
                BindingToCapture = null;
                CaptureStatusText = null;
            }
            System.Diagnostics.Debug.WriteLine($"[SettingsVM CancelCapture] State after reset: IsCapturingKeyboard: {IsCapturingKeyboard}, IsCapturingJoystick: {IsCapturingJoystick}");
        }
        [RelayCommand]
        private void ResetKeyboardDefaults()
        {
            var defaults = _settingsService.GetDefaultSettings();
            foreach (var binding in Bindings)
            {
                bool isGlobal = binding.ActionId.StartsWith("GLOBAL_");
                var defaultDict = isGlobal ? defaults.GlobalKeyboardBindings : defaults.LocalKeyboardBindings;
                if (defaultDict.TryGetValue(binding.ActionId, out string? defaultKeyBinding)) { binding.KeyboardBindingDisplay = defaultKeyBinding; }
                else { binding.KeyboardBindingDisplay = null; }
            }
        }
        [RelayCommand(CanExecute = nameof(IsGamepadConnected))]
        private void ResetJoystickDefaults()
        {
            var defaults = _settingsService.GetDefaultSettings();
            foreach (var binding in Bindings)
            {
                bool isGlobal = binding.ActionId.StartsWith("GLOBAL_");
                var defaultDict = isGlobal ? defaults.GlobalJoystickBindings : defaults.LocalJoystickBindings;
                if (defaultDict.TryGetValue(binding.ActionId, out string? defaultJoyBinding)) { binding.JoystickBindingDisplay = defaultJoyBinding; }
                else { binding.JoystickBindingDisplay = null; }
            }
        }
        [RelayCommand]
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
        [RelayCommand]
        private void Cancel()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        public void UpdateKeyboardCaptureStatusForModifier()
        {
            if (IsCapturingKeyboard && BindingToCapture != null)
            {
                CaptureStatusText = $"Press the main key for '{BindingToCapture.ActionName}' (Modifiers detected)...";
            }
        }
        public void ApplyKeyboardCapture(string displayValue, object dataValue)
        {
            if (!IsCapturingKeyboard || BindingToCapture == null || string.IsNullOrWhiteSpace(displayValue)) return;
            foreach (var existingBinding in Bindings)
            {
                if (existingBinding != BindingToCapture &&
                    !string.IsNullOrEmpty(existingBinding.KeyboardBindingDisplay) &&
                    existingBinding.KeyboardBindingDisplay.Equals(displayValue, StringComparison.OrdinalIgnoreCase))
                {
                    existingBinding.KeyboardBindingDisplay = null;
                }
            }
            BindingToCapture.KeyboardBindingDisplay = displayValue;
            BindingToCapture.KeyboardBindingData = dataValue;
            CancelCapture();
        }
        public void ApplyJoystickCapture(string displayValue, object dataValue)
        {
            if (!IsCapturingJoystick || BindingToCapture == null) return;
            foreach (var existingBinding in Bindings)
            {
                if (existingBinding != BindingToCapture &&
                    !string.IsNullOrEmpty(existingBinding.JoystickBindingDisplay) &&
                    existingBinding.JoystickBindingDisplay.Equals(displayValue, StringComparison.OrdinalIgnoreCase))
                {
                    existingBinding.JoystickBindingDisplay = null;
                }
            }
            BindingToCapture.JoystickBindingDisplay = displayValue;
            BindingToCapture.JoystickBindingData = dataValue;
            CancelCapture();
        }
        partial void OnIsCapturingJoystickChanged(bool value)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsVM OnIsCapturingJoystickChanged] Value: {value}. Current _captureCts is null: {_captureCts == null}");
            if (!value)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsVM OnIsCapturingJoystickChanged] IsCapturingJoystick is FALSE. Calling StopJoystickPolling.");
                StopJoystickPolling();
            }
        }
        private void StartJoystickPolling()
        {
            StopJoystickPolling();
            _captureCts = new CancellationTokenSource();
            var token = _captureCts.Token;
            Task.Run(async () =>
            {
                System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] Joystick polling task ENTERED.");
                Gamepad? gamepad = null;
                int retries = 3;
                for (int i = 0; i < retries; i++)
                {
                    if (token.IsCancellationRequested) { System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] Token cancelled during gamepad GET RETRY loop."); break; }
                    try
                    {
                        if (_dispatcherQueue != null)
                        {
                            var tcs = new TaskCompletionSource<Gamepad?>();
                            _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                            {
                                try { tcs.SetResult(Windows.Gaming.Input.Gamepad.Gamepads.FirstOrDefault()); }
                                catch (Exception ex) { tcs.SetException(ex); }
                            });
                            gamepad = await tcs.Task;
                        }
                        else { gamepad = Windows.Gaming.Input.Gamepad.Gamepads.FirstOrDefault(); }
                        if (gamepad is object) break;
                        System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture] No gamepad on attempt {i + 1}. Delaying...");
                        await Task.Delay(150, token);
                    }
                    catch (OperationCanceledException) { System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] Gamepad fetch attempt task CANCELED."); break; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture] Exception getting gamepad list (attempt {i + 1}): {ex.Message}"); if (token.IsCancellationRequested) break; await Task.Delay(150, token); }
                }
                if (token.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] Polling task CANCELED before main loop.");
                    _dispatcherQueue?.TryEnqueue(() => { if (IsCapturingJoystick) CancelCapture(); });
                    return;
                }
                if (gamepad == null)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] No gamepad found. EXITING polling task.");
                    _dispatcherQueue?.TryEnqueue(() => CaptureStatusText = "No gamepad detected for capture.");
                    _dispatcherQueue?.TryEnqueue(() => { if (IsCapturingJoystick) CancelCapture(); });
                    return;
                }
                var rgCtrl = RawGameController.FromGameController(gamepad);
                System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture] Gamepad found: {rgCtrl?.DisplayName ?? "Unknown"}. Entering WHILE loop. Token IsCancellationRequested: {token.IsCancellationRequested}");
                GamepadReading previousReading = default;
                bool firstRead = true;
                int loopCount = 0;
                while (!token.IsCancellationRequested)
                {
                    loopCount++;
                    if (token.IsCancellationRequested) { System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture INNER LOOP #{loopCount}] Token IS CANCELED before reading."); break; }
                    try
                    {
                        var currentReading = gamepad.GetCurrentReading();
                        if (firstRead || currentReading.Buttons != previousReading.Buttons)
                        {
                            System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture POLL #{loopCount}] Timestamp: {currentReading.Timestamp}, Buttons: {currentReading.Buttons}");
                            firstRead = false;
                        }
                        GamepadButtons justPressed = (currentReading.Buttons ^ previousReading.Buttons) & currentReading.Buttons;
                        if (justPressed != GamepadButtons.None)
                        {
                            GamepadButtons buttonToApplyOnce = GetFirstPressedButton(justPressed);
                            if (buttonToApplyOnce != GamepadButtons.None)
                            {
                                string? internalCodeOnce = MapGamepadButtonToInternalCode(buttonToApplyOnce);
                                if (internalCodeOnce != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture #{loopCount}] Joystick button DETECTED FOR CAPTURE: {internalCodeOnce} (Enum: {buttonToApplyOnce})");
                                    _dispatcherQueue?.TryEnqueue(() => { if (IsCapturingJoystick) { System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture DISPATCH #{loopCount}] Applying capture for: {internalCodeOnce}"); ApplyJoystickCapture(internalCodeOnce, buttonToApplyOnce); } });
                                }
                            }
                        }
                        previousReading = currentReading;
                        if (token.IsCancellationRequested) { System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture INNER LOOP #{loopCount}] Token IS CANCELED after processing, before delay."); break; }
                        await Task.Delay(30, token);
                    }
                    catch (OperationCanceledException) { System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture INNER LOOP #{loopCount}] Polling CANCELED during read/delay (TaskCanceledException)."); break; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture INNER LOOP #{loopCount}] Error: {ex.Message}"); _dispatcherQueue?.TryEnqueue(() => { if (IsCapturingJoystick) CancelCapture(); }); break; }
                    if (loopCount > 300)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture INNER LOOP #{loopCount}] Loop count limit reached. Exiting.");
                        _dispatcherQueue?.TryEnqueue(() => { if (IsCapturingJoystick) CancelCapture(); });
                        break;
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[SettingsVM Capture] Joystick polling task WHILE LOOP EXITED. LoopCount: {loopCount}, Token IsCancellationRequested: {token.IsCancellationRequested}");
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    if (IsCapturingJoystick)
                    {
                        System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] Polling loop ended, but IsCapturingJoystick still true. Forcing CancelCapture.");
                        CancelCapture();
                    }
                });
            }, token);
            System.Diagnostics.Debug.WriteLine("[SettingsVM Capture] Task.Run for joystick polling has been LAUNCHED.");
        }
        private void StopJoystickPolling()
        {
            var stackTrace = new System.Diagnostics.StackTrace();
            System.Diagnostics.Debug.WriteLine($"[SettingsVM StopJoystickPolling] CALLED. StackTrace: {stackTrace.ToString().Replace("\r\n", " ").Substring(0, Math.Min(300, stackTrace.ToString().Length))}");
            try
            {
                if (_captureCts != null && !_captureCts.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsVM StopJoystickPolling] Cancelling CancellationTokenSource.");
                    _captureCts.Cancel();
                }
                _captureCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsVM StopJoystickPolling] CancellationTokenSource already disposed.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsVM StopJoystickPolling] Exception during CancellationToken management: {ex.Message}");
            }
            _captureCts = null;
            System.Diagnostics.Debug.WriteLine("[SettingsVM StopJoystickPolling] CancellationToken processed and nulled.");
        }
        private GamepadButtons GetFirstPressedButton(GamepadButtons buttons) { foreach (GamepadButtons flag in (GamepadButtons[])Enum.GetValues(typeof(GamepadButtons))) { if (flag != GamepadButtons.None && buttons.HasFlag(flag)) { return flag; } } return GamepadButtons.None; }
        private string? MapGamepadButtonToInternalCode(object dataValue)
        {
            if (dataValue is GamepadButtons button)
            {
                return button switch
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
                };
            }
            return null;
        }
        private void PopulateBindingsCollection()
        {
            Bindings.Clear();
            Bindings.Add(new BindingConfig("LOCAL_NAV_UP", "Navigate Up"));
            Bindings.Add(new BindingConfig("LOCAL_NAV_DOWN", "Navigate Down"));
            Bindings.Add(new BindingConfig("LOCAL_NAV_LEFT", "Navigate Left"));
            Bindings.Add(new BindingConfig("LOCAL_NAV_RIGHT", "Navigate Right"));
            Bindings.Add(new BindingConfig("LOCAL_ACCEPT", "Accept / Enter"));
            Bindings.Add(new BindingConfig("LOCAL_CANCEL", "Cancel / Back"));
            Bindings.Add(new BindingConfig("LOCAL_CONTEXT_MENU", "Open Context Menu"));
            Bindings.Add(new BindingConfig("LOCAL_SAVE_WORD", "Save Word Shortcut"));
            Bindings.Add(new BindingConfig("LOCAL_NAV_FAST_PAGE_UP", "Fast Nav - Page Up/Start"));
            Bindings.Add(new BindingConfig("LOCAL_NAV_FAST_PAGE_DOWN", "Fast Nav - Page Down/End"));
            Bindings.Add(new BindingConfig("LOCAL_SETTINGS_HUB", "Open Settings (from Hub)"));
            Bindings.Add(new BindingConfig("LOCAL_ANKI_LINK", "Send to Anki (Shortcut)"));
            Bindings.Add(new BindingConfig("GLOBAL_TOGGLE", "Toggle Window (Global)"));
            Bindings.Add(new BindingConfig("GLOBAL_MENU", "Focus Menu/Hub (Global)"));
        }
        private void ApplyLoadedBindings()
        {
            foreach (var binding in Bindings)
            {
                bool isGlobal = binding.ActionId.StartsWith("GLOBAL_");
                string? joyBinding = null;
                string? keyBinding = null;
                var joySourceDict = isGlobal ? _loadedSettings.GlobalJoystickBindings : _loadedSettings.LocalJoystickBindings;
                var keySourceDict = isGlobal ? _loadedSettings.GlobalKeyboardBindings : _loadedSettings.LocalKeyboardBindings;
                joySourceDict?.TryGetValue(binding.ActionId, out joyBinding);
                keySourceDict?.TryGetValue(binding.ActionId, out keyBinding);
                binding.JoystickBindingDisplay = joyBinding;
                binding.KeyboardBindingDisplay = keyBinding;
            }
        }
        public void UpdateGamepadStatus()
        {
            bool currentlyConnected = false;
            string detectedNameStr = "No Gamepad Connected";
            try
            {
                var gamepads = Windows.Gaming.Input.Gamepad.Gamepads;
                currentlyConnected = gamepads.Any();
                if (currentlyConnected)
                {
                    Windows.Gaming.Input.Gamepad? firstGamepad = gamepads.FirstOrDefault();
                    if (firstGamepad is object)
                    {
                        var rawCtrl = RawGameController.FromGameController(firstGamepad);
                        detectedNameStr = rawCtrl?.DisplayName ?? "Connected Gamepad";
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SettingsVM UpdateGamepadStatus] Error: {ex.Message}"); detectedNameStr = "Error detecting gamepad"; }
            IsGamepadConnected = currentlyConnected;
            DetectedGamepadName = detectedNameStr;
        }
        private void OnGamepadConnectionChanged(object? sender, Windows.Gaming.Input.Gamepad e)
        {
            _dispatcherQueue?.TryEnqueue(UpdateGamepadStatus);
        }
        public void CleanupOnClose()
        {
            StopJoystickPolling();
            Windows.Gaming.Input.Gamepad.GamepadAdded -= OnGamepadConnectionChanged;
            Windows.Gaming.Input.Gamepad.GamepadRemoved -= OnGamepadConnectionChanged;
            System.Diagnostics.Debug.WriteLine("[SettingsVM] CleanupOnClose completed.");
        }
    }
}