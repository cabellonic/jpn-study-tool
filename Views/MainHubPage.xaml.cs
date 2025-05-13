// Views/MainHubPage.xaml.cs
using System.Linq;
using System.Threading.Tasks;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
namespace JpnStudyTool.Views
{
    public sealed partial class MainHubPage : Page
    {
        public MainHubViewModel ViewModel { get; private set; }
        private string? _targetSessionIdForFocus = null;
        public MainHubPage()
        {
            this.InitializeComponent();
            ViewModel = new MainHubViewModel();
            this.DataContext = ViewModel;
            this.Loaded += MainHubPage_Loaded;
        }
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            System.Diagnostics.Debug.WriteLine("[MainHubPage] OnNavigatedTo called.");
            if (e.Parameter is string sessionId && !string.IsNullOrEmpty(sessionId))
            {
                _targetSessionIdForFocus = sessionId;
                System.Diagnostics.Debug.WriteLine($"[MainHubPage] Received target session ID for focus: {_targetSessionIdForFocus}");
            }
            else
            {
                _targetSessionIdForFocus = null;
            }
            if (ViewModel != null)
            {
                await ViewModel.LoadDataAsync();
            }
        }
        private async void MainHubPage_Loaded(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[MainHubPage] Loaded event fired.");
            FrameworkElement? elementToFocus = null;
            string focusedElementName = "nothing";
            if (!string.IsNullOrEmpty(_targetSessionIdForFocus))
            {
                await Task.Delay(100);
                TryFocusTargetSessionCard(_targetSessionIdForFocus);
                _targetSessionIdForFocus = null;
                return;
            }
            ItemsRepeater? sessionsRepeater = this.FindName("RecentSessionsRepeater") as ItemsRepeater;
            if (ViewModel.RecentSessions.Any() && sessionsRepeater != null)
            {
                await Task.Delay(50);
                elementToFocus = sessionsRepeater.TryGetElement(0) as Control;
                if (elementToFocus != null)
                {
                    focusedElementName = "FirstSessionCard";
                    (elementToFocus as Control)?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, HorizontalAlignmentRatio = 0.0 });
                }
            }
            if (elementToFocus == null)
            {
                elementToFocus = this.FindName("NewSessionButtonHub") as Button;
                if (elementToFocus != null)
                {
                    focusedElementName = "NewSessionButtonHub";
                }
            }
            if (elementToFocus == null)
            {
                elementToFocus = this.FindName("SettingsButtonHub") as Button;
                if (elementToFocus != null)
                {
                    focusedElementName = "SettingsButtonHub (fallback)";
                }
            }
            if (elementToFocus != null)
            {
                elementToFocus.Focus(FocusState.Programmatic);
                System.Diagnostics.Debug.WriteLine($"[MainHubPage Loaded] Initial focus set to: {focusedElementName}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MainHubPage Loaded] Could not set initial focus to any specific element.");
            }
        }
        private void TryFocusTargetSessionCard(string? targetSessionId)
        {
            if (string.IsNullOrEmpty(targetSessionId) || ViewModel.RecentSessions == null || !ViewModel.RecentSessions.Any())
            {
                System.Diagnostics.Debug.WriteLine($"[MainHubPage TryFocusTarget] No target ID or no sessions to search.");
                return;
            }
            ItemsRepeater? sessionsRepeater = this.FindName("RecentSessionsRepeater") as ItemsRepeater;
            if (sessionsRepeater == null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainHubPage TryFocusTarget] ItemsRepeater not found.");
                return;
            }
            var sessionItem = ViewModel.RecentSessions.FirstOrDefault(s => s.SessionId == targetSessionId);
            if (sessionItem != null)
            {
                int index = ViewModel.RecentSessions.IndexOf(sessionItem);
                System.Diagnostics.Debug.WriteLine($"[MainHubPage TryFocusTarget] Found session at index {index} for ID {targetSessionId}.");
                if (index >= 0)
                {
                    var uiElement = sessionsRepeater.TryGetElement(index) as Control;
                    if (uiElement != null)
                    {
                        uiElement.Focus(FocusState.Programmatic);
                        uiElement.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, HorizontalAlignmentRatio = 0.5 });
                        System.Diagnostics.Debug.WriteLine($"[MainHubPage TryFocusTarget] Focused UI element for session ID: {targetSessionId}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainHubPage TryFocusTarget] Could not realize UI element at index {index} for session ID: {targetSessionId}. Focusing ScrollViewer as fallback.");
                        (this.FindName("RecentSessionsScrollViewer") as ScrollViewer)?.Focus(FocusState.Programmatic);
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MainHubPage TryFocusTarget] Session ID {targetSessionId} not found in RecentSessions.");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }
    }
}