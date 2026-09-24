using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class ProfileBundleSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTrip_PreservesAllData()
    {
        var bundle = new ProfileBundle
        {
            Version = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            SelectedGuiProfileId = BuiltInPresets.GuiNordId,
            DefaultTerminalProfileId = BuiltInPresets.TerminalDraculaId,
            TabBarSettings = new TabBarSettings
            {
                Placement = TabPlacement.Bottom,
                ConnectingColor = "#112233",
                ConnectedColor = "#445566",
                DisconnectedColor = "#778899",
                ErrorColor = "#AABBCC"
            },
            GuiProfiles =
            [
                new GuiProfile
                {
                    Id = "custom-gui-1",
                    Name = "My Dark",
                    WindowBackground = "#101010",
                    PanelBackground = "#151515",
                    PanelAltBackground = "#202020",
                    BorderBrush = "#303030",
                    PrimaryText = "#E0E0E0",
                    SecondaryText = "#909090",
                    AccentColor = "#007ACC",
                    AccentHover = "#0098FF",
                    IsBuiltIn = false
                }
            ],
            TerminalProfiles =
            [
                new TerminalProfile
                {
                    Id = "custom-term-1",
                    Name = "My Terminal",
                    Foreground = "#FFFFFF",
                    Background = "#050505",
                    CursorColor = "#00FF00",
                    SelectionBackground = "#333333",
                    FontFamily = "JetBrains Mono",
                    FontSize = 15.0,
                    FontWeight = "Bold",
                    IsItalic = true,
                    LineHeight = 1.3,
                    CursorBlink = false,
                    IsBuiltIn = false,
                    AnsiColors =
                    [
                        "#000000", "#111111", "#222222", "#333333",
                        "#444444", "#555555", "#666666", "#777777",
                        "#888888", "#999999", "#AAAAAA", "#BBBBBB",
                        "#CCCCCC", "#DDDDDD", "#EEEEEE", "#FFFFFF"
                    ]
                }
            ]
        };

        var json = ProfileBundleSerializer.Serialize(bundle);
        Assert.False(string.IsNullOrWhiteSpace(json));

        var restored = ProfileBundleSerializer.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Equal(bundle.Version, restored.Version);
        Assert.Equal(bundle.SelectedGuiProfileId, restored.SelectedGuiProfileId);
        Assert.Equal(bundle.DefaultTerminalProfileId, restored.DefaultTerminalProfileId);

        Assert.Equal(TabPlacement.Bottom, restored.TabBarSettings.Placement);
        Assert.Equal("#112233", restored.TabBarSettings.ConnectingColor);

        var restoredGui = Assert.Single(restored.GuiProfiles);
        Assert.Equal("custom-gui-1", restoredGui.Id);
        Assert.Equal("My Dark", restoredGui.Name);
        Assert.Equal("#101010", restoredGui.WindowBackground);

        var restoredTerm = Assert.Single(restored.TerminalProfiles);
        Assert.Equal("custom-term-1", restoredTerm.Id);
        Assert.Equal("#FFFFFF", restoredTerm.Foreground);
        Assert.Equal("JetBrains Mono", restoredTerm.FontFamily);
        Assert.Equal(16, restoredTerm.AnsiColors.Length);
        Assert.Equal("#FFFFFF", restoredTerm.AnsiColors[15]);
    }

    [Fact]
    public void Deserialize_SparseJson_FillsFallbacksAndEnsures16AnsiColors()
    {
        // 极度残缺的 JSON
        var sparseJson = """
        {
            "version": 1,
            "guiProfiles": [
                {
                    "name": "Sparse GUI"
                }
            ],
            "terminalProfiles": [
                {
                    "name": "Sparse Terminal",
                    "ansiColors": ["#111111", "#222222"]
                },
                {
                    "name": "Null ANSI Terminal",
                    "ansiColors": null
                }
            ]
        }
        """;

        var bundle = ProfileBundleSerializer.Deserialize(sparseJson);
        Assert.NotNull(bundle);
        Assert.NotNull(bundle.TabBarSettings);
        Assert.Equal(TabPlacement.Top, bundle.TabBarSettings.Placement);

        var gui = Assert.Single(bundle.GuiProfiles);
        Assert.Equal("Sparse GUI", gui.Name);
        Assert.False(string.IsNullOrWhiteSpace(gui.Id));
        // 缺失颜色应被补齐为兜底默认
        Assert.False(string.IsNullOrWhiteSpace(gui.WindowBackground));
        Assert.False(string.IsNullOrWhiteSpace(gui.AccentColor));

        Assert.Equal(2, bundle.TerminalProfiles.Count);

        var term1 = bundle.TerminalProfiles[0];
        Assert.Equal("Sparse Terminal", term1.Name);
        Assert.Equal("#FFFFFF", term1.Foreground);
        Assert.Equal(16, term1.AnsiColors.Length);
        Assert.Equal("#111111", term1.AnsiColors[0]);
        Assert.Equal("#222222", term1.AnsiColors[1]);
        // 后续不足的索引应由默认兜底颜色填充且非空
        for (int i = 0; i < 16; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(term1.AnsiColors[i]));
        }

        var term2 = bundle.TerminalProfiles[1];
        Assert.Equal("Null ANSI Terminal", term2.Name);
        Assert.NotNull(term2.AnsiColors);
        Assert.Equal(16, term2.AnsiColors.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(term2.AnsiColors[i]));
        }
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsOrReturnsNull()
    {
        var invalidJson = "{ invalid json content ...";
        Assert.ThrowsAny<Exception>(() => ProfileBundleSerializer.Deserialize(invalidJson));
    }

    [Fact]
    public void MergeProfiles_HandlesOverwriteAndMergeCorrectly()
    {
        var existingGui = new List<GuiProfile>
        {
            new() { Id = "gui-1", Name = "Existing 1", WindowBackground = "#111111" }
        };

        var incomingGui = new List<GuiProfile>
        {
            new() { Id = "gui-1", Name = "Incoming 1 Overwritten", WindowBackground = "#999999" },
            new() { Id = "gui-2", Name = "Incoming 2", WindowBackground = "#222222" }
        };

        // 1. 合并（仅新增，保留已有）
        var merged = ProfileBundleSerializer.MergeProfiles(existingGui, incomingGui, overwrite: false);
        Assert.Equal(2, merged.Count);
        Assert.Equal("Existing 1", merged.First(p => p.Id == "gui-1").Name);
        Assert.Equal("Incoming 2", merged.First(p => p.Id == "gui-2").Name);

        // 2. 覆盖（同 ID 覆盖，不同 ID 追加）
        var overwritten = ProfileBundleSerializer.MergeProfiles(existingGui, incomingGui, overwrite: true);
        Assert.Equal(2, overwritten.Count);
        Assert.Equal("Incoming 1 Overwritten", overwritten.First(p => p.Id == "gui-1").Name);
        Assert.Equal("Incoming 2", overwritten.First(p => p.Id == "gui-2").Name);
    }
}
