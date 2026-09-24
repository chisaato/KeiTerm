using System.Collections.Generic;

namespace Kei.Term.Core.Models.Profiles;

public static class BuiltInPresets
{
    // 内置 GUI 外观预置 ID
    public const string GuiVsCodeDarkId = "builtin-gui-vscode-dark";
    public const string GuiOneDarkId = "builtin-gui-one-dark";
    public const string GuiNordId = "builtin-gui-nord";
    public const string GuiSolarizedDarkId = "builtin-gui-solarized-dark";

    // 内置 Terminal 终端预置 ID
    public const string TerminalMonokaiId = "builtin-term-monokai";
    public const string TerminalDraculaId = "builtin-term-dracula";
    public const string TerminalGruvboxDarkId = "builtin-term-gruvbox-dark";
    public const string TerminalSolarizedDarkId = "builtin-term-solarized-dark";
    public const string TerminalSolarizedLightId = "builtin-term-solarized-light";
    public const string TerminalTomorrowNightId = "builtin-term-tomorrow-night";

    public static IReadOnlyList<GuiProfile> DefaultGuiProfiles { get; } = new List<GuiProfile>
    {
        new()
        {
            Id = GuiVsCodeDarkId,
            Name = "VS Code Dark",
            IsBuiltIn = true,
            WindowBackground = "#181818",
            PanelBackground = "#1F1F1F",
            PanelAltBackground = "#252526",
            BorderBrush = "#2D2D2D",
            PrimaryText = "#CCCCCC",
            SecondaryText = "#858585",
            AccentColor = "#0E639C",
            AccentHover = "#1177BB"
        },
        new()
        {
            Id = GuiOneDarkId,
            Name = "One Dark",
            IsBuiltIn = true,
            WindowBackground = "#282C34",
            PanelBackground = "#21252B",
            PanelAltBackground = "#2C313A",
            BorderBrush = "#181A1F",
            PrimaryText = "#ABB2BF",
            SecondaryText = "#5C6370",
            AccentColor = "#61AFEF",
            AccentHover = "#4D9CE6"
        },
        new()
        {
            Id = GuiNordId,
            Name = "Nord",
            IsBuiltIn = true,
            WindowBackground = "#2E3440",
            PanelBackground = "#3B4252",
            PanelAltBackground = "#434C5E",
            BorderBrush = "#4C566A",
            PrimaryText = "#ECEFF4",
            SecondaryText = "#D8DEE9",
            AccentColor = "#88C0D0",
            AccentHover = "#81A1C1"
        },
        new()
        {
            Id = GuiSolarizedDarkId,
            Name = "Solarized Dark",
            IsBuiltIn = true,
            WindowBackground = "#002B36",
            PanelBackground = "#073642",
            PanelAltBackground = "#0F4654",
            BorderBrush = "#586E75",
            PrimaryText = "#93A1A1",
            SecondaryText = "#657B83",
            AccentColor = "#268BD2",
            AccentHover = "#2AA198"
        }
    }.AsReadOnly();

    public static IReadOnlyList<TerminalProfile> DefaultTerminalProfiles { get; } = new List<TerminalProfile>
    {
        // 1. Monokai (高对比纯白)
        new()
        {
            Id = TerminalMonokaiId,
            Name = "Monokai",
            IsBuiltIn = true,
            Foreground = "#FFFFFF",
            Background = "#272822",
            CursorColor = "#F8F8F0",
            SelectionBackground = "#49483E",
            AnsiColors =
            [
                "#272822", // Black
                "#F92672", // Red
                "#A6E22E", // Green
                "#F4BF75", // Yellow
                "#66D9EF", // Blue
                "#AE81FF", // Magenta
                "#A1EFE4", // Cyan
                "#F8F8F2", // White
                "#75715E", // Bright Black
                "#F92672", // Bright Red
                "#A6E22E", // Bright Green
                "#F4BF75", // Bright Yellow
                "#66D9EF", // Bright Blue
                "#AE81FF", // Bright Magenta
                "#A1EFE4", // Bright Cyan
                "#F9F8F5"  // Bright White
            ]
        },
        // 2. Dracula
        new()
        {
            Id = TerminalDraculaId,
            Name = "Dracula",
            IsBuiltIn = true,
            Foreground = "#F8F8F2",
            Background = "#282A36",
            CursorColor = "#F8F8F2",
            SelectionBackground = "#44475A",
            AnsiColors =
            [
                "#21222C", // Black
                "#FF5555", // Red
                "#50FA7B", // Green
                "#F1FA8C", // Yellow
                "#BD93F9", // Blue
                "#FF79C6", // Magenta
                "#8BE9FD", // Cyan
                "#F8F8F2", // White
                "#6272A4", // Bright Black
                "#FF6E6E", // Bright Red
                "#69FF94", // Bright Green
                "#FFFFA5", // Bright Yellow
                "#D6ACFF", // Bright Blue
                "#FF92DF", // Bright Magenta
                "#A4FFFF", // Bright Cyan
                "#FFFFFF"  // Bright White
            ]
        },
        // 3. Gruvbox Dark
        new()
        {
            Id = TerminalGruvboxDarkId,
            Name = "Gruvbox Dark",
            IsBuiltIn = true,
            Foreground = "#EBDBB2",
            Background = "#282828",
            CursorColor = "#EBDBB2",
            SelectionBackground = "#504945",
            AnsiColors =
            [
                "#282828", // Black
                "#CC241D", // Red
                "#98971A", // Green
                "#D79921", // Yellow
                "#458588", // Blue
                "#B16286", // Magenta
                "#689D6A", // Cyan
                "#A89984", // White
                "#928374", // Bright Black
                "#FB4934", // Bright Red
                "#B8BB26", // Bright Green
                "#FABD2F", // Bright Yellow
                "#83A598", // Bright Blue
                "#D3869B", // Bright Magenta
                "#8EC07C", // Bright Cyan
                "#EBDBB2"  // Bright White
            ]
        },
        // 4. Solarized Dark
        new()
        {
            Id = TerminalSolarizedDarkId,
            Name = "Solarized Dark",
            IsBuiltIn = true,
            Foreground = "#93A1A1",
            Background = "#002B36",
            CursorColor = "#93A1A1",
            SelectionBackground = "#073642",
            AnsiColors =
            [
                "#073642", // Black
                "#DC322F", // Red
                "#859900", // Green
                "#B58900", // Yellow
                "#268BD2", // Blue
                "#D33682", // Magenta
                "#2AA198", // Cyan
                "#EEE8D5", // White
                "#002B36", // Bright Black
                "#CB4B16", // Bright Red
                "#586E75", // Bright Green
                "#657B83", // Bright Yellow
                "#839496", // Bright Blue
                "#6C71C4", // Bright Magenta
                "#93A1A1", // Bright Cyan
                "#FDF6E3"  // Bright White
            ]
        },
        // 5. Solarized Light
        new()
        {
            Id = TerminalSolarizedLightId,
            Name = "Solarized Light",
            IsBuiltIn = true,
            Foreground = "#586E75",
            Background = "#FDF6E3",
            CursorColor = "#586E75",
            SelectionBackground = "#EEE8D5",
            AnsiColors =
            [
                "#073642", // Black
                "#DC322F", // Red
                "#859900", // Green
                "#B58900", // Yellow
                "#268BD2", // Blue
                "#D33682", // Magenta
                "#2AA198", // Cyan
                "#EEE8D5", // White
                "#002B36", // Bright Black
                "#CB4B16", // Bright Red
                "#586E75", // Bright Green
                "#657B83", // Bright Yellow
                "#839496", // Bright Blue
                "#6C71C4", // Bright Magenta
                "#93A1A1", // Bright Cyan
                "#FDF6E3"  // Bright White
            ]
        },
        // 6. Tomorrow Night
        new()
        {
            Id = TerminalTomorrowNightId,
            Name = "Tomorrow Night",
            IsBuiltIn = true,
            Foreground = "#C5C8C6",
            Background = "#1D1F21",
            CursorColor = "#C5C8C6",
            SelectionBackground = "#373B41",
            AnsiColors =
            [
                "#1D1F21", // Black
                "#CC6666", // Red
                "#B5BD68", // Green
                "#F0C674", // Yellow
                "#81A2BE", // Blue
                "#B294BB", // Magenta
                "#8ABEB7", // Cyan
                "#C5C8C6", // White
                "#969896", // Bright Black
                "#CC6666", // Bright Red
                "#B5BD68", // Bright Green
                "#F0C674", // Bright Yellow
                "#81A2BE", // Bright Blue
                "#B294BB", // Bright Magenta
                "#8ABEB7", // Bright Cyan
                "#FFFFFF"  // Bright White
            ]
        }
    }.AsReadOnly();

    public static GuiProfile GetDefaultGuiProfile() => DefaultGuiProfiles[0];

    public static TerminalProfile GetDefaultTerminalProfile() => DefaultTerminalProfiles[0];
}
