using System;
using System.Collections.Generic;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.Core.Models.Profiles;
using RoyalTerminal.Terminal.Theming;
using Xunit;

namespace Kei.Term.Tests;

// 终端配色适配器：颜色格式规范 + RoyalTerminal 主题桥接
public class TerminalThemeAdapterTests
{
    private static TerminalProfile CreateProfile(string ansi0 = "#010203", string selection = "#501D99F3")
    {
        TerminalProfile profile = new()
        {
            Id = "adapter-test",
            Name = "Adapter Test",
            Foreground = "#D4D4D4",
            Background = "#1E1E1E",
            CursorColor = "#FFFFFF",
            SelectionBackground = selection,
            AnsiColors = new string[16]
        };

        for (int i = 0; i < 16; i++)
        {
            profile.AnsiColors[i] = $"#{i:X2}{i:X2}{i:X2}";
        }

        profile.AnsiColors[0] = ansi0;
        return profile;
    }

    [Fact]
    public void TryParseArgb_AcceptsSixDigitAndEightDigit()
    {
        Assert.True(TerminalThemeAdapter.TryParseArgb("#1E1E1E", out uint six));
        Assert.Equal(0xFF1E1E1Eu, six);

        Assert.True(TerminalThemeAdapter.TryParseArgb("#501D99F3", out uint eight));
        Assert.Equal(0x501D99F3u, eight);

        // 小写输入同样接受
        Assert.True(TerminalThemeAdapter.TryParseArgb("#1e1e1e", out uint lower));
        Assert.Equal(0xFF1E1E1Eu, lower);
    }

    [Fact]
    public void TryParseArgb_RejectsUnsupportedInput()
    {
        // 5 位/7 位等非 3/6/8 长度不得猜测
        Assert.False(TerminalThemeAdapter.TryParseArgb("#12345", out _));
        Assert.False(TerminalThemeAdapter.TryParseArgb("#1234567", out _));
        Assert.False(TerminalThemeAdapter.TryParseArgb("1E1E1E", out _));
        Assert.False(TerminalThemeAdapter.TryParseArgb("#GGGGGG", out _));
        Assert.False(TerminalThemeAdapter.TryParseArgb(null, out _));
        Assert.False(TerminalThemeAdapter.TryParseArgb("", out _));
        Assert.False(TerminalThemeAdapter.TryParseArgb("   ", out _));
    }

    [Fact]
    public void ToArgb_LegacyThreeDigit_ExpandsInsteadOfCrashing()
    {
        // 兼容边界：既有 Avalonia Color.TryParse / 编辑弹窗正则接受 #RGB，
        // 新适配器必须最小兼容展开，不得无提示崩溃
        Assert.Equal(TerminalThemeAdapter.ToArgb("#11EE11"), TerminalThemeAdapter.ToArgb("#1E1"));
        Assert.Equal(0xFF11EE11u, TerminalThemeAdapter.ToArgb("#1E1"));
    }

    [Fact]
    public void ToArgb_MalformedInput_ThrowsWithClearMessage()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => TerminalThemeAdapter.ToArgb("#12345"));
        Assert.Contains("hex", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToSkColor_PreservesAlphaAndRgbIndependently()
    {
        // 00 通道
        Assert.Equal(0x00, TerminalThemeAdapter.ToSkColor(0x00123456u).Alpha);
        Assert.Equal(0x12, TerminalThemeAdapter.ToSkColor(0x00123456u).Red);
        Assert.Equal(0x34, TerminalThemeAdapter.ToSkColor(0x00123456u).Green);
        Assert.Equal(0x56, TerminalThemeAdapter.ToSkColor(0x00123456u).Blue);

        // 50 通道（Konsole 自动推导选区色目标值）
        Assert.Equal(0x50, TerminalThemeAdapter.ToSkColor(0x501D99F3u).Alpha);
        Assert.Equal(0x1D, TerminalThemeAdapter.ToSkColor(0x501D99F3u).Red);
        Assert.Equal(0x99, TerminalThemeAdapter.ToSkColor(0x501D99F3u).Green);
        Assert.Equal(0xF3, TerminalThemeAdapter.ToSkColor(0x501D99F3u).Blue);

        // FF 通道
        Assert.Equal(0xFF, TerminalThemeAdapter.ToSkColor(0xFF1D99F3u).Alpha);
    }

    [Theory]
    [InlineData("#000000", 0xFF000000u)]
    [InlineData("#00000000", 0x00000000u)]
    [InlineData("#501D99F3", 0x501D99F3u)]
    [InlineData("#FF1D99F3", 0xFF1D99F3u)]
    public void ToArgb_ChannelBoundaries_AreExact(string hex, uint expected)
    {
        Assert.Equal(expected, TerminalThemeAdapter.ToArgb(hex));
    }

    [Fact]
    public void ToRoyalTheme_MapsAnsiSlotZeroToFirstBase16Entry()
    {
        TerminalProfile profile = CreateProfile(ansi0: "#010203");

        TerminalTheme theme = TerminalThemeAdapter.ToRoyalTheme(profile);

        // Base16 顺序：ANSI 0..15 依次对应 base16[0..15]
        Assert.Equal(0xFF010203u, theme.Palette[0]);
        Assert.Equal(TerminalThemeAdapter.ToArgb(profile.AnsiColors[15]), theme.Palette[15]);
    }

    [Fact]
    public void ToRoyalTheme_ShortAnsiArray_FallsBackWithoutCrash()
    {
        TerminalProfile profile = CreateProfile();
        profile.AnsiColors = new[] { "#010203", "#040506" };

        TerminalTheme theme = TerminalThemeAdapter.ToRoyalTheme(profile);

        Assert.NotNull(theme);
        // 缺失槽位回退到内置默认，而不是抛异常或写入 0
        Assert.Equal(0xFF010203u, theme.Palette[0]);
        Assert.NotEqual(0u, theme.Palette[15]);
    }

    [Fact]
    public void ToRoyalTheme_Index16_IsCubeBlack_NotACopyOfAnsi0()
    {
        TerminalProfile profile = CreateProfile(ansi0: "#010203");

        TerminalTheme theme = TerminalThemeAdapter.ToRoyalTheme(profile);

        // xterm 色立方第一格是 (0,0,0)，不得把 ANSI 0 再刷一遍
        Assert.NotEqual(theme.Palette[0], theme.Palette[16]);
        Assert.Equal(0xFF000000u, theme.Palette[16]);
    }

    [Fact]
    public void ToRoyalTheme_Index255_IsXtermGrayRamp_NotAnsiWhite()
    {
        TerminalProfile profile = CreateProfile();
        profile.AnsiColors[15] = "#FFFFFF";

        TerminalTheme theme = TerminalThemeAdapter.ToRoyalTheme(profile);

        // 灰度阶 i=23：8 + 10*23 = 238。不是把 ANSI 白复制到 255。
        const uint expected = 0xFFEEEEEE;
        Assert.Equal(expected, theme.Palette[255]);
        Assert.NotEqual(theme.Palette[15], theme.Palette[255]);
        Assert.Equal((theme.Palette[255] >> 16) & 0xFF, (theme.Palette[255] >> 8) & 0xFF);
        Assert.Equal((theme.Palette[255] >> 8) & 0xFF, theme.Palette[255] & 0xFF);
    }

    [Fact]
    public void TerminalFontSnapshot_CopiesFallbackListInsteadOfAliasingIt()
    {
        List<string> source = ["Cascadia Mono", "Noto Sans Mono"];

        TerminalFontSnapshot snapshot = new("JetBrains Mono, Consolas, monospace", source, 14.0, true);

        Assert.Equal("JetBrains Mono", snapshot.PrimaryFontFamily);
        // 外部修改源列表不得影响快照，避免“假不可变”
        source.Add("Later Added");
        Assert.Equal(2, snapshot.FallbackFonts.Count);
        Assert.DoesNotContain("Later Added", snapshot.FallbackFonts);
    }

    // M1：主字体单一化规则必须与 TerminalTabViewModel.NormalizeFontFamilyName 一致
    [Theory]
    [InlineData("JetBrains Mono", "JetBrains Mono")]
    [InlineData("JetBrains Mono, Consolas, monospace", "JetBrains Mono")]
    [InlineData("  Cascadia Mono , Consolas ", "Cascadia Mono")]
    [InlineData("", "Noto Sans Mono")]
    [InlineData("   ", "Noto Sans Mono")]
    [InlineData(null, "Noto Sans Mono")]
    [InlineData(",Arial", "Noto Sans Mono")]
    public void NormalizePrimaryFontFamily_MatchesExistingRule(string? input, string expected)
    {
        Assert.Equal(expected, TerminalFontSnapshot.NormalizePrimaryFontFamily(input));
    }
}
