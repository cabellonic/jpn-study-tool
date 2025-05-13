
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        System.Diagnostics.Debug.WriteLine($"[SettingsService] Config file path: {_settingsFilePath}");
    }
    public AppSettings LoadSettings()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    settings = JsonSerializer.Deserialize<AppSettings>(json, _serializerOptions);
                }
                else { System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file was empty. Using defaults."); }
            }
            else { System.Diagnostics.Debug.WriteLine("[SettingsService] Settings file not found. Using defaults."); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Error loading/deserializing settings: {ex.Message}. Using defaults.");
            if (ex is JsonException) { try { File.Delete(_settingsFilePath); System.Diagnostics.Debug.WriteLine("[SettingsService] Deleted potentially corrupted settings file."); } catch { } }
        }
        bool settingsWereNull = settings == null;
        settings ??= GetDefaultSettings();

        settings.GlobalJoystickBindings ??= GetDefaultSettings().GlobalJoystickBindings;
        settings.GlobalKeyboardBindings ??= GetDefaultSettings().GlobalKeyboardBindings;
        settings.LocalJoystickBindings ??= GetDefaultSettings().LocalJoystickBindings;
        settings.LocalKeyboardBindings ??= GetDefaultSettings().LocalKeyboardBindings;

        if (!Enum.IsDefined(typeof(AiAnalysisTrigger), settings.AiAnalysisTriggerMode))
        {
            settings.AiAnalysisTriggerMode = GetDefaultSettings().AiAnalysisTriggerMode;
            if (!settingsWereNull) System.Diagnostics.Debug.WriteLine("[SettingsService] AiAnalysisTriggerMode reset to default due to invalid loaded value.");
        }
        System.Diagnostics.Debug.WriteLine($"[SettingsService] Settings loaded. UseAIMode: {settings.UseAIMode}, Trigger: {settings.AiAnalysisTriggerMode}, HasApiKey: {!string.IsNullOrEmpty(settings.GeminiApiKey)}, ActiveSessionId from file: {settings.ActiveSessionId ?? "None"}");
        return settings;
    }
    public void SaveSettings(AppSettings settings)
    {
        try
        {
            settings.GlobalJoystickBindings ??= new Dictionary<string, string?>();
            settings.GlobalKeyboardBindings ??= new Dictionary<string, string?>();
            settings.LocalJoystickBindings ??= new Dictionary<string, string?>();
            settings.LocalKeyboardBindings ??= new Dictionary<string, string?>();
            string json = JsonSerializer.Serialize(settings, _serializerOptions);
            File.WriteAllText(_settingsFilePath, json);
            System.Diagnostics.Debug.WriteLine("[SettingsService] Settings saved successfully.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Error saving settings: {ex.Message}");
        }
    }
    public AppSettings GetDefaultSettings()
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
            { "LOCAL_NAV_UP", "DPAD_UP" },
            { "LOCAL_NAV_DOWN", "DPAD_DOWN" },
            { "LOCAL_NAV_LEFT", "DPAD_LEFT" },
            { "LOCAL_NAV_RIGHT", "DPAD_RIGHT" },
            { "LOCAL_ACCEPT", "BTN_B" },
            { "LOCAL_CANCEL", "BTN_A" },
            { "LOCAL_CONTEXT_MENU", "BTN_X" },
            { "LOCAL_SAVE_WORD", "BTN_Y" },
            { "LOCAL_NAV_FAST_PAGE_UP", "BTN_TL" },
            { "LOCAL_NAV_FAST_PAGE_DOWN", "BTN_TR" },
            { "LOCAL_SETTINGS_HUB", "BTN_START" },
            { "LOCAL_ANKI_LINK", "BTN_SELECT" }
        },
            LocalKeyboardBindings = new Dictionary<string, string?>()
        {
            { "LOCAL_NAV_UP", "Up" },
            { "LOCAL_NAV_DOWN", "Down" },
            { "LOCAL_NAV_LEFT", "Left" },
            { "LOCAL_NAV_RIGHT", "Right" },
            { "LOCAL_ACCEPT", "Enter" },
            { "LOCAL_CANCEL", "Escape" },
            { "LOCAL_CONTEXT_MENU", "P" },
            { "LOCAL_SAVE_WORD", "Ctrl+S" },
            { "LOCAL_NAV_FAST_PAGE_UP", "PageUp" },
            { "LOCAL_NAV_FAST_PAGE_DOWN", "PageDown" },
            { "LOCAL_SETTINGS_HUB", "F1" },
            { "LOCAL_ANKI_LINK", "Ctrl+A" }
        },
            UseAIMode = false,
            AiAnalysisTriggerMode = AiAnalysisTrigger.OnDemand,
            GeminiApiKey = null
        };
        return defaultSettings;
    }
}