using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using Microsoft.UI.Dispatching;
namespace JpnStudyTool.Services
{
    public class GlobalHotkeyService : IDisposable
    {
        private const int XBOX_VID = 0x045E;
        private const int XBOX_PID = 0x028E;
        private const int NINTENDO_VID = 0x057E;
        private const int PRO_CONTROLLER_PID_USB = 0x2009;
        private byte[]? _previousRawReportForDiff = null;

        private static readonly Dictionary<string, (int byteIndex, byte bitMask)> ButtonMap_ProController_Report3F = new()
        {
            { "BTN_Y",      (1, 0x01) },
            { "BTN_X",      (1, 0x02) },
            { "BTN_B",      (1, 0x04) },
            { "BTN_A",      (1, 0x08) },
            { "BTN_TR",     (1, 0x10) },
            { "BTN_TL",     (1, 0x20) },
            { "BTN_ZR",     (1, 0x40) },
            { "BTN_ZL",     (1, 0x80) },
            { "BTN_SELECT", (2, 0x01) },
            { "BTN_START",  (2, 0x02) },
            { "BTN_THUMBL", (2, 0x04) },
            { "BTN_THUMBR", (2, 0x08) },
            { "BTN_MODE",   (2, 0x10) },
            { "BTN_CAPTURE",(2, 0x20) },
                    };
        private const int DPadByteIndex_ProController_Report3F = 3;
        private static readonly Dictionary<string, byte> DPadValues_ProController_Report3F = new()
        {
            { "DPAD_UP",         0x00 }, { "DPAD_UP_RIGHT",   0x01 },
            { "DPAD_RIGHT",      0x02 }, { "DPAD_DOWN_RIGHT", 0x03 },
            { "DPAD_DOWN",       0x04 }, { "DPAD_DOWN_LEFT",  0x05 },
            { "DPAD_LEFT",       0x06 }, { "DPAD_UP_LEFT",    0x07 },
                    };
        private static readonly Dictionary<string, (int byteIndex, byte bitMask)> ButtonMap_ProController_Report30 = new()
        {
                        { "BTN_Y",      (3, 0x01) },
            { "BTN_X",      (3, 0x02) },
            { "BTN_B",      (3, 0x04) },
            { "BTN_A",      (3, 0x08) },
            { "BTN_TR",     (3, 0x40) },
            { "BTN_ZR",     (3, 0x80) },

            { "BTN_SELECT", (4, 0x01) },
            { "BTN_START",  (4, 0x02) },
            { "BTN_THUMBR", (4, 0x04) },
            { "BTN_THUMBL", (4, 0x08) },
            { "BTN_MODE",   (4, 0x10) },
            { "BTN_CAPTURE",(4, 0x20) },

            { "DPAD_DOWN",  (5, 0x01) },
            { "DPAD_UP",    (5, 0x02) },
            { "DPAD_RIGHT", (5, 0x04) },
            { "DPAD_LEFT",  (5, 0x08) },
            { "BTN_TL",     (5, 0x40) },
            { "BTN_ZL",     (5, 0x80) },
        };

        private readonly int _targetVid;
        private readonly int _targetPid;
        private readonly string? _toggleButtonCode;
        private readonly string? _menuButtonCode;
        private readonly DispatcherQueue? _uiDispatcherQueue;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
        private bool _wasTogglePressed = false;
        private bool _wasMenuPressed = false;
        private enum ControllerType { Unknown, Xbox, ProController }
        private ControllerType _activeControllerType = ControllerType.Unknown;
        private ManualResetEventSlim _pauseEvent = new(true);
        private bool _isPaused = false;
        private bool disposedValue;
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
            System.Diagnostics.Debug.WriteLine($"[GHS Initialized] Target VID: {_targetVid:X4}, PID: {_targetPid:X4}, Toggle: '{_toggleButtonCode ?? "N"}', Menu: '{_menuButtonCode ?? "N"}'");
        }
        public void StartListener()
        {
            if (_listenerTask != null && !_listenerTask.IsCompleted) return;
            _cts = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenerLoop(_cts.Token), _cts.Token);
            System.Diagnostics.Debug.WriteLine("[GHS Listener task started]");
        }
        public void StopListener()
        {
            System.Diagnostics.Debug.WriteLine("[GHS Requesting listener stop]");
            _cts?.Cancel();
        }
        public void PauseListener() { if (!_isPaused) { _pauseEvent.Reset(); _isPaused = true; System.Diagnostics.Debug.WriteLine("[GHS Listener paused]"); } }
        public void ResumeListener() { if (_isPaused) { _pauseEvent.Set(); _isPaused = false; _wasTogglePressed = false; _wasMenuPressed = false; System.Diagnostics.Debug.WriteLine("[GHS Listener resumed]"); } }

        private async Task ListenerLoop(CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] Loop STARTING...");
            HidDevice? device = null;
            HidStream? stream = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    _pauseEvent.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] Wait on pauseEvent canceled.");
                    break;
                }
                if (token.IsCancellationRequested) break;
                try
                {
                    if (stream == null)
                    {
                        CloseStreamAndDevice(ref stream, ref device);

                        await Task.Delay(1500, token);
                        if (token.IsCancellationRequested) break;
                        device = FindDevice();
                        if (device != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] Device found: {device.GetProductName()} (VID:{device.VendorID:X4}, PID:{device.ProductID:X4})");
                            DetermineActiveControllerType(device);
                            if (_activeControllerType != ControllerType.Unknown)
                            {
                                try
                                {
                                    if (device.TryOpen(out stream))
                                    {
                                        stream.ReadTimeout = Timeout.Infinite;
                                        System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] HID Stream OPENED (using TryOpen) for {device.GetProductName()}. ReadTimeout: Infinite.");
                                        _wasTogglePressed = false; _wasMenuPressed = false;
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] device.TryOpen() FAILED for {device.GetProductName()}.");
                                        stream = null;
                                    }
                                }
                                catch (Exception openEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] Exception during device.TryOpen(): {openEx.Message}");
                                    CloseStreamAndDevice(ref stream, ref device);
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] Device type unknown, not opening stream.");
                                device = null;
                            }
                        }
                        else
                        {

                        }
                        if (stream == null)
                        {
                            await Task.Delay(2500, token);
                            continue;
                        }
                    }
                    byte[] inputReport = new byte[device!.GetMaxInputReportLength()];
                    int bytesRead = 0;
                    try
                    {
                        bytesRead = await stream.ReadAsync(inputReport, 0, inputReport.Length, token);
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] ReadAsync was CANCELED.");
                        if (token.IsCancellationRequested) break;
                        CloseStreamAndDevice(ref stream, ref device);
                        continue;
                    }
                    catch (IOException ioEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] ReadAsync IOException: {ioEx.Message}. Closing stream.");
                        CloseStreamAndDevice(ref stream, ref device);
                        continue;
                    }
                    catch (TimeoutException)
                    {
                        System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] ReadAsync TIMED OUT. Closing stream.");
                        CloseStreamAndDevice(ref stream, ref device);
                        continue;
                    }
                    catch (Exception readEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] ReadAsync UNEXPECTED Exception: {readEx.Message}. Closing stream.");
                        CloseStreamAndDevice(ref stream, ref device);
                        continue;
                    }
                    if (bytesRead > 0)
                    {
                        var currentRawReport = inputReport.Take(bytesRead).ToArray();

                        if (currentRawReport.Length > 0 && currentRawReport[0] == 0x30)
                        {
                            string byte1Hex = (currentRawReport.Length > 1) ? $"{currentRawReport[1]:X2}" : "N/A";
                            string byte2Hex = (currentRawReport.Length > 2) ? $"{currentRawReport[2]:X2}" : "N/A";
                            string byte3Hex = (currentRawReport.Length > 3) ? $"{currentRawReport[3]:X2}" : "N/A";
                            string byte4Hex = (currentRawReport.Length > 4) ? $"{currentRawReport[4]:X2}" : "N/A";

                            string byte5Hex = (currentRawReport.Length > 5) ? $"{currentRawReport[5]:X2}" : "N/A";
                            string byte6Hex = (currentRawReport.Length > 6) ? $"{currentRawReport[6]:X2}" : "N/A";
                            string byte7Hex = (currentRawReport.Length > 7) ? $"{currentRawReport[7]:X2}" : "N/A";
                            string byte8Hex = (currentRawReport.Length > 8) ? $"{currentRawReport[8]:X2}" : "N/A";
                            string byte9Hex = (currentRawReport.Length > 9) ? $"{currentRawReport[9]:X2}" : "N/A";
                            string byte10Hex = (currentRawReport.Length > 10) ? $"{currentRawReport[10]:X2}" : "N/A";
                        }

                        ProcessInputReport(currentRawReport);
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] ReadAsync returned 0 bytes (and not canceled). Possible disconnect. Closing stream.");
                        CloseStreamAndDevice(ref stream, ref device);
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] Main loop OperationCanceled. Exiting.");
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GHS ListenerLoop] UNEXPECTED Error in main loop: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
                    CloseStreamAndDevice(ref stream, ref device);
                    if (!token.IsCancellationRequested) await Task.Delay(3000, token);
                }
            }
            CloseStreamAndDevice(ref stream, ref device);
            System.Diagnostics.Debug.WriteLine("[GHS ListenerLoop] Loop FINISHED.");
        }

        private HidDevice? FindDevice()
        {
            try { return DeviceList.Local.GetHidDeviceOrNull(vendorID: _targetVid, productID: _targetPid); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GHS FindDevice] Error: {ex.Message}"); return null; }
        }
        private void DetermineActiveControllerType(HidDevice device)
        {
            if (device.VendorID == XBOX_VID && device.ProductID == XBOX_PID) _activeControllerType = ControllerType.Xbox;
            else if (device.VendorID == NINTENDO_VID && device.ProductID == PRO_CONTROLLER_PID_USB) _activeControllerType = ControllerType.ProController;
            else _activeControllerType = ControllerType.Unknown;
            System.Diagnostics.Debug.WriteLine($"[GHS DeterminedType] Type: {_activeControllerType}");
        }
        private void ProcessInputReport(byte[] report)
        {

            bool isToggleCurrentlyPressed = IsTargetButtonPressed(report, _toggleButtonCode);
            bool isMenuCurrentlyPressed = IsTargetButtonPressed(report, _menuButtonCode);

            if (!isToggleCurrentlyPressed && _wasTogglePressed)
            {
                System.Diagnostics.Debug.WriteLine($"[GHS] Hotkey '{_toggleButtonCode}' RELEASED. Triggering action.");
                TriggerAction(App.RequestToggleWindow);
            }
            if (!isMenuCurrentlyPressed && _wasMenuPressed)
            {
                System.Diagnostics.Debug.WriteLine($"[GHS] Hotkey '{_menuButtonCode}' RELEASED. Triggering action.");
                TriggerAction(App.RequestToggleMenu);
            }
            _wasTogglePressed = isToggleCurrentlyPressed;
            _wasMenuPressed = isMenuCurrentlyPressed;
        }
        private bool IsTargetButtonPressed(byte[] report, string? buttonCode)
        {
            if (string.IsNullOrEmpty(buttonCode) || _activeControllerType == ControllerType.Unknown || report == null || report.Length == 0) return false;
            return _activeControllerType switch
            {
                ControllerType.ProController => IsButtonPressed_ProController(report, buttonCode),
                _ => false,
            };
        }
        private bool IsButtonPressed_ProController(byte[] report, string buttonCode)
        {
            if (report.Length == 0) return false;
            byte reportId = report[0];
            if (reportId == 0x30)
            {
                if (ButtonMap_ProController_Report30.TryGetValue(buttonCode, out var binding_30))
                {
                    if (binding_30.byteIndex > 0 && binding_30.byteIndex < report.Length)
                    {
                        return (report[binding_30.byteIndex] & binding_30.bitMask) != 0;
                    }
                }
            }
            else if (reportId == 0x3F || reportId == 0x21)
            {
                if (DPadValues_ProController_Report3F.TryGetValue(buttonCode, out byte dpadValueToTest_3F))
                {
                    if (DPadByteIndex_ProController_Report3F > 0 && DPadByteIndex_ProController_Report3F < report.Length)
                    {
                        return report[DPadByteIndex_ProController_Report3F] == dpadValueToTest_3F;
                    }
                }
                else if (ButtonMap_ProController_Report3F.TryGetValue(buttonCode, out var binding_3F))
                {
                    if (binding_3F.byteIndex > 0 && binding_3F.byteIndex < report.Length)
                    {
                        return (report[binding_3F.byteIndex] & binding_3F.bitMask) != 0;
                    }
                }
            }
            return false;
        }

        private void TriggerAction(Action? triggerAction)
        {
            if (triggerAction == null || _uiDispatcherQueue == null) return;
            if (!_uiDispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => triggerAction.Invoke()))
            { System.Diagnostics.Debug.WriteLine($"[GHS TriggerAction] TryEnqueue FAILED."); }
        }
        private void CloseStreamAndDevice(ref HidStream? stream, ref HidDevice? device)
        {
            try { stream?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GHS Error disposing stream: {ex.Message}"); }
            stream = null; device = null; _activeControllerType = ControllerType.Unknown;
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_cts != null && !_cts.IsCancellationRequested) _cts.Cancel();
                    _pauseEvent?.Set();
                    try { _listenerTask?.Wait(TimeSpan.FromMilliseconds(200)); } catch { }
                    _cts?.Dispose(); _cts = null;
                    _pauseEvent?.Dispose(); _pauseEvent = null;
                    _listenerTask = null;
                }
                disposedValue = true;
                System.Diagnostics.Debug.WriteLine("[GHS Disposed]");
            }
        }
        public void Dispose() { Dispose(disposing: true); GC.SuppressFinalize(this); }
    }
}