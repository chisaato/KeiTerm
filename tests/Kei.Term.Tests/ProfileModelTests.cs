using System;
using System.Linq;
using System.Text.RegularExpressions;
using Kei.Term.Core.Models.Profiles;
using Xunit;

namespace Kei.Term.Tests;

public class ProfileModelTests
{
    private static readonly Regex HexColorRegex = new(@"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    [Fact]
    public void GuiProfile_DefaultValues_AreValid()
    {
        var profile = new GuiProfile();

        Assert.False(string.IsNullOrWhiteSpace(profile.Id));
        Assert.Equal(string.Empty, profile.Name);
        Assert.False(profile.IsBuiltIn);
        Assert.Matches(HexColorRegex, profile.WindowBackground);
        Assert.Matches(HexColorRegex, profile.PanelBackground);
        Assert.Matches(HexColorRegex, profile.PanelAltBackground);
        Assert.Matches(HexColorRegex, profile.BorderBrush);
        Assert.Matches(HexColorRegex, profile.PrimaryText);
        Assert.Matches(HexColorRegex, profile.SecondaryText);
        Assert.Matches(HexColorRegex, profile.AccentColor);
        Assert.Matches(HexColorRegex, profile.AccentHover);
    }

    [Fact]
    public void TerminalProfile_DefaultValues_AreValidAndForegroundIsPureWhite()
    {
        var profile = new TerminalProfile();

        Assert.False(string.IsNullOrWhiteSpace(profile.Id));
        Assert.Equal(string.Empty, profile.Name);
        Assert.False(profile.IsBuiltIn);
        // Foreground 默认值必须是纯白 #FFFFFF
        Assert.Equal("#FFFFFF", profile.Foreground);
        Assert.Matches(HexColorRegex, profile.Background);
        Assert.Matches(HexColorRegex, profile.CursorColor);
        Assert.Matches(HexColorRegex, profile.SelectionBackground);

        // AnsiColors 默认长度为 16
        Assert.NotNull(profile.AnsiColors);
        Assert.Equal(16, profile.AnsiColors.Length);

        Assert.False(string.IsNullOrWhiteSpace(profile.FontFamily));
        Assert.True(profile.FontSize > 0);
        Assert.False(string.IsNullOrWhiteSpace(profile.FontWeight));
        Assert.True(profile.LineHeight > 0);
        Assert.True(profile.CursorBlink);
    }

    [Fact]
    public void TabBarSettings_DefaultValues_AreValid()
    {
        var settings = new TabBarSettings();

        Assert.Equal(TabPlacement.Top, settings.Placement);
        Assert.Matches(HexColorRegex, settings.ConnectingColor);
        Assert.Matches(HexColorRegex, settings.ConnectedColor);
        Assert.Matches(HexColorRegex, settings.DisconnectedColor);
        Assert.Matches(HexColorRegex, settings.ErrorColor);
    }

    [Fact]
    public void BuiltInPresets_DefaultGuiProfiles_ContainRequiredProfiles()
    {
        var presets = BuiltInPresets.DefaultGuiProfiles;

        Assert.NotNull(presets);
        Assert.NotEmpty(presets);

        // 必须包含 VS Code Dark (或 Kei Classic), One Dark, Nord, Solarized Dark 及 Veritas 4 款主题
        var names = presets.Select(p => p.Name).ToList();
        Assert.Contains(names, n => n.Contains("VS Code Dark", StringComparison.OrdinalIgnoreCase) || n.Contains("Kei Classic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("One Dark", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Nord", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Solarized Dark", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Omagari Hare", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Kagami Chihiro", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Konuri Maki", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Otose Kotama", StringComparison.OrdinalIgnoreCase));

        foreach (var profile in presets)
        {
            Assert.True(profile.IsBuiltIn);
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
            Assert.Matches(HexColorRegex, profile.WindowBackground);
            Assert.Matches(HexColorRegex, profile.PanelBackground);
            Assert.Matches(HexColorRegex, profile.PanelAltBackground);
            Assert.Matches(HexColorRegex, profile.BorderBrush);
            Assert.Matches(HexColorRegex, profile.PrimaryText);
            Assert.Matches(HexColorRegex, profile.SecondaryText);
            Assert.Matches(HexColorRegex, profile.AccentColor);
            Assert.Matches(HexColorRegex, profile.AccentHover);
        }
    }

    [Fact]
    public void BuiltInPresets_DefaultTerminalProfiles_ContainRequiredProfilesAndValidColors()
    {
        var presets = BuiltInPresets.DefaultTerminalProfiles;

        Assert.NotNull(presets);
        Assert.NotEmpty(presets);

        // 必须包含 Monokai, Dracula, Gruvbox, Solarized Dark, Solarized Light, Tomorrow Night 及 Veritas 4 款主题
        var names = presets.Select(p => p.Name).ToList();
        Assert.Contains(names, n => n.Contains("Monokai", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Dracula", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Gruvbox", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Solarized Dark", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Solarized Light", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Tomorrow Night", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Omagari Hare", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Kagami Chihiro", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Konuri Maki", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Otose Kotama", StringComparison.OrdinalIgnoreCase));

        foreach (var profile in presets)
        {
            Assert.True(profile.IsBuiltIn);
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
            Assert.Matches(HexColorRegex, profile.Foreground);
            Assert.Matches(HexColorRegex, profile.Background);
            Assert.Matches(HexColorRegex, profile.CursorColor);
            Assert.Matches(HexColorRegex, profile.SelectionBackground);

            Assert.NotNull(profile.AnsiColors);
            Assert.Equal(16, profile.AnsiColors.Length);
            for (int i = 0; i < 16; i++)
            {
                Assert.True(
                    HexColorRegex.IsMatch(profile.AnsiColors[i]),
                    $"Profile '{profile.Name}' has invalid hex color at ANSI index {i}: '{profile.AnsiColors[i]}'");
            }
        }
    }

    [Fact]
    public void BuiltInPresets_GetDefaultGuiProfile_ReturnsValidBuiltInProfile()
    {
        var defaultProfile = BuiltInPresets.GetDefaultGuiProfile();

        Assert.NotNull(defaultProfile);
        Assert.True(defaultProfile.IsBuiltIn);
        Assert.Contains(BuiltInPresets.DefaultGuiProfiles, p => p.Id == defaultProfile.Id);
    }

    [Fact]
    public void BuiltInPresets_GetDefaultTerminalProfile_ReturnsValidBuiltInProfile()
    {
        var defaultProfile = BuiltInPresets.GetDefaultTerminalProfile();

        Assert.NotNull(defaultProfile);
        Assert.True(defaultProfile.IsBuiltIn);
        Assert.Contains(BuiltInPresets.DefaultTerminalProfiles, p => p.Id == defaultProfile.Id);
    }

    [Fact]
    public void TerminalProfileDeepCopy_PreservesLegacyFontFields()
    {
        var original = new TerminalProfile
        {
            Id = "legacy-id",
            Name = "Legacy",
            IsBuiltIn = true,
            Foreground = "#112233",
            Background = "#445566",
            CursorColor = "#778899",
            SelectionBackground = "#50AABBCC",
            AnsiColors = ["#000000", "#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#777777",
                          "#888888", "#999999", "#AAAAAA", "#BBBBBB", "#CCCCCC", "#DDDDDD", "#EEEEEE", "#FFFFFF"],
            // 旧字体字段必须原样保留，IsItalic 不可漏
            FontFamily = "Legacy Mono",
            FontSize = 13.5,
            FontWeight = "SemiBold",
            IsItalic = true,
            LineHeight = 1.35,
            CursorBlink = false
        };

        TerminalProfile copy = original.DeepCopy();

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.IsBuiltIn, copy.IsBuiltIn);
        Assert.Equal(original.Foreground, copy.Foreground);
        Assert.Equal(original.Background, copy.Background);
        Assert.Equal(original.CursorColor, copy.CursorColor);
        Assert.Equal(original.SelectionBackground, copy.SelectionBackground);
        Assert.Equal(original.FontFamily, copy.FontFamily);
        Assert.Equal(original.FontSize, copy.FontSize);
        Assert.Equal(original.FontWeight, copy.FontWeight);
        Assert.True(copy.IsItalic);
        Assert.Equal(original.LineHeight, copy.LineHeight);
        Assert.False(copy.CursorBlink);

        // 数组必须隔离，改副本不得影响原对象
        Assert.NotSame(original.AnsiColors, copy.AnsiColors);
        copy.AnsiColors[0] = "#FF0000";
        Assert.Equal("#000000", original.AnsiColors[0]);
    }
}
