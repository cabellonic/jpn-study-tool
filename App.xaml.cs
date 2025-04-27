// App.xaml.cs
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using WinRT.Interop;

namespace JpnStudyTool;

public partial class App : Application
{
    public static MainWindow? MainWin = null;
    private static HWND _mainWndHwnd = default;
    private static GlobalHotkeyService? _hotkeyService;
    internal static KeyboardHotkeyService? _keyboardHotkeyService;
    private static DispatcherQueue? _mainDispatcherQueue;
    private static SettingsService? _settingsService;

    private static SUBCLASSPROC? _appSubclassProcDelegate;
    private const uint APP_SUBCLASS_ID = 101;
    private static bool _isSubclassed = false;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _mainDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = new SettingsService();

        var mainWindowInstance = new MainWindow();

        HWND hwnd = default;

        try
        {
            hwnd = (HWND)WindowNative.GetWindowHandle(mainWindowInstance);
            if (hwnd == HWND.Null) throw new Exception("GetWindowHandle returned NULL");
            _mainWndHwnd = hwnd;
            System.Diagnostics.Debug.WriteLine($"[App] MainWindow HWND acquired and stored: {_mainWndHwnd}.");
            SetupWindowSubclassing(_mainWndHwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] CRITICAL ERROR getting HWND or subclassing. Error: {ex.Message}. Global keyboard hotkeys may fail.");
            _mainWndHwnd = default;
        }

        AppSettings currentSettings = _settingsService.LoadSettings();

        System.Diagnostics.Debug.WriteLine("[App] Passing initial settings to MainWindow instance...");
        mainWindowInstance.UpdateLocalBindings(currentSettings);

        ReloadAndRestartGlobalServices(_mainWndHwnd, currentSettings);

        MainWin = mainWindowInstance;
        MainWin.Activate();

        System.Diagnostics.Debug.WriteLine("[App] OnLaunched sequence complete.");
    }

    private static void SetupWindowSubclassing(HWND hwnd)
    {
        if (_isSubclassed || hwnd == HWND.Null) return;

        _appSubclassProcDelegate = new SUBCLASSPROC(AppWindowSubclassProc);
        bool success = PInvoke.SetWindowSubclass(hwnd, _appSubclassProcDelegate, APP_SUBCLASS_ID, nuint.Zero);

        if (success)
        {
            _isSubclassed = true;
            System.Diagnostics.Debug.WriteLine($"[App] Window subclass set successfully for HWND {hwnd}.");
        }
        else
        {
            int error = Marshal.GetLastPInvokeError();
            System.Diagnostics.Debug.WriteLine($"[App] Failed to set window subclass. Error: {error}");
            _appSubclassProcDelegate = null;
        }
    }

    private static LRESULT AppWindowSubclassProc(HWND hWnd, uint uMsg, WPARAM wParam, LPARAM lParam, nuint uIdSubclass, nuint dwRefData)
    {
        const uint WM_HOTKEY = 0x0312;
        const uint WM_NCDESTROY = 0x0082;

        switch (uMsg)
        {
            case WM_HOTKEY:
                System.Diagnostics.Debug.WriteLine($"[App Subclass] WM_HOTKEY received: ID={(int)wParam.Value}");
                _keyboardHotkeyService?.ProcessHotkeyMessage((int)wParam.Value);
                return new LRESULT(0);

            case WM_NCDESTROY:
                System.Diagnostics.Debug.WriteLine("[App Subclass] WM_NCDESTROY received. Removing app subclass.");
                RemoveWindowSubclassing(hWnd);
                CleanupServices();
                _mainWndHwnd = default;
                break;
        }
        return PInvoke.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static void RemoveWindowSubclassing(HWND hwnd)
    {
        if (!_isSubclassed || hwnd == HWND.Null || _appSubclassProcDelegate == null) return;
        try
        {
            System.Diagnostics.Debug.WriteLine($"[App] Attempting to remove subclass ID {APP_SUBCLASS_ID} from HWND {hwnd}...");
            bool removed = PInvoke.RemoveWindowSubclass(hwnd, _appSubclassProcDelegate, APP_SUBCLASS_ID);
            if (!removed)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 1460) System.Diagnostics.Debug.WriteLine($"[App] Failed to remove subclass. Error: {error}");
                else System.Diagnostics.Debug.WriteLine($"[App] Subclass likely already removed (Error 1460).");
            }
            else { System.Diagnostics.Debug.WriteLine($"[App] Subclass removed successfully."); }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] Exception during RemoveWindowSubclass: {ex.Message}"); }
        finally { _isSubclassed = false; _appSubclassProcDelegate = null; }
    }

    internal static void ReloadAndRestartGlobalServices(HWND? hwndCheck = null, AppSettings? settings = null)
    {
        System.Diagnostics.Debug.WriteLine("[App] Reloading settings and restarting global services...");
        HWND currentHwnd = _mainWndHwnd;

        AppSettings currentSettings;
        if (settings != null)
        {
            currentSettings = settings;
            System.Diagnostics.Debug.WriteLine("[App] Using provided settings for reload.");
        }
        else
        {
            if (_settingsService == null) { _settingsService = new SettingsService(); }
            currentSettings = _settingsService.LoadSettings();
            System.Diagnostics.Debug.WriteLine("[App] Loaded settings internally for reload.");
        }

        System.Diagnostics.Debug.WriteLine("[App] Disposing existing services...");
        _keyboardHotkeyService?.Dispose();
        _keyboardHotkeyService = null;
        _hotkeyService?.StopListener();
        _hotkeyService?.Dispose();
        _hotkeyService = null;
        System.Diagnostics.Debug.WriteLine("[App] Finished disposing old services.");

        int? targetVid = currentSettings.SelectedGlobalGamepadVid;
        int? targetPid = currentSettings.SelectedGlobalGamepadPid;
        currentSettings.GlobalJoystickBindings.TryGetValue("GLOBAL_TOGGLE", out string? toggleJoyCode);
        currentSettings.GlobalJoystickBindings.TryGetValue("GLOBAL_MENU", out string? menuJoyCode);
        System.Diagnostics.Debug.WriteLine($"[App] Settings Used - VID:{targetVid?.ToString("X4") ?? "N"}, PID:{targetPid?.ToString("X4") ?? "N"}, ToggleJoy:'{toggleJoyCode ?? "N"}', MenuJoy:'{menuJoyCode ?? "N"}'");
        if (targetVid.HasValue && targetPid.HasValue)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Configuring GlobalHotkeyService...");
            try
            {
                _hotkeyService = new GlobalHotkeyService(_mainDispatcherQueue, targetVid.Value, targetPid.Value, toggleJoyCode, menuJoyCode);
                _hotkeyService.StartListener();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] Failed to restart GlobalHotkeyService: {ex.Message}"); _hotkeyService = null; }
        }
        else { System.Diagnostics.Debug.WriteLine("[App] No global gamepad configured. GlobalHotkeyService not started."); _hotkeyService = null; }

        string? toggleKeyStr = null; string? menuKeyStr = null;
        currentSettings.GlobalKeyboardBindings?.TryGetValue("GLOBAL_TOGGLE", out toggleKeyStr);
        currentSettings.GlobalKeyboardBindings?.TryGetValue("GLOBAL_MENU", out menuKeyStr);
        System.Diagnostics.Debug.WriteLine($"[App] Settings Used - ToggleKey:'{toggleKeyStr ?? "N"}', MenuKey:'{menuKeyStr ?? "N"}'");
        if (currentHwnd != default && currentHwnd != HWND.Null)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Configuring KeyboardHotkeyService (using HWND {currentHwnd})...");
            try
            {
                _keyboardHotkeyService = new KeyboardHotkeyService(currentHwnd);
                _keyboardHotkeyService.RegisterHotkeys(currentSettings.GlobalKeyboardBindings ?? new Dictionary<string, string?>());
                System.Diagnostics.Debug.WriteLine("[App] KeyboardHotkeyService configured.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to start KeyboardHotkeyService: {ex.Message}");
                _keyboardHotkeyService?.Dispose(); _keyboardHotkeyService = null;
            }
        }
        else { System.Diagnostics.Debug.WriteLine("[App] KeyboardHotkeyService not started due to missing HWND."); _keyboardHotkeyService?.Dispose(); _keyboardHotkeyService = null; }

        if (settings == null && MainWin != null)
        {
            System.Diagnostics.Debug.WriteLine("[App] Notifying MainWindow to update local bindings after internal reload...");
            MainWin.DispatcherQueue?.TryEnqueue(() => { MainWin.UpdateLocalBindings(currentSettings); });
        }
        else if (settings != null) { System.Diagnostics.Debug.WriteLine("[App] Skipping MainWindow notification (initial load or settings provided)."); }

        System.Diagnostics.Debug.WriteLine("[App] Finished reloading global services.");
    }

    public static void RequestToggleWindow()
    {
        _mainDispatcherQueue?.TryEnqueue(() =>
        {
            System.Diagnostics.Debug.WriteLine("[App] RequestToggleWindow called.");
            MainWin?.ToggleVisibility();
        });
    }

    public static void RequestToggleMenu()
    {
        _mainDispatcherQueue?.TryEnqueue(() =>
        {
            System.Diagnostics.Debug.WriteLine("[App] RequestToggleMenu called.");
            MainWin?.TriggerMenuToggle();
            System.Diagnostics.Debug.WriteLine("[App] Menu toggle requested on UI thread.");
        });
    }

    public static void PauseGlobalHotkeys()
    {
        _hotkeyService?.PauseListener();
        _keyboardHotkeyService?.UnregisterAllHotkeys();
        System.Diagnostics.Debug.WriteLine("[App] Global hotkeys (Joy+Key) paused/unregistered.");
    }

    public static void ResumeGlobalHotkeys()
    {
        _hotkeyService?.ResumeListener();
        if (_keyboardHotkeyService != null && _settingsService != null)
        {
            try
            {
                var settings = _settingsService.LoadSettings();
                _keyboardHotkeyService.RegisterHotkeys(settings.GlobalKeyboardBindings ?? new Dictionary<string, string?>());
                System.Diagnostics.Debug.WriteLine("[App] Keyboard hotkeys re-registered.");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] Failed to re-register keyboard hotkeys: {ex.Message}"); }
        }
        else { System.Diagnostics.Debug.WriteLine("[App] Keyboard hotkeys NOT re-registered (service null?)."); }
        System.Diagnostics.Debug.WriteLine("[App] Global hotkeys (Joy+Key) resumed/re-registered.");
    }

    private static void CleanupServices()
    {
        System.Diagnostics.Debug.WriteLine("[App] Cleaning up services...");
        _hotkeyService?.StopListener();
        _hotkeyService?.Dispose();
        _hotkeyService = null;
        _keyboardHotkeyService?.Dispose();
        _keyboardHotkeyService = null;
    }

    private Window? m_window; // This field seems unused
}