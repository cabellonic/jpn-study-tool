// Services/WindowActivationService.cs
using System;
using Windows.Win32.Foundation;

using static Windows.Win32.PInvoke;
using static Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS;
using static Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD;

namespace JpnStudyTool.Services;

public class WindowActivationService
{
    private HWND _windowHandle = HWND.Null;

    public void RegisterWindowHandle(IntPtr hwnd)
    {
        _windowHandle = (HWND)hwnd;

        IntPtr handlePtr = IntPtr.Zero;

        unsafe
        {
            try
            {
                handlePtr = (IntPtr)_windowHandle.Value;
                System.Diagnostics.Debug.WriteLine($"[WindowActivation] Registered HWND: {handlePtr}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowActivation] Error unsafe casting HWND value: {ex.Message}");
                handlePtr = hwnd;
            }
        }
    }

    public void ToggleMainWindowVisibility()
    {
        if (_windowHandle == HWND.Null) { /* ... */ return; }
        try
        {
            BOOL isVisible = IsWindowVisible(_windowHandle);
            HWND HWND_TOPMOST_VALUE = new HWND(-1);
            HWND HWND_NOTOPMOST_VALUE = new HWND(-2);

            if (isVisible)
            {
                ShowWindow(_windowHandle, SW_HIDE);
                System.Diagnostics.Debug.WriteLine("[WindowActivation] Hiding window.");
            }
            else
            {
                ShowWindow(_windowHandle, SW_SHOWNORMAL);
                SetForegroundWindow(_windowHandle);
                SetWindowPos(_windowHandle, HWND_TOPMOST_VALUE, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetWindowPos(_windowHandle, HWND_NOTOPMOST_VALUE, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                System.Diagnostics.Debug.WriteLine("[WindowActivation] Showing and activating window.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowActivation] Error toggling window visibility: {ex.Message}");
        }
    }
}