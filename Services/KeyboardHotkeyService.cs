// Services/KeyboardHotkeyService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace JpnStudyTool.Services;

internal class KeyboardHotkeyService : IDisposable
{
    private HWND _hwnd;
    private readonly Dictionary<string, Action> _actions;
    private readonly Dictionary<int, string> _registeredHotkeys;

    public KeyboardHotkeyService(HWND windowHandle)
    {
        if (windowHandle == HWND.Null)
            throw new ArgumentNullException(nameof(windowHandle), "Window handle cannot be null.");

        _hwnd = windowHandle;
        _actions = new Dictionary<string, Action>
        {
            { "GLOBAL_TOGGLE", App.RequestToggleWindow },
            { "GLOBAL_MENU", App.RequestToggleMenu }
        };
        _registeredHotkeys = new Dictionary<int, string>();
        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Instance created for HWND {_hwnd}. Subclassing handled by App.");
    }

    public void RegisterHotkeys(Dictionary<string, string?>? keyboardBindings)
    {
        UnregisterAllHotkeys();

        if (keyboardBindings == null || _hwnd == HWND.Null)
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Skipping registration (Bindings null or HWND invalid).");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Registering hotkeys for HWND {_hwnd}...");
        int currentHotkeyId = 1;

        foreach (var kvp in keyboardBindings)
        {
            string actionId = kvp.Key;
            string? bindingString = kvp.Value;

            if (string.IsNullOrWhiteSpace(bindingString) || !_actions.ContainsKey(actionId))
            {
                continue;
            }

            if (TryParseBinding(bindingString, out var modifiers, out var vk))
            {
                bool success = PInvoke.RegisterHotKey(_hwnd, currentHotkeyId, modifiers, vk);
                if (success)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Registered: ID={currentHotkeyId}, Mod={modifiers}, VK={vk} for Action='{actionId}'");
                    _registeredHotkeys.Add(currentHotkeyId, actionId);
                    currentHotkeyId++;
                }
                else
                {
                    int error = Marshal.GetLastPInvokeError();
                    System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] FAILED RegisterHotKey: Mod={modifiers}, VK={vk}, Action='{actionId}'. Error: {error}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Failed to parse binding string: '{bindingString}' for Action='{actionId}'");
            }
        }
        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Finished registering hotkeys. Count: {_registeredHotkeys.Count}");
    }

    private bool TryParseBinding(string bindingString, out HOT_KEY_MODIFIERS modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = bindingString.Split('+').Select(p => p.Trim().ToUpperInvariant()).ToList();
        if (!parts.Any()) return false;

        if (parts.Contains("CTRL")) { modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL; parts.Remove("CTRL"); }
        if (parts.Contains("ALT")) { modifiers |= HOT_KEY_MODIFIERS.MOD_ALT; parts.Remove("ALT"); }
        if (parts.Contains("SHIFT")) { modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT; parts.Remove("SHIFT"); }
        if (parts.Contains("WIN")) { modifiers |= HOT_KEY_MODIFIERS.MOD_WIN; parts.Remove("WIN"); }

        string? keyPart = parts.LastOrDefault();
        if (string.IsNullOrEmpty(keyPart)) return false;

        if (Enum.TryParse<VirtualKey>(keyPart, true, out VirtualKey virtualKey))
        {
            vk = (uint)virtualKey;
            return true;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Could not parse key part: {keyPart}");
            return false;
        }
    }

    public void ProcessHotkeyMessage(int hotkeyId)
    {
        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Processing hotkey ID: {hotkeyId}");
        if (_registeredHotkeys.TryGetValue(hotkeyId, out string? actionId) && actionId != null)
        {
            if (_actions.TryGetValue(actionId, out Action? action))
            {
                System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Triggering action from ProcessHotkeyMessage: {actionId}");
                action?.Invoke();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Action not found for ActionID: {actionId}");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Hotkey ID not registered by this service: {hotkeyId}");
        }
    }

    public void UnregisterAllHotkeys()
    {
        if (_hwnd == HWND.Null || disposedValue || !_registeredHotkeys.Any()) return;

        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Unregistering all hotkeys for HWND {_hwnd}...");
        foreach (var kvp in _registeredHotkeys)
        {
            int hotkeyId = kvp.Key;
            if (!PInvoke.UnregisterHotKey(_hwnd, hotkeyId))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 1401 && error != 1413)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Failed to unregister hotkey ID {hotkeyId}. Error: {error}");
                }
            }
            else
            {
                // System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Unregistered hotkey ID {hotkeyId}."); // Can be verbose
            }
        }
        _registeredHotkeys.Clear();
        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Finished unregistering hotkeys.");
    }

    private bool disposedValue;
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Dispose called (disposing: {disposing}) for HWND {_hwnd}.");
            if (disposing)
            {
                UnregisterAllHotkeys();
            }
            _hwnd = HWND.Null;
            disposedValue = true;
            System.Diagnostics.Debug.WriteLine("[KeyboardHotkeyService] Dispose finished.");
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}