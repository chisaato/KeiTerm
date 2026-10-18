namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class VeritasMakiTerminalPreset
{
    public const string Id = "builtin-term-veritas-maki";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Konuri Maki",
        IsBuiltIn = true,
        Foreground = "#F6F5F8",
        Background = "#1C1B26",
        CursorColor = "#C83E44",
        SelectionBackground = "#C83E4438",
        AnsiColors =
        [
            "#1C1B26", // Black (Jacket dark violet)
            "#C83E44", // Red (Hair vermilion)
            "#2CC985", // Green (Graffiti spray green)
            "#FFD034", // Yellow (Graffiti yellow)
            "#2F8BE6", // Blue (Tie blue)
            "#E63985", // Magenta (Graffiti pink/magenta)
            "#32F4FB", // Cyan (Eye bright cyan)
            "#DDDCE4", // White
            "#544F6C", // Bright Black (Muted dark violet)
            "#E04D54", // Bright Red (Bright hair vermilion)
            "#4AE29D", // Bright Green
            "#FFE054", // Bright Yellow
            "#4D9FF0", // Bright Blue
            "#F2589D", // Bright Magenta
            "#60F7FD", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}

