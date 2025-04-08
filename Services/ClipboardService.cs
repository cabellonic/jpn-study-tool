// Services/ClipboardService.cs
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using System.Text.RegularExpressions;

namespace JpnStudyTool.Services;

public class ClipboardService
{
    public event EventHandler<string>? JapaneseTextCopied;

    private string _lastClipboardText = string.Empty;
    private bool _isMonitoring = false;
    private bool _readClipboardOnLoad = false;

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
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] AAAA!");
            DataPackageView content = Clipboard.GetContent();
            if (content == null) return;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] BBBB!");
            if (!content.Contains(StandardDataFormats.Text)) return;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] CCCC!");
       
            string text = await content.GetTextAsync();
            text = text.Replace("\r", "").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] DDDD!");
            if (text == _lastClipboardText) return;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] EEEE!");
            if (!JapaneseTextDetector.ContainsJapanese(text)) return;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] FFFF!");

            _lastClipboardText = text;
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Content changed: {text.Substring(0, Math.Min(50, text.Length))}...");
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Japanese text detected!");
            // Throw event to notify ViewModel
            JapaneseTextCopied?.Invoke(this, text);
            
        }
        catch (Exception ex)
        {
            // Reset last clipboard text on error
            System.Diagnostics.Debug.WriteLine($"[ClipboardService] Error accessing clipboard: {ex.Message}");
            _lastClipboardText = string.Empty;
        }
    }
}