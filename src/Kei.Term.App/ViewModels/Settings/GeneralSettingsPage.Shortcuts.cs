using Avalonia.Input;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.ViewModels.Settings;

public partial class GeneralSettingsPage
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandPaletteShortcutLabel))]
    private string _commandPaletteShortcut = AppShortcuts.DefaultCommandPaletteShortcut;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandPaletteShortcutLabel))]
    private bool _isRecordingCommandPaletteShortcut;
    [ObservableProperty] private string? _commandPaletteShortcutError;

    public string CommandPaletteShortcutLabel => IsRecordingCommandPaletteShortcut
        ? Strings.Get("Settings.General.ShortcutRecording")
        : AppShortcuts.Label(AppShortcuts.CommandPalette(CommandPaletteShortcut));
    public string NewSessionShortcutLabel => AppShortcuts.Label(AppShortcuts.NewSession);
    public string QuickConnectShortcutLabel => AppShortcuts.Label(AppShortcuts.QuickConnect);
    public string ConnectSavedSessionShortcutLabel => AppShortcuts.Label(AppShortcuts.ConnectSavedSession);
    public string NewTabShortcutLabel => AppShortcuts.Label(AppShortcuts.NewTab);

    [RelayCommand]
    private void StartRecordingCommandPaletteShortcut()
    {
        CommandPaletteShortcutError = null;
        IsRecordingCommandPaletteShortcut = !IsRecordingCommandPaletteShortcut;
    }

    [RelayCommand]
    private void ResetCommandPaletteShortcut()
    {
        IsRecordingCommandPaletteShortcut = false;
        CommandPaletteShortcutError = null;
        CommandPaletteShortcut = AppShortcuts.DefaultCommandPaletteShortcut;
    }

    public void RecordCommandPaletteShortcut(Key key, KeyModifiers modifiers, PhysicalKey physicalKey = PhysicalKey.None)
    {
        if (!IsRecordingCommandPaletteShortcut) return;
        // macOS Option 会生成 π 等字符；录制快捷键时保留实际字母键，避免把符号误识别成 A。
        if (OperatingSystem.IsMacOS() && (modifiers & KeyModifiers.Alt) != 0 && physicalKey != PhysicalKey.None)
            key = physicalKey.ToQwertyKey();
        if (key == Key.Escape)
        {
            IsRecordingCommandPaletteShortcut = false;
            CommandPaletteShortcutError = null;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        string value = AppShortcuts.Serialize(new KeyGesture(key, modifiers));
        if (!AppShortcuts.TryParsePaletteShortcut(value, out _))
        {
            CommandPaletteShortcutError = Strings.Get("Settings.General.ShortcutInvalid");
            return;
        }
        CommandPaletteShortcut = value;
        CommandPaletteShortcutError = null;
        IsRecordingCommandPaletteShortcut = false;
    }

    public void SetCommandPaletteShortcut(string? value)
    {
        CommandPaletteShortcut = AppShortcuts.TryParsePaletteShortcut(value, out _) ? value! : AppShortcuts.DefaultCommandPaletteShortcut;
        IsRecordingCommandPaletteShortcut = false;
        CommandPaletteShortcutError = null;
    }
}
