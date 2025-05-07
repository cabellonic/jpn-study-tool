// Views/NewSessionDialog.xaml.cs
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace JpnStudyTool.Views
{
    public sealed partial class NewSessionDialog : ContentDialog
    {
        public string SessionName => SessionNameTextBox.Text;
        public string? SelectedImagePath { get; private set; }

        public NewSessionDialog()
        {
            this.InitializeComponent();
        }

        private async void SelectImageButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var fileOpenPicker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            fileOpenPicker.FileTypeFilter.Add(".jpg");
            fileOpenPicker.FileTypeFilter.Add(".jpeg");
            fileOpenPicker.FileTypeFilter.Add(".png");
            fileOpenPicker.FileTypeFilter.Add(".bmp");

            if (App.MainWin != null)
            {
                InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(App.MainWin));
            }
            else if (this.XamlRoot != null && this.XamlRoot.Content != null)
            {
                InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(this.XamlRoot.Content));
            }

            var file = await fileOpenPicker.PickSingleFileAsync();
            if (file != null)
            {
                SelectedImagePath = file.Path;
                SelectedImagePathTextBlock.Text = $"Selected: {System.IO.Path.GetFileName(file.Path)}";
                SelectedImagePathTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
            else
            {
                SelectedImagePathTextBlock.Text = "";
                SelectedImagePathTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        private void NewSessionDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(SessionNameTextBox.Text))
            {
                args.Cancel = true;
                NameErrorMessageTextBlock.Text = "Session name cannot be empty.";
                NameErrorMessageTextBlock.Visibility = Visibility.Visible;
                SessionNameTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            }
            else
            {
                NameErrorMessageTextBlock.Visibility = Visibility.Collapsed;
            }
        }
    }
}