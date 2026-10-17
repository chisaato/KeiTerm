namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class TendouKeiTerminalPreset
{
    public const string Id = "builtin-term-tendou-kei";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Tendou Kei (天童凯伊)",
        IsBuiltIn = true,
        Foreground = "#F4F3F8",
        Background = "#14171E",
        CursorColor = "#FF66A6",
        SelectionBackground = "#55E83674",
        AnsiColors =
        [
            "#1C202A", // Black
            "#E83674", // Red
            "#2EC67C", // Green
            "#EAA83B", // Yellow
            "#388EE6", // Blue
            "#E04FB0", // Magenta
            "#38C2EE", // Cyan
            "#D5DBE5", // White
            "#50596B", // Bright Black
            "#FF5792", // Bright Red
            "#43E396", // Bright Green
            "#FFC25C", // Bright Yellow
            "#58A6FF", // Bright Blue
            "#FF66C4", // Bright Magenta
            "#64DFFF", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}
