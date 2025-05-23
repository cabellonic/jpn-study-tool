﻿// Models/AppSettings.cs
using System.Collections.Generic;

namespace JpnStudyTool.Models;

public enum AiAnalysisTrigger
{
    OnDemand,
    OnCopy,
    OnGlobalOpen
}

public partial class AppSettings
{
    public bool ReadClipboardOnLoad { get; set; } = false;

    public int? SelectedGlobalGamepadVid { get; set; } = null;
    public int? SelectedGlobalGamepadPid { get; set; } = null;

    public Dictionary<string, string?> GlobalJoystickBindings { get; set; }
    public Dictionary<string, string?> GlobalKeyboardBindings { get; set; }

    public Dictionary<string, string?> LocalJoystickBindings { get; set; }
    public Dictionary<string, string?> LocalKeyboardBindings { get; set; }

    public bool UseAIMode { get; set; } = false;
    public AiAnalysisTrigger AiAnalysisTriggerMode { get; set; } = AiAnalysisTrigger.OnDemand;
    public string? GeminiApiKey { get; set; } = null;
    public string? ActiveSessionId { get; set; } = null;

    public AppSettings()
    {
        GlobalJoystickBindings = new Dictionary<string, string?>();
        GlobalKeyboardBindings = new Dictionary<string, string?>();
        LocalJoystickBindings = new Dictionary<string, string?>();
        LocalKeyboardBindings = new Dictionary<string, string?>();
    }
}