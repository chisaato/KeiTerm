using System;
using Avalonia.Input;

namespace Kei.Term.App.Helpers;

// 菜单、工具提示与空态共享实际手势，避免平台文案和按键绑定各自演变。
public static class AppShortcuts
{
    private static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS()
        ? KeyModifiers.Meta : KeyModifiers.Control;

    public static KeyGesture NewSession { get; } = new(Key.N, PrimaryModifier);
    public static KeyGesture NewTab { get; } = new(Key.T, PrimaryModifier);
    public static KeyGesture CloseTab { get; } = new(Key.W, PrimaryModifier);
    public static KeyGesture QuitApplication { get; } = new(Key.Q, PrimaryModifier);
    public static KeyGesture ConnectSavedSession { get; } = new(Key.O, PrimaryModifier);
    public const string DefaultCommandPaletteShortcut = "Primary+Shift+P";
    public static KeyGesture QuickConnect { get; } = OperatingSystem.IsMacOS()
        ? new(Key.K, KeyModifiers.Meta) : new(Key.Q, KeyModifiers.Control);
    public static KeyGesture Cut { get; } = new(Key.X, PrimaryModifier);
    public static KeyGesture Copy { get; } = new(Key.C, PrimaryModifier);
    public static KeyGesture Paste { get; } = new(Key.V, PrimaryModifier);
    public static KeyGesture SelectAll { get; } = new(Key.A, PrimaryModifier);

    public static KeyGesture CommandPalette(string? shortcut)
        => TryParsePaletteShortcut(shortcut, out KeyGesture? gesture) ? gesture! : Parse(DefaultCommandPaletteShortcut);

    private static KeyGesture Parse(string value) => KeyGesture.Parse(value.Replace("Primary", OperatingSystem.IsMacOS() ? "Meta" : "Ctrl", StringComparison.OrdinalIgnoreCase));

    public static bool TryParsePaletteShortcut(string? value, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            KeyGesture candidate = Parse(value);
            if (candidate.Key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape
                || (candidate.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) == 0)
                return false;

            // 保留连接、标签及标准编辑/窗口操作，避免自定义键截走输入框或终端的常用操作。
            if (candidate.KeyModifiers == PrimaryModifier && candidate.Key is
                Key.N or Key.K or Key.Q or Key.O or Key.T or Key.W or Key.C or Key.X or Key.V or Key.A or Key.Z or Key.Y or Key.F or Key.OemComma)
                return false;
            gesture = candidate;
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static string Serialize(KeyGesture gesture)
        => gesture.ToString().Replace(OperatingSystem.IsMacOS() ? "Meta" : "Ctrl", "Primary", StringComparison.Ordinal);

    public static string Label(KeyGesture gesture)
    {
        if (!OperatingSystem.IsMacOS()) return gesture.ToString("p", null);
        KeyModifiers modifiers = gesture.KeyModifiers;
        return ((modifiers & KeyModifiers.Control) != 0 ? "⌃" : "")
            + ((modifiers & KeyModifiers.Alt) != 0 ? "⌥" : "")
            + ((modifiers & KeyModifiers.Shift) != 0 ? "⇧" : "")
            + ((modifiers & KeyModifiers.Meta) != 0 ? "⌘" : "") + gesture.Key;
    }
}
