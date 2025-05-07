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
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (ViewModel.LoadDataCommand.CanExecute(null))
            {
                await ViewModel.LoadDataCommand.ExecuteAsync(null);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Cleanup();
        }
    }
}