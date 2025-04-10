// App.xaml.cs
using System;
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

            int targetVid = 0x057E; // Nintendo VID
            int targetPid = 0x2009; // Pro Controller PID

            string toggleButton = "BTN_THUMBL";
            string menuButton = "BTN_THUMBR";

            System.Diagnostics.Debug.WriteLine($"[App] Configuring GlobalHotkeyService - VID={targetVid:X4}, PID={targetPid:X4}, Toggle='{toggleButton}', Menu='{menuButton}'");

            // --- Initialize Hotkey Service ---
            try
            {
                _hotkeyService?.Dispose();
                _hotkeyService = new GlobalHotkeyService(_mainDispatcherQueue, targetVid, targetPid, toggleButton, menuButton);
                _hotkeyService.StartListener();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to start GlobalHotkeyService: {ex.Message}");
            }

            MainWin = new MainWindow();
            MainWin.Activate();
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

        private Window? m_window;
    }
}
