using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Core.Services;

public sealed class ProfileBundle
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("selectedGuiProfileId")]
    public string? SelectedGuiProfileId { get; set; }

    [JsonPropertyName("defaultTerminalProfileId")]
    public string? DefaultTerminalProfileId { get; set; }

    [JsonPropertyName("tabBarSettings")]
    public TabBarSettings TabBarSettings { get; set; } = new();

    [JsonPropertyName("guiProfiles")]
    public List<GuiProfile> GuiProfiles { get; set; } = [];

    [JsonPropertyName("terminalProfiles")]
    public List<TerminalProfile> TerminalProfiles { get; set; } = [];
}

public static class ProfileBundleSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ProfileBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    public static ProfileBundle Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var bundle = JsonSerializer.Deserialize<ProfileBundle>(json, JsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize profile bundle.");

        // 防御性补齐与 Fallback 兜底处理
        bundle.TabBarSettings ??= new TabBarSettings();
        EnsureTabBarSettingsValid(bundle.TabBarSettings);

        bundle.GuiProfiles ??= [];
        foreach (var gui in bundle.GuiProfiles)
        {
            EnsureGuiProfileValid(gui);
        }

        bundle.TerminalProfiles ??= [];
        foreach (var term in bundle.TerminalProfiles)
        {
            EnsureTerminalProfileValid(term);
        }

        return bundle;
    }

    public static List<T> MergeProfiles<T>(
        IEnumerable<T> existingList,
        IEnumerable<T> incomingList,
        bool overwrite) where T : class
    {
        ArgumentNullException.ThrowIfNull(existingList);
        ArgumentNullException.ThrowIfNull(incomingList);

        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in existingList)
        {
            var id = GetProfileId(item);
            if (!string.IsNullOrWhiteSpace(id))
            {
                result[id] = item;
            }
        }

        foreach (var item in incomingList)
        {
            var id = GetProfileId(item);
            if (string.IsNullOrWhiteSpace(id)) continue;

            if (overwrite || !result.ContainsKey(id))
            {
                result[id] = item;
            }
        }

        return result.Values.ToList();
    }

    private static string? GetProfileId<T>(T item) => item switch
    {
        GuiProfile g => g.Id,
        TerminalProfile t => t.Id,
        _ => null
    };

    private static void EnsureTabBarSettingsValid(TabBarSettings s)
    {
        var fb = new TabBarSettings();
        if (string.IsNullOrWhiteSpace(s.ConnectingColor)) s.ConnectingColor = fb.ConnectingColor;
        if (string.IsNullOrWhiteSpace(s.ConnectedColor)) s.ConnectedColor = fb.ConnectedColor;
        if (string.IsNullOrWhiteSpace(s.DisconnectedColor)) s.DisconnectedColor = fb.DisconnectedColor;
        if (string.IsNullOrWhiteSpace(s.ErrorColor)) s.ErrorColor = fb.ErrorColor;
    }

    private static void EnsureGuiProfileValid(GuiProfile p)
    {
        var fb = new GuiProfile();
        if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString();
        if (p.Name == null) p.Name = string.Empty;

        if (string.IsNullOrWhiteSpace(p.WindowBackground)) p.WindowBackground = fb.WindowBackground;
        if (string.IsNullOrWhiteSpace(p.PanelBackground)) p.PanelBackground = fb.PanelBackground;
        if (string.IsNullOrWhiteSpace(p.PanelAltBackground)) p.PanelAltBackground = fb.PanelAltBackground;
        if (string.IsNullOrWhiteSpace(p.BorderBrush)) p.BorderBrush = fb.BorderBrush;
        if (string.IsNullOrWhiteSpace(p.PrimaryText)) p.PrimaryText = fb.PrimaryText;
        if (string.IsNullOrWhiteSpace(p.SecondaryText)) p.SecondaryText = fb.SecondaryText;
        if (string.IsNullOrWhiteSpace(p.AccentColor)) p.AccentColor = fb.AccentColor;
        if (string.IsNullOrWhiteSpace(p.AccentHover)) p.AccentHover = fb.AccentHover;
    }

    private static void EnsureTerminalProfileValid(TerminalProfile p)
    {
        var fb = new TerminalProfile();
        if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString();
        if (p.Name == null) p.Name = string.Empty;

        if (string.IsNullOrWhiteSpace(p.Foreground)) p.Foreground = fb.Foreground;
        if (string.IsNullOrWhiteSpace(p.Background)) p.Background = fb.Background;
        if (string.IsNullOrWhiteSpace(p.CursorColor)) p.CursorColor = fb.CursorColor;
        if (string.IsNullOrWhiteSpace(p.SelectionBackground)) p.SelectionBackground = fb.SelectionBackground;
        if (string.IsNullOrWhiteSpace(p.FontFamily)) p.FontFamily = fb.FontFamily;
        if (p.FontSize <= 0) p.FontSize = fb.FontSize;
        if (string.IsNullOrWhiteSpace(p.FontWeight)) p.FontWeight = fb.FontWeight;
        if (p.LineHeight <= 0) p.LineHeight = fb.LineHeight;

        // ANSI 16 色矩阵严密补齐
        var defaultPreset = BuiltInPresets.GetDefaultTerminalProfile();
        if (p.AnsiColors == null || p.AnsiColors.Length != 16)
        {
            var colors = new string[16];
            for (int i = 0; i < 16; i++)
            {
                if (p.AnsiColors != null && i < p.AnsiColors.Length && !string.IsNullOrWhiteSpace(p.AnsiColors[i]))
                {
                    colors[i] = p.AnsiColors[i];
                }
                else
                {
                    colors[i] = defaultPreset.AnsiColors[i];
                }
            }
            p.AnsiColors = colors;
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                if (string.IsNullOrWhiteSpace(p.AnsiColors[i]))
                {
                    p.AnsiColors[i] = defaultPreset.AnsiColors[i];
                }
            }
        }
    }
}
