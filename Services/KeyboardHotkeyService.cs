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
    private static int _nextHotkeyId = 1;

    public KeyboardHotkeyService(HWND windowHandle)
    {
        if (windowHandle == HWND.Null)
            throw new ArgumentNullException(nameof(windowHandle), "Window handle cannot be null for KeyboardHotkeyService.");

        _hwnd = windowHandle;
        _actions = new Dictionary<string, Action>
        {
            { "GLOBAL_TOGGLE", App.RequestToggleWindow },
            { "GLOBAL_MENU", App.RequestToggleMenu }
        };
        _registeredHotkeys = new Dictionary<int, string>();
        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Instance created for HWND {_hwnd}.");
    }

    public void RegisterHotkeys(Dictionary<string, string?>? keyboardBindings)
    {
        UnregisterAllHotkeys();

        if (keyboardBindings == null || !keyboardBindings.Any() || _hwnd == HWND.Null)
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Skipping registration (No bindings, or HWND invalid).");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Registering hotkeys for HWND {_hwnd}...");

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
                int currentHotkeyId = _nextHotkeyId++;

                System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Attempting RegisterHotKey: ID={currentHotkeyId}, Mod={modifiers}, VK={vk}, Action='{actionId}', Binding='{bindingString}'");
                try
                {
                    bool success = PInvoke.RegisterHotKey(_hwnd, currentHotkeyId, modifiers, vk);

                    if (success)
                    {
                        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Registered successfully.");
                        _registeredHotkeys.Add(currentHotkeyId, actionId);
                    }
                    else
                    {
                        int error = Marshal.GetLastPInvokeError();
                        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] FAILED RegisterHotKey. Error: {error}" + (error == 1409 ? " (Hotkey already registered by another application?)" : ""));
                    }
                }
                catch (ArgumentException argEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] CRITICAL ArgumentException during RegisterHotKey for Binding='{bindingString}'. VK={vk}, Mod={modifiers}. Exception: {argEx.Message}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] CRITICAL Exception during RegisterHotKey for Binding='{bindingString}'. Exception: {ex.GetType().Name} - {ex.Message}");
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
        modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        vk = 0;

        var parts = bindingString.Split('+').Select(p => p.Trim().ToUpperInvariant()).ToList();
        if (!parts.Any()) return false;

        if (parts.Contains("CTRL")) { modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL; parts.Remove("CTRL"); }
        if (parts.Contains("ALT")) { modifiers |= HOT_KEY_MODIFIERS.MOD_ALT; parts.Remove("ALT"); }
        if (parts.Contains("SHIFT")) { modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT; parts.Remove("SHIFT"); }
        if (parts.Contains("WIN")) { modifiers |= HOT_KEY_MODIFIERS.MOD_WIN; parts.Remove("WIN"); }

        string? keyPart = parts.LastOrDefault();
        if (string.IsNullOrEmpty(keyPart))
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] No key part found in binding: '{bindingString}' after removing modifiers.");
            return false;
        }


        if (Enum.TryParse<VirtualKey>(keyPart, true, out VirtualKey virtualKey))
        {
            if (virtualKey == VirtualKey.Control || virtualKey == VirtualKey.Shift || virtualKey == VirtualKey.Menu ||
                virtualKey == VirtualKey.LeftControl || virtualKey == VirtualKey.RightControl ||
                virtualKey == VirtualKey.LeftShift || virtualKey == VirtualKey.RightShift ||
                virtualKey == VirtualKey.LeftMenu || virtualKey == VirtualKey.RightMenu ||
                virtualKey == VirtualKey.LeftWindows || virtualKey == VirtualKey.RightWindows)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Invalid binding: Modifier key '{keyPart}' cannot be the main hotkey.");
                return false;
            }

            vk = (uint)virtualKey;
            return true;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Could not parse key part: '{keyPart}' from binding: '{bindingString}'");
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
        var hotkeyIds = _registeredHotkeys.Keys.ToList();
        foreach (int hotkeyId in hotkeyIds)
        {
            if (!PInvoke.UnregisterHotKey(_hwnd, hotkeyId))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 1413 && error != 1401)
                {
                    System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Failed to unregister hotkey ID {hotkeyId}. Error: {error}");
                }
            }
            else
            {
                // System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Unregistered hotkey ID {hotkeyId}.");
            }
            _registeredHotkeys.Remove(hotkeyId);
        }
        System.Diagnostics.Debug.WriteLine($"[KeyboardHotkeyService] Finished unregistering hotkeys. Count remaining: {_registeredHotkeys.Count}");
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