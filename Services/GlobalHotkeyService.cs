// Services/GlobalHotkeyService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using Microsoft.UI.Dispatching;

namespace JpnStudyTool.Services;

public class GlobalHotkeyService : IDisposable
{
    // --- Knowed IDs ---
    private const int XBOX_VID = 0x045E;
    private const int XBOX_PID = 0x028E; // 360 Controller / Virtual XInput

    private const int NINTENDO_VID = 0x057E;
    private const int PRO_CONTROLLER_PID_USB = 0x2009;

    // --- Button Map ---
    // Xbox (360 / Virtual XInput)
    private static readonly Dictionary<string, (int byteIndex, byte bitMask)> ButtonMap_Xbox = new()
    {
        { "DPAD_UP",        (2, 0x01) }, { "DPAD_DOWN",      (2, 0x02) },
        { "DPAD_LEFT",      (2, 0x04) }, { "DPAD_RIGHT",     (2, 0x08) },
        { "BTN_START",      (2, 0x10) }, { "BTN_SELECT",     (2, 0x20) },
        { "BTN_THUMBL",     (2, 0x40) }, { "BTN_THUMBR",     (2, 0x80) },
        { "BTN_TL",         (3, 0x01) }, { "BTN_TR",         (3, 0x02) },
        { "BTN_MODE",       (3, 0x04) }, { "BTN_SOUTH",      (3, 0x10) }, // A
        { "BTN_EAST",       (3, 0x20) }, // B
        { "BTN_WEST",       (3, 0x40) }, // X
        { "BTN_NORTH",      (3, 0x80) }, // Y
    };

    // Pro Controller (Nintendo Switch)
    private static readonly Dictionary<string, (int byteIndex, byte bitMask)> ButtonMap_ProController_Report3F = new()
    {
        // Byte 1
        { "BTN_B", (1, 0x01) }, { "BTN_A", (1, 0x02) }, { "BTN_Y", (1, 0x04) }, { "BTN_X", (1, 0x08) },
        { "BTN_TL", (1, 0x10) }, { "BTN_TR", (1, 0x20) }, { "BTN_ZL", (1, 0x40) }, { "BTN_ZR", (1, 0x80) },

        // Byte 2
        { "BTN_SELECT",    (2, 0x01) }, // Minus
        { "BTN_START",     (2, 0x02) }, // Plus
        { "BTN_THUMBL",    (2, 0x04) }, // L Stick Click (L3)
        { "BTN_THUMBR",    (2, 0x08) }, // R Stick Click (R3)
        { "BTN_MODE",      (2, 0x10) }, // Home Button
        { "BTN_CAPTURE",   (2, 0x20) }, // Capture Button
    };
    // Special map for d-pad (pro controller)
    private const int DPadByteIndex_ProController_Report3F = 3;
    private const byte DPadNeutral_ProController_Report3F = 0x08;
    private static readonly Dictionary<string, byte> DPadValues_ProController_Report3F = new()
    {
        { "DPAD_UP", 0x00 }, { "DPAD_UP_RIGHT", 0x01 }, { "DPAD_RIGHT", 0x02 }, { "DPAD_DOWN_RIGHT", 0x03 },
        { "DPAD_DOWN", 0x04 }, { "DPAD_DOWN_LEFT", 0x05 }, { "DPAD_LEFT", 0x06 }, { "DPAD_UP_LEFT", 0x07 },
    };

    private readonly int _targetVid;
    private readonly int _targetPid;
    private readonly string? _toggleButtonCode;
    private readonly string? _menuButtonCode;
    private readonly DispatcherQueue? _uiDispatcherQueue;

    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    // On release logic
    private bool _wasTogglePressed = false;
    private bool _wasMenuPressed = false;

    private enum ControllerType { Unknown, Xbox, ProController }
    private ControllerType _activeControllerType = ControllerType.Unknown;


    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private bool _isPaused = false;

    public GlobalHotkeyService(DispatcherQueue? uiDispatcherQueue,
                               int targetVendorId, int targetProductId,
                               string? configuredToggleButtonCode,
                               string? configuredMenuButtonCode)
    {
        _uiDispatcherQueue = uiDispatcherQueue;
        _targetVid = targetVendorId;
        _targetPid = targetProductId;
        _toggleButtonCode = configuredToggleButtonCode;
        _menuButtonCode = configuredMenuButtonCode;

        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Initialized. Target VID: {_targetVid:X4}, PID: {_targetPid:X4}");
        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Toggle Button: '{_toggleButtonCode ?? "None"}'");
        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Menu Button: '{_menuButtonCode ?? "None"}'");
    }

    public void StartListener()
    {
        if (_listenerTask != null && !_listenerTask.IsCompleted)
        {
            System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Listener already running.");
            return;
        }
        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenerLoop(_cts.Token), _cts.Token);
        System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Listener task started.");
    }

    public void StopListener()
    {
        System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Requesting listener stop...");
        _cts?.Cancel();
    }

    public void PauseListener()
    {
        if (!_isPaused)
        {
            _pauseEvent.Reset();
            _isPaused = true;
            System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Listener paused.");
        }
    }

    public void ResumeListener()
    {
        if (_isPaused)
        {
            _pauseEvent.Set();
            _isPaused = false;
            _wasTogglePressed = false;
            _wasMenuPressed = false;
            _pauseEvent.Set();
            System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Listener resumed and state reset.");
        }
    }

    private async Task ListenerLoop(CancellationToken token)
    {
        System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Listener loop running...");
        HidDevice? device = null;
        HidStream? stream = null;
        byte[]? lastInputReport = null;
        _activeControllerType = ControllerType.Unknown;
        _wasTogglePressed = false;
        _wasMenuPressed = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                _pauseEvent.Wait(token);
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Operation canceled while waiting for pause event.");
                break;
            }

            if (token.IsCancellationRequested) break;

            try
            {
                // --- 1: Find and open device ---
                if (stream == null)
                {
                    CloseStreamAndDevice(ref stream, ref device);
                    lastInputReport = null;

                    if (token.IsCancellationRequested) break;
                    await Task.Delay(1500, token);

                    device = FindDevice();

                    if (device != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Checking device properties - VID: {device.VendorID:X4}, PID: {device.ProductID:X4}");
                        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Comparing with Target - VID: {_targetVid:X4}, PID: {_targetPid:X4}");
                        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Comparing with Xbox    - VID: {XBOX_VID:X4}, PID: {XBOX_PID:X4}");
                        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Comparing with ProCon   - VID: {NINTENDO_VID:X4}, PID: {PRO_CONTROLLER_PID_USB:X4}");


                        if (device.VendorID == XBOX_VID && device.ProductID == XBOX_PID)
                        {
                            _activeControllerType = ControllerType.Xbox;
                        }
                        else if (device.VendorID == NINTENDO_VID &&
                                 (device.ProductID == PRO_CONTROLLER_PID_USB))
                        {
                            _activeControllerType = ControllerType.ProController;
                        }
                        else
                        {
                            _activeControllerType = ControllerType.Unknown;
                        }
                        System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Found device: {device.GetProductName()}. Type determined as: {_activeControllerType}");

                        if (_activeControllerType != ControllerType.Unknown)
                        {
                            try
                            {
                                if (device.TryOpen(out stream))
                                {
                                    stream.ReadTimeout = Timeout.Infinite;
                                    System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] HID Stream opened for {device.GetProductName()}");
                                    lastInputReport = null;
                                    _wasTogglePressed = false;
                                    _wasMenuPressed = false;
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Failed to open stream for {device.GetProductName()}");
                                    stream = null; device = null; _activeControllerType = ControllerType.Unknown;
                                }
                            }
                            catch (Exception openEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Error opening HID stream: {openEx.Message}");
                                stream = null; device = null; _activeControllerType = ControllerType.Unknown;
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Device type not directly supported.");
                            device = null;
                        }
                    }

                    if (stream == null)
                    {
                        if (token.IsCancellationRequested) break;
                        await Task.Delay(2500, token);
                        continue;
                    }
                }

                // --- 2: Read the stream ---
                if (stream == null || device == null || !stream.CanRead)
                {
                    System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Stream became invalid unexpectedly. Resetting...");
                    CloseStreamAndDevice(ref stream, ref device);
                    continue;
                }

                byte[] inputReport = new byte[device.GetMaxInputReportLength()];
                int bytesRead = 0;

                if (stream == null)
                {
                    System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Stream became null just before Task.Run. Resetting...");
                    CloseStreamAndDevice(ref stream, ref device);
                    continue;
                }

                var readTask = Task.Run(() => stream.Read(inputReport), token);
                var delayTask = Task.Delay(Timeout.Infinite, token);
                var completedTask = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

                if (completedTask == delayTask)
                {
                    token.ThrowIfCancellationRequested();
                }
                else
                {
                    bytesRead = await readTask.ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] HID Read returned 0 bytes. Assuming disconnection.");
                        CloseStreamAndDevice(ref stream, ref device);
                        continue;
                    }
                }

                // --- 3: Proccess valid input---
                if (bytesRead > 0)
                {
                    DateTime now = DateTime.UtcNow;
                    var currentReport = inputReport.Take(bytesRead).ToArray();

                    if (lastInputReport == null || !currentReport.SequenceEqual(lastInputReport))
                    {
                        ProcessInputReport(currentReport);
                        lastInputReport = currentReport.ToArray();
                    }
                }
            }
            catch (IOException ioEx)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] IOException in loop (likely disconnect): {ioEx.Message}");
                CloseStreamAndDevice(ref stream, ref device);

                if (token.IsCancellationRequested)
                {
                    // Exit the loop if was cancelled while catching the error
                    System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Cancellation requested during IOException handling.");
                    break;
                }
            }
            catch (InvalidOperationException opEx) // Can occur if stream is closed or disposed while reading it
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] InvalidOperationException in loop (stream closed?): {opEx.Message}");
                CloseStreamAndDevice(ref stream, ref device);
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Operation canceled. Exiting listener loop.");
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Unexpected Error in ListenerLoop: {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                CloseStreamAndDevice(ref stream, ref device);
                if (!token.IsCancellationRequested)
                {
                    await Task.Delay(5000, token);
                }
            }
        }

        CloseStreamAndDevice(ref stream, ref device);
        System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Listener loop finished.");
    }

    private HidDevice? FindDevice()
    {
        try
        {
            var device = DeviceList.Local.GetHidDeviceOrNull(vendorID: _targetVid, productID: _targetPid);


            if (device == null)
            {
                // System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Target device VID={_targetVid:X4} PID={_targetPid:X4} not found.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] FindDevice found: {device.GetProductName()} (VID={device.VendorID:X4}, PID={device.ProductID:X4})");
            }
            return device;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Error enumerating HID devices: {ex.Message}");
            return null;
        }
    }

    private void ProcessInputReport(byte[] report)
    {
        bool isToggleCurrentlyPressed = IsTargetButtonPressed(report, _toggleButtonCode);
        bool isMenuCurrentlyPressed = IsTargetButtonPressed(report, _menuButtonCode);

        if (!isToggleCurrentlyPressed && _wasTogglePressed)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Hotkey '{_toggleButtonCode}' RELEASED.");
            TriggerAction(_toggleButtonCode, App.RequestToggleWindow);
        }

        if (!isMenuCurrentlyPressed && _wasMenuPressed)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Hotkey '{_menuButtonCode}' RELEASED.");
            TriggerAction(_menuButtonCode, App.RequestToggleMenu);
        }

        _wasTogglePressed = isToggleCurrentlyPressed;
        _wasMenuPressed = isMenuCurrentlyPressed;
    }

    private bool IsTargetButtonPressed(byte[] report, string? buttonCode)
    {
        if (string.IsNullOrEmpty(buttonCode) || _activeControllerType == ControllerType.Unknown || report == null || report.Length == 0)
        {
            return false;
        }

        return _activeControllerType switch
        {
            ControllerType.Xbox => IsButtonPressed_Xbox(report, buttonCode),
            ControllerType.ProController => IsButtonPressed_ProController(report, buttonCode),
            _ => false,
        };
    }

    private bool IsButtonPressed_Xbox(byte[] report, string buttonCode)
    {
        if (ButtonMap_Xbox.TryGetValue(buttonCode, out var binding))
        {
            if (binding.byteIndex < report.Length)
            {
                return (report[binding.byteIndex] & binding.bitMask) != 0;
            }
        }
        return false;
    }

    private bool IsButtonPressed_ProController(byte[] report, string buttonCode)
    {
        if (report[0] != 0x3F) return false;

        if (DPadValues_ProController_Report3F.TryGetValue(buttonCode, out byte dpadValue))
        {
            if (DPadByteIndex_ProController_Report3F < report.Length)
            {
                return report[DPadByteIndex_ProController_Report3F] == dpadValue;
            }
        }
        else if (ButtonMap_ProController_Report3F.TryGetValue(buttonCode, out var binding))
        {
            if (binding.byteIndex < report.Length)
            {
                return (report[binding.byteIndex] & binding.bitMask) != 0;
            }
        }
        return false;
    }

    private void TriggerAction(string? buttonCode, Action? triggerAction)
    {
        if (triggerAction == null || string.IsNullOrEmpty(buttonCode)) return;

        if (_uiDispatcherQueue == null)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Error: uiDispatcherQueue is NULL! Cannot trigger action for {buttonCode}.");
            return;
        }

        bool success = _uiDispatcherQueue.TryEnqueue(() =>
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Executing action for '{buttonCode}' (on release) on UI thread...");
            try { triggerAction.Invoke(); System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Action for '{buttonCode}' invoked successfully."); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Exception during invoked action for '{buttonCode}': {ex.Message}"); }
        });

        if (!success) { System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] TryEnqueue for '{buttonCode}' FAILED."); }
    }

    private void CloseStreamAndDevice(ref HidStream? stream, ref HidDevice? device)
    {
        if (stream != null || device != null)
        {
            System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Closing stream and device...");
        }
        try { stream?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] Error disposing stream: {ex.Message}"); }
        stream = null;
        device = null;
        _activeControllerType = ControllerType.Unknown;
        _wasTogglePressed = false;
        _wasMenuPressed = false;
        if (stream != null || device != null)
        {
            // System.Diagnostics.Debug.WriteLine("[GlobalHotkeyService] Stream and device closed.");
        }
    }

    // Dispose pattern
    private bool disposedValue;
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                StopListener();
                _pauseEvent?.Set();
                try { _listenerTask?.Wait(500); } catch { }
                _cts?.Dispose();
                _pauseEvent?.Dispose();
            }
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}