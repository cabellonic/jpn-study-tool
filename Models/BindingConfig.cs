// Models/BindingConfig.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace JpnStudyTool.Models;

public partial class BindingConfig : ObservableObject
{
    public string ActionName { get; }
    public string ActionId { get; }

    [ObservableProperty]
    private string? _keyboardBindingDisplay;

    [ObservableProperty]
    private string? _joystickBindingDisplay;

    public string KeyboardBindingDisplayResolved =>
        string.IsNullOrEmpty(_keyboardBindingDisplay) ? "Set Keyboard" : _keyboardBindingDisplay;

    public string JoystickBindingDisplayResolved =>
        string.IsNullOrEmpty(_joystickBindingDisplay) ? "Set Joystick" : _joystickBindingDisplay;


    public object? KeyboardBindingData { get; set; }
    public object? JoystickBindingData { get; set; }

    public BindingConfig(string actionId, string actionName, string? initialKey = null, string? initialJoy = null)
    {
        ActionId = actionId;
        ActionName = actionName;
        _keyboardBindingDisplay = initialKey;
        _joystickBindingDisplay = initialJoy;
    }

    partial void OnKeyboardBindingDisplayChanged(string? value)
    {
        OnPropertyChanged(nameof(KeyboardBindingDisplayResolved));
    }

    partial void OnJoystickBindingDisplayChanged(string? value)
    {
        OnPropertyChanged(nameof(JoystickBindingDisplayResolved));
    }
}