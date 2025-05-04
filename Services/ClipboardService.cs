// Services/ClipboardService.cs
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace JpnStudyTool.Services;

public class ClipboardService
{
    public event EventHandler<string>? JapaneseTextCopied;

    private string _lastClipboardText = string.Empty;
    private bool _isMonitoring = false;
    private bool _readClipboardOnLoad = false;
    private const string IgnorePrefix = "[JST_IGNORE]";

    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        _isMonitoring = true;
        Clipboard.ContentChanged += Clipboard_ContentChanged;
        System.Diagnostics.Debug.WriteLine("[ClipboardService] Monitoring started.");
        if (!_readClipboardOnLoad) return;
        _ = CheckClipboardContent();
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;
        _isMonitoring = false;
        Clipboard.ContentChanged -= Clipboard_ContentChanged;
        System.Diagnostics.Debug.WriteLine("[ClipboardService] Monitoring stopped.");
    }

    private async void Clipboard_ContentChanged(object? sender, object e)
    {
        await CheckClipboardContent();
    }

    private async Task CheckClipboardContent()
    {
        if (!_isMonitoring) return;

        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (content == null) return;
            if (!content.Contains(StandardDataFormats.Text)) return;

            string text = await content.GetTextAsync();
            if (text.StartsWith(IgnorePrefix)) return;
            text = text.Replace("\r", "").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text == _lastClipboardText) return;
            if (!JapaneseTextDetector.ContainsJapanese(text)) return;

            _lastClipboardText = text;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Content changed: {text.Substring(0, Math.Min(50, text.Length))}...");
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Japanese text detected!");
            JapaneseTextCopied?.Invoke(this, text);

        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Error accessing clipboard: {ex.Message}");
            _lastClipboardText = string.Empty;
        }
    }
    public static void SetClipboardTextWithIgnore(string text)
    {
        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(IgnorePrefix + text);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Error setting clipboard text: {ex.Message}");
        }
    }
}