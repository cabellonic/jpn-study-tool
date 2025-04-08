// MainWindow.xaml.cs
using Microsoft.UI.Xaml;
using JpnStudyTool.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace JpnStudyTool;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();

        ViewModel = new MainWindowViewModel();

        // Set DataContext on the root UI element for data binding
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.DataContext = ViewModel;
        }

        this.Title = "Jpn Study Tool v0.1";
        this.Closed += MainWindow_Closed;
    }
    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Use ScrollIntoView to make sure the selected item is visible
        if (sender is ListView listView && listView.SelectedItem != null)
        {
            listView.ScrollIntoView(listView.SelectedItem);
            System.Diagnostics.Debug.WriteLine($"[MainWindow] ScrollIntoView called for index {listView.SelectedIndex}");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        ViewModel?.Cleanup();
    }
}