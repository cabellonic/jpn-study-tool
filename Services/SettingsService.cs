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
                        System.Diagnostics.Debug.WriteLine("[SettingsService] Settings loaded successfully.");
                        return settings;
                    }
                }
                System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file was empty or invalid JSON.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file not found. Using defaults.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Error loading settings: {ex.Message}");
        }

        return CreateDefaultSettings();
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

    private AppSettings CreateDefaultSettings()
    {
        var defaultSettings = new AppSettings
        {
            ReadClipboardOnLoad = false,
            SelectedGlobalGamepadVid = null,
            SelectedGlobalGamepadPid = null,
            GlobalJoystickBindings = new Dictionary<string, string?>(),
            GlobalKeyboardBindings = new Dictionary<string, string?>()
            {
                { "GLOBAL_TOGGLE", "Ctrl+Alt+J" },
                { "GLOBAL_MENU", "Ctrl+Alt+M" }
            }
        };
        return defaultSettings;
    }
}