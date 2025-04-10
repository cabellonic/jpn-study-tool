// App.xaml.cs
using System;
using JpnStudyTool.Models;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace JpnStudyTool
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static MainWindow? MainWin = null;
        private static GlobalHotkeyService? _hotkeyService;
        private static DispatcherQueue? _mainDispatcherQueue;
        private static SettingsService? _settingsService;
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _mainDispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _settingsService = new SettingsService();

            ReloadAndRestartGlobalServices();

            // --- Load Config ---
            AppSettings currentSettings = _settingsService.LoadSettings();

            int? targetVid = currentSettings.SelectedGlobalGamepadVid;
            int? targetPid = currentSettings.SelectedGlobalGamepadPid;

            currentSettings.GlobalJoystickBindings.TryGetValue("GLOBAL_TOGGLE", out string? toggleButtonCode);
            currentSettings.GlobalJoystickBindings.TryGetValue("GLOBAL_MENU", out string? menuButtonCode);

            System.Diagnostics.Debug.WriteLine($"[App] Loaded Settings - VID: {targetVid?.ToString("X4") ?? "None"}, PID: {targetPid?.ToString("X4") ?? "None"}");
            System.Diagnostics.Debug.WriteLine($"[App] Loaded Settings - ToggleJoy: '{toggleButtonCode ?? "None"}', MenuJoy: '{menuButtonCode ?? "None"}'");

            if (targetVid.HasValue && targetPid.HasValue)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Configuring GlobalHotkeyService with loaded settings...");
                try
                {
                    _hotkeyService?.Dispose();
                    _hotkeyService = new GlobalHotkeyService(
                        _mainDispatcherQueue,
                        targetVid.Value,
                        targetPid.Value,
                        toggleButtonCode,
                        menuButtonCode);
                    _hotkeyService.StartListener();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] Failed to start GlobalHotkeyService: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App] No global gamepad configured in settings. GlobalHotkeyService not started.");
                _hotkeyService?.Dispose();
                _hotkeyService = null;
            }


            MainWin = new MainWindow();
            MainWin.Activate();
        }

        public static void ReloadAndRestartGlobalServices()
        {
            System.Diagnostics.Debug.WriteLine("[App] Reloading settings and restarting global services...");

            AppSettings currentSettings;
            if (_settingsService == null)
            {
                System.Diagnostics.Debug.WriteLine("[App] Error: SettingsService is null during reload.");
                _settingsService = new SettingsService();
            }
            currentSettings = _settingsService.LoadSettings();


            _hotkeyService?.StopListener();
            _hotkeyService?.Dispose();
            _hotkeyService = null;

            int? targetVid = currentSettings.SelectedGlobalGamepadVid;
            int? targetPid = currentSettings.SelectedGlobalGamepadPid;
            currentSettings.GlobalJoystickBindings.TryGetValue("GLOBAL_TOGGLE", out string? toggleButtonCode);
            currentSettings.GlobalJoystickBindings.TryGetValue("GLOBAL_MENU", out string? menuButtonCode);

            System.Diagnostics.Debug.WriteLine($"[App] Reloaded Settings - VID: {targetVid?.ToString("X4") ?? "None"}, PID: {targetPid?.ToString("X4") ?? "None"}");
            System.Diagnostics.Debug.WriteLine($"[App] Reloaded Settings - ToggleJoy: '{toggleButtonCode ?? "None"}', MenuJoy: '{menuButtonCode ?? "None"}'");

            if (targetVid.HasValue && targetPid.HasValue)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Configuring GlobalHotkeyService with reloaded settings...");
                try
                {
                    _hotkeyService = new GlobalHotkeyService(
                        _mainDispatcherQueue,
                        targetVid.Value,
                        targetPid.Value,
                        toggleButtonCode,
                        menuButtonCode);
                    _hotkeyService.StartListener();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] Failed to restart GlobalHotkeyService: {ex.Message}");
                    _hotkeyService = null;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App] No global gamepad configured in reloaded settings. GlobalHotkeyService not started.");
                _hotkeyService = null;
            }

            System.Diagnostics.Debug.WriteLine("[App] Finished reloading global services.");
        }

        public static void RequestToggleWindow()
        {
            System.Diagnostics.Debug.WriteLine("[App] RequestToggleWindow called by hotkey service.");
            MainWin?.ToggleVisibility();
        }

        public static void RequestToggleMenu()
        {
            System.Diagnostics.Debug.WriteLine("[App] RequestToggleMenu called by hotkey service.");
            _mainDispatcherQueue?.TryEnqueue(() =>
            {
                MainWin?.TriggerMenuToggle();
                System.Diagnostics.Debug.WriteLine("[App] Menu toggle requested on UI thread.");
            });
        }

        public static void PauseGlobalHotkeys()
        {
            if (_hotkeyService != null)
            {
                _hotkeyService.PauseListener();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App] PauseGlobalHotkeys skipped: Service not running.");
            }
        }

        public static void ResumeGlobalHotkeys()
        {
            if (_hotkeyService != null)
            {
                _hotkeyService.ResumeListener();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App] ResumeGlobalHotkeys skipped: Service not running.");
            }
        }

        private Window? m_window;
    }
}
