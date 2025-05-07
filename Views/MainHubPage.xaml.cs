// Views/MainHubPage.xaml.cs
using JpnStudyTool.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace JpnStudyTool.Views
{
    public sealed partial class MainHubPage : Page
    {
        public MainHubViewModel ViewModel { get; private set; }

        public MainHubPage()
        {
            this.InitializeComponent();
            ViewModel = new MainHubViewModel();
            this.DataContext = ViewModel;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            System.Diagnostics.Debug.WriteLine("[MainHubPage] OnNavigatedTo called.");
            if (ViewModel != null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainHubPage] Forcing LoadDataAsync execution for testing.");
                await ViewModel.LoadDataAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }
    }
}