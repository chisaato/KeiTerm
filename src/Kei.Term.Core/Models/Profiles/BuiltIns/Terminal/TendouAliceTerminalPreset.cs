namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class TendouAliceTerminalPreset
{
    public const string Id = "builtin-term-tendou-alice";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Tendou Alice (天童爱丽丝)",
        IsBuiltIn = true,
        Foreground = "#EDF3FC",
        Background = "#131722",
        CursorColor = "#23D2EA",
        SelectionBackground = "#55258DF0",
        AnsiColors =
        [
            "#181E2C", // Black
            "#EF4D56", // Red
            "#29D48C", // Green
            "#F1B838", // Yellow
            "#2D8FF4", // Blue
            "#A265EA", // Magenta
            "#23D2EA", // Cyan
            "#D8E2F2", // White
            "#485672", // Bright Black
            "#FF6B74", // Bright Red
            "#47EBB0", // Bright Green
            "#FFD15C", // Bright Yellow
            "#52A6FF", // Bright Blue
            "#BE86FF", // Bright Magenta
            "#5AE4F8", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}
