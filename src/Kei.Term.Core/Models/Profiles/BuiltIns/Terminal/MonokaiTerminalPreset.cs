namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class MonokaiTerminalPreset
{
    public const string Id = "builtin-term-monokai";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
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
    };
}
