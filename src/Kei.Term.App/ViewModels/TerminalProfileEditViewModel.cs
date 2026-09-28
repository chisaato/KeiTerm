using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.ViewModels;

public sealed partial class TerminalProfileEditViewModel : ObservableObject
{
    private static readonly Regex HexColorRegex = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isBuiltIn;

    // 核心基础色 (Hex)
    [ObservableProperty]
    private string _background = "#1E1E1E";

    [ObservableProperty]
    private string _foreground = "#FFFFFF";

    [ObservableProperty]
    private string _cursorColor = "#FFFFFF";

    [ObservableProperty]
    private string _selectionBackground = "#264F78";

    // 活跃选中的调色目标 ("Background", "Foreground", "Cursor", "Selection", 或 "Ansi0" ~ "Ansi15")
    [ObservableProperty]
    private string _activeColorTarget = "Background";

    [ObservableProperty]
    private string _activeHex = "#1E1E1E";

    [ObservableProperty]
    private IBrush _activeBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));

    // 16 色 ANSI Hex 列表
    public ObservableCollection<string> AnsiColors { get; } = [];

    // 便于 XAML 绑定的 SolidColorBrush 资源
    [ObservableProperty]
    private IBrush _backgroundBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));

    [ObservableProperty]
    private IBrush _foregroundBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));

    [ObservableProperty]
    private IBrush _cursorBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));

    [ObservableProperty]
    private IBrush _selectionBrush = new SolidColorBrush(Color.Parse("#264F78"));

    public ObservableCollection<IBrush> AnsiBrushes { get; } = [];

    public bool IsConfirmed { get; private set; }

    public TerminalProfile ResultProfile { get; private set; }

    public TerminalProfileEditViewModel(TerminalProfile? sourceProfile = null)
    {
        ResultProfile = sourceProfile ?? new TerminalProfile();
        InitFromProfile(ResultProfile);
    }

    private void InitFromProfile(TerminalProfile profile)
    {
        Name = profile.IsBuiltIn ? $"{profile.Name} (副本)" : profile.Name;
        IsBuiltIn = false; // 用户编辑后均为自定义副本或自定义主题

        Background = NormalizeHex(profile.Background, "#1E1E1E");
        Foreground = NormalizeHex(profile.Foreground, "#FFFFFF");
        CursorColor = NormalizeHex(profile.CursorColor, "#FFFFFF");
        SelectionBackground = NormalizeHex(profile.SelectionBackground, "#264F78");

        AnsiColors.Clear();
        AnsiBrushes.Clear();

        string[] sourceAnsi = profile.AnsiColors != null && profile.AnsiColors.Length == 16
            ? profile.AnsiColors
            : new string[16];

        for (int i = 0; i < 16; i++)
        {
            string hex = NormalizeHex(sourceAnsi[i], i < 8 ? "#808080" : "#C0C0C0");
            AnsiColors.Add(hex);
            AnsiBrushes.Add(CreateBrush(hex));
        }

        RefreshBaseBrushes();
        SelectTarget("Background");
    }

    [RelayCommand]
    public void SelectTarget(string targetName)
    {
        ActiveColorTarget = targetName;
        if (targetName == "Background")
        {
            ActiveHex = Background;
        }
        else if (targetName == "Foreground")
        {
            ActiveHex = Foreground;
        }
        else if (targetName == "Cursor")
        {
            ActiveHex = CursorColor;
        }
        else if (targetName == "Selection")
        {
            ActiveHex = SelectionBackground;
        }
        else if (targetName.StartsWith("Ansi") && int.TryParse(targetName[4..], out int idx) && idx >= 0 && idx < 16)
        {
            ActiveHex = AnsiColors[idx];
        }
    }

    partial void OnActiveHexChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !HexColorRegex.IsMatch(value))
        {
            return;
        }

        string hex = value.Trim().ToUpperInvariant();
        if (ActiveColorTarget == "Background")
        {
            Background = hex;
            BackgroundBrush = CreateBrush(hex);
        }
        else if (ActiveColorTarget == "Foreground")
        {
            Foreground = hex;
            ForegroundBrush = CreateBrush(hex);
        }
        else if (ActiveColorTarget == "Cursor")
        {
            CursorColor = hex;
            CursorBrush = CreateBrush(hex);
        }
        else if (ActiveColorTarget == "Selection")
        {
            SelectionBackground = hex;
            SelectionBrush = CreateBrush(hex);
        }
        else if (ActiveColorTarget.StartsWith("Ansi") && int.TryParse(ActiveColorTarget[4..], out int idx) && idx >= 0 && idx < 16)
        {
            AnsiColors[idx] = hex;
            AnsiBrushes[idx] = CreateBrush(hex);
        }

        ActiveBrush = CreateBrush(hex);
    }

    [RelayCommand]
    public void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "自定义终端主题";
        }

        string[] ansiArr = new string[16];
        for (int i = 0; i < 16; i++)
        {
            ansiArr[i] = AnsiColors[i];
        }

        ResultProfile = new TerminalProfile
        {
            Id = ResultProfile.IsBuiltIn ? Guid.NewGuid().ToString() : ResultProfile.Id,
            Name = Name.Trim(),
            IsBuiltIn = false,
            Background = Background,
            Foreground = Foreground,
            CursorColor = CursorColor,
            SelectionBackground = SelectionBackground,
            AnsiColors = ansiArr,
            FontFamily = ResultProfile.FontFamily,
            FontSize = ResultProfile.FontSize,
            FontWeight = ResultProfile.FontWeight,
            LineHeight = ResultProfile.LineHeight,
            CursorBlink = ResultProfile.CursorBlink
        };

        IsConfirmed = true;
    }

    private void RefreshBaseBrushes()
    {
        BackgroundBrush = CreateBrush(Background);
        ForegroundBrush = CreateBrush(Foreground);
        CursorBrush = CreateBrush(CursorColor);
        SelectionBrush = CreateBrush(SelectionBackground);
    }

    private static IBrush CreateBrush(string hex)
    {
        if (Color.TryParse(hex, out var c))
        {
            return new SolidColorBrush(c);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    private static string NormalizeHex(string? input, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(input) && HexColorRegex.IsMatch(input))
        {
            return input.ToUpperInvariant();
        }
        return fallback.ToUpperInvariant();
    }
}
