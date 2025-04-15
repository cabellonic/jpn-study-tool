// Services/SettingsService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JpnStudyTool.Models;
using Windows.Storage;

namespace JpnStudyTool.Services;

public class SettingsService
{
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _serializerOptions;

    public SettingsService()
    {
        string localFolderPath = ApplicationData.Current.LocalFolder.Path;
        _settingsFilePath = Path.Combine(localFolderPath, "appsettings.json");

        _serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        System.Diagnostics.Debug.WriteLine($"[SettingsService] Config file path: {_settingsFilePath}");
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings.GlobalJoystickBindings ??= new Dictionary<string, string?>();
                        settings.GlobalKeyboardBindings ??= new Dictionary<string, string?>();
                        settings.LocalJoystickBindings ??= new Dictionary<string, string?>();
                        settings.LocalKeyboardBindings ??= new Dictionary<string, string?>();
                        System.Diagnostics.Debug.WriteLine("[SettingsService] Settings loaded successfully from file.");
                        return settings;
                    }
                    System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file deserialized to null. Using defaults.");
                }
                else { System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file was empty. Using defaults."); }
            }
            else { System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file not found. Using defaults."); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Error loading/deserializing settings: {ex.Message}. Using defaults.");
            if (ex is JsonException) { try { File.Delete(_settingsFilePath); } catch { } }
        }

        System.Diagnostics.Debug.WriteLine("[SettingsService] Returning default settings.");
        return GetDefaultSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            settings.GlobalJoystickBindings ??= new Dictionary<string, string?>();
            settings.GlobalKeyboardBindings ??= new Dictionary<string, string?>();

            string json = JsonSerializer.Serialize(settings, _serializerOptions);
            File.WriteAllText(_settingsFilePath, json);
            System.Diagnostics.Debug.WriteLine("[SettingsService] Settings saved successfully.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Error saving settings: {ex.Message}");
        }
    }

    private void MergeDefaultBindings(Dictionary<string, string?> target, Dictionary<string, string?> defaults)
    {
        if (defaults == null) return;
        foreach (var kvp in defaults)
        {
            if (!target.ContainsKey(kvp.Key))
            {
                target.Add(kvp.Key, kvp.Value);
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Applied default binding for '{kvp.Key}'.");
            }
        }
    }

    private AppSettings CreateDefaultSettings()
    {
        var defaultSettings = new AppSettings
        {
            ReadClipboardOnLoad = false,
            SelectedGlobalGamepadVid = null,
            SelectedGlobalGamepadPid = null,
            GlobalJoystickBindings = new Dictionary<string, string?>() {
                { "GLOBAL_TOGGLE", "BTN_THUMBR" },
                { "GLOBAL_MENU", "BTN_THUMBL" }
            },
            GlobalKeyboardBindings = new Dictionary<string, string?>()
            {
                { "GLOBAL_TOGGLE", "Ctrl+Alt+J" },
                { "GLOBAL_MENU", "Ctrl+Alt+M" }
            },
            LocalJoystickBindings = new Dictionary<string, string?>()
            {
                { "NAV_UP", "DPAD_UP" },
                { "NAV_DOWN", "DPAD_DOWN" },
                { "NAV_LEFT", "DPAD_LEFT" },
                { "NAV_RIGHT", "DPAD_RIGHT" },
                { "DETAIL_ENTER", "BTN_A" },
                { "DETAIL_BACK", "BTN_B" },
                { "SAVE_WORD", "BTN_Y" },
                { "DELETE_WORD", "BTN_X" }
            },
            LocalKeyboardBindings = new Dictionary<string, string?>()
            {
                { "NAV_UP", "Up" },
                { "NAV_DOWN", "Down" },
                { "NAV_LEFT", "Left" },
                { "NAV_RIGHT", "Right" },
                { "DETAIL_ENTER", "Enter" },
                { "DETAIL_BACK", "Back" },
                { "SAVE_WORD", "Ctrl+S" },
                { "DELETE_WORD", "Delete" }
            }

        };
        return defaultSettings;
    }

    public AppSettings GetDefaultSettings()
    {
        return CreateDefaultSettings();
    }
}