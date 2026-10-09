using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Models;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.ViewModels;

public sealed partial class TerminalProfileEditViewModel : ViewModelBase
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

    // 颜色合法性校验（当 ActiveHex 非法时禁止确认）
    [ObservableProperty]
    private bool _isCurrentColorValid = true;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

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

    // 供终端 Demo 预览消费的实时草稿 Profile 与 Font 快照
    [ObservableProperty]
    private TerminalProfile _previewProfile = new();

    public TerminalFontSnapshot? FontSnapshot { get; }

    public bool IsConfirmed { get; private set; }

    public TerminalProfile ResultProfile { get; private set; }

    // 原构造函数兼容
    public TerminalProfileEditViewModel(TerminalProfile? sourceProfile = null)
        : this(sourceProfile, null)
    {
    }

    // 包含字体快照的新重载
    public TerminalProfileEditViewModel(TerminalProfile? sourceProfile, TerminalFontSnapshot? fontSnapshot)
    {
        FontSnapshot = fontSnapshot;
        ResultProfile = sourceProfile != null ? sourceProfile.DeepCopy() : new TerminalProfile();
        InitFromProfile(ResultProfile);
    }

    // JSON 确认只替换配色草稿；保留当前方案身份以及字体弹窗已经编辑的字段。
    public void ApplyJsonProfile(TerminalProfile imported)
    {
        TerminalProfile draft = ResultProfile.DeepCopy();
        draft.Name = imported.Name;
        draft.Background = imported.Background;
        draft.Foreground = imported.Foreground;
        draft.CursorColor = imported.CursorColor;
        draft.SelectionBackground = imported.SelectionBackground;
        draft.AnsiColors = (string[])imported.AnsiColors.Clone();
        InitFromProfile(draft, appendCopySuffix: false);
    }

    partial void OnNameChanged(string value)
    {
        if (AnsiColors.Count == 16) UpdatePreviewProfile();
    }

    private void InitFromProfile(TerminalProfile profile, bool appendCopySuffix = true)
    {
        Name = profile.IsBuiltIn && appendCopySuffix ? $"{profile.Name} (副本)" : profile.Name;
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
        UpdatePreviewProfile();
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
            IsCurrentColorValid = false;
            ValidationMessage = "十六进制色值格式无效（支持 #RGB、#RRGGBB 或 #AARRGGBB）";
            return;
        }

        IsCurrentColorValid = true;
        ValidationMessage = string.Empty;

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
        UpdatePreviewProfile();
    }

    private void UpdatePreviewProfile()
    {
        string[] ansiArr = new string[16];
        for (int i = 0; i < 16; i++)
        {
            ansiArr[i] = AnsiColors[i];
        }

        PreviewProfile = new TerminalProfile
        {
            Id = ResultProfile.Id,
            Name = Name,
            IsBuiltIn = false,
            Background = Background,
            Foreground = Foreground,
            CursorColor = CursorColor,
            SelectionBackground = SelectionBackground,
            AnsiColors = ansiArr,
            FontFamily = ResultProfile.FontFamily,
            FontSize = ResultProfile.FontSize,
            FontWeight = ResultProfile.FontWeight,
            IsItalic = ResultProfile.IsItalic,
            LineHeight = ResultProfile.LineHeight,
            CursorBlink = ResultProfile.CursorBlink
        };
    }

    [RelayCommand]
    public void Confirm()
    {
        // 若当前处于非法颜色状态，禁止确认
        if (!IsCurrentColorValid)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "自定义终端主题";
        }

        string[] ansiArr = new string[16];
        for (int i = 0; i < 16; i++)
        {
            ansiArr[i] = AnsiColors[i];
        }

        // 严格深拷贝赋值，保留旧字体字段（FontFamily, FontSize, FontWeight, IsItalic, LineHeight, CursorBlink）
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
            IsItalic = ResultProfile.IsItalic,
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
