namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class GruvboxTerminalPreset
{
    public const string Id = "builtin-term-gruvbox-dark";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
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
    };
}
