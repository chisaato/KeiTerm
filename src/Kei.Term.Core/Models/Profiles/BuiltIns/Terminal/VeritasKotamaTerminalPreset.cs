namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class VeritasKotamaTerminalPreset
{
    public const string Id = "builtin-term-veritas-kotama";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Otose Kotama",
        IsBuiltIn = true,
        Foreground = "#F4F0E8",
        Background = "#1A1A1A",
        CursorColor = "#DB922C",
        SelectionBackground = "#DB922C38",
        AnsiColors =
        [
            "#1A1A1A", // Black (Coat dark charcoal)
            "#DE5353", // Red
            "#43B87A", // Green
            "#DB922C", // Yellow (Coat amber/orange)
            "#3B92E0", // Blue
            "#C070DC", // Magenta (Eye soft lilac/violet)
            "#32C2E8", // Cyan (Earphone glow & tie cyan)
            "#DCD6CE", // White
            "#5C5753", // Bright Black (Muted warm charcoal)
            "#EE6868", // Bright Red
            "#5DD194", // Bright Green
            "#ECA946", // Bright Yellow
            "#5AA6EE", // Bright Blue
            "#D585EF", // Bright Magenta (Eye vivid lilac)
            "#55D5F8", // Bright Cyan
            "#FFFFFF"  // Bright White (Hair white/cream highlight)
        ]
    };
}

