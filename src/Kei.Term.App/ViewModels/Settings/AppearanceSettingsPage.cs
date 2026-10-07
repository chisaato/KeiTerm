using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.ViewModels.Settings;

public record TreeDensityPresetOption(string Key, string Label);
public record TabPlacementOption(string Key, string Label, Kei.Term.Core.Models.Profiles.TabPlacement Placement);

// 「外观」分类页：界面主题、会话树显示密度与终端字体
public partial class AppearanceSettingsPage : ViewModelBase
{
    private bool _isSyncingPreset;
    private bool _isSyncingFont;

    public AppearanceSettingsPage()
    {
        UiFallbackFonts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(UiPreviewFontFamily));
        _selectedTheme = ThemeOptions[0];

        DensityPresetOptions = new[]
        {
            new TreeDensityPresetOption("UltraCompact", Strings.Get("Settings.Appearance.TreeDensityUltraCompact")),
            new TreeDensityPresetOption("Compact", Strings.Get("Settings.Appearance.TreeDensityCompact")),
            new TreeDensityPresetOption("Normal", Strings.Get("Settings.Appearance.TreeDensityNormal")),
            new TreeDensityPresetOption("Custom", Strings.Get("Settings.Appearance.TreeDensityCustom")),
        };
        _selectedDensityPreset = DensityPresetOptions[1]; // 默认 Compact

        // 标签栏停靠位置选项
        TabPlacementOptions = new[]
        {
            new TabPlacementOption("Top", Strings.Get("Settings.Appearance.TabPlacementTop"), Kei.Term.Core.Models.Profiles.TabPlacement.Top),
            new TabPlacementOption("Bottom", Strings.Get("Settings.Appearance.TabPlacementBottom"), Kei.Term.Core.Models.Profiles.TabPlacement.Bottom),
        };
        _selectedTabPlacement = TabPlacementOptions[0];

        // 扫描加载系统字体
        AvailableFonts = SystemFontScanner.GetInstalledFonts();
        _selectedFont = AvailableFonts.FirstOrDefault(f => string.Equals(f.Name, _fontFamily, StringComparison.OrdinalIgnoreCase))
            ?? AvailableFonts.FirstOrDefault()
            ?? new FontFamilyOption("Cascadia Mono", true);
        _selectedUiFont = AvailableFonts.FirstOrDefault(f => _uiFontFamily.Contains(f.Name, StringComparison.OrdinalIgnoreCase))
            ?? AvailableFonts.FirstOrDefault();
    }

    // GUI 配色方案与终端配色方案集合
    public ObservableCollection<GuiProfile> GuiProfiles { get; } = [];
    public ObservableCollection<TerminalProfile> TerminalProfiles { get; } = [];

    [ObservableProperty]
    private GuiProfile? _selectedGuiProfile;

    partial void OnSelectedGuiProfileChanged(GuiProfile? value)
    {
        if (value != null)
        {
            // 实时热切换 GUI Profile 预览
            ProfileManagerService.ApplyGuiProfile(value);
        }
    }

    [ObservableProperty]
    private TerminalProfile? _selectedTerminalProfile;

    // 草稿预览回调：选择变化即通知父流程（设置页保持不落盘、不改已提交 ID）
    private Action<TerminalProfile?>? _draftPreviewHandler;

    public void SetDraftPreviewHandler(Action<TerminalProfile?> handler) => _draftPreviewHandler = handler;

    partial void OnSelectedTerminalProfileChanged(TerminalProfile? value)
    {
        _draftPreviewHandler?.Invoke(value);
    }

    // 填充 Profile 候选项并选中对应项
    public void SetProfiles(
        IEnumerable<GuiProfile> guiProfiles,
        string? activeGuiId,
        IEnumerable<TerminalProfile> termProfiles,
        string? activeTermId)
    {
        GuiProfiles.Clear();
        foreach (var gp in guiProfiles)
        {
            GuiProfiles.Add(gp);
        }
        SelectedGuiProfile = GuiProfiles.FirstOrDefault(p => string.Equals(p.Id, activeGuiId, StringComparison.OrdinalIgnoreCase))
            ?? GuiProfiles.FirstOrDefault();

        TerminalProfiles.Clear();
        foreach (var tp in termProfiles)
        {
            TerminalProfiles.Add(tp);
        }
        SelectedTerminalProfile = TerminalProfiles.FirstOrDefault(p => string.Equals(p.Id, activeTermId, StringComparison.OrdinalIgnoreCase))
            ?? TerminalProfiles.FirstOrDefault();
    }

    // 标签栏停靠位置选项
    public IReadOnlyList<TabPlacementOption> TabPlacementOptions { get; }

    [ObservableProperty]
    private TabPlacementOption _selectedTabPlacement;

    public void SetTabPlacement(string? placementKey)
    {
        SelectedTabPlacement = TabPlacementOptions.FirstOrDefault(o => string.Equals(o.Key, placementKey, StringComparison.OrdinalIgnoreCase))
            ?? TabPlacementOptions[0];
    }

    // 字体列表与当前选中的字体
    public IReadOnlyList<FontFamilyOption> AvailableFonts { get; }

    // 用于 AutoCompleteBox 的自定义过滤谓词（多 token 匹配 DisplayName）
    public AutoCompleteFilterPredicate<object> FontItemFilter { get; } = (search, item) =>
    {
        if (item is FontFamilyOption opt)
        {
            return FontNameSearch.IsMatch(search, opt.DisplayName);
        }
        return false;
    };

    [ObservableProperty]
    private FontFamilyOption? _selectedFont;

    partial void OnSelectedFontChanged(FontFamilyOption? value)
    {
        if (_isSyncingFont || value == null) return;
        _isSyncingFont = true;
        FontFamily = value.Name;
        _isSyncingFont = false;
        OnPropertyChanged(nameof(PreviewFontFamily));
    }

    // 当前字体草稿快照（主字体/回退/字号/闪烁），供共用 Demo 与调色弹窗读取
    public TerminalFontSnapshot DraftFontSnapshot => new(
        FontFamily,
        TerminalFallbackFonts.ToList(),
        FontSize,
        CursorBlink);

    private void NotifyDraftFontSnapshotChanged() => OnPropertyChanged(nameof(DraftFontSnapshot));

    // 实时预览属性（供 SettingsWindow 直接绑定）
    public FontFamily PreviewFontFamily
    {
        get
        {
            var fonts = new List<string>();
            if (!string.IsNullOrWhiteSpace(FontFamily)) fonts.Add(FontFamily);
            fonts.AddRange(TerminalFallbackFonts);
            if (fonts.Count == 0) fonts.Add("monospace");
            return new FontFamily(string.Join(", ", fonts));
        }
    }

    public FontWeight PreviewFontWeight => FontWeight.Regular;
    public string PreviewSampleText => "AaBbCc 012345 !@#$% 中文测试 0O1lI|";

    // 主题候选项：纯粹深色为主与跟随系统
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new ThemeOption[]
    {
        new("Dark", Strings.Get("Settings.Appearance.ThemeDark")),
        new("System", Strings.Get("Settings.Appearance.ThemeSystem")),
    };

    // 当前选中的主题选项
    [ObservableProperty]
    private ThemeOption _selectedTheme;

    // 树密度预设选项
    public IReadOnlyList<TreeDensityPresetOption> DensityPresetOptions { get; }

    [ObservableProperty]
    private TreeDensityPresetOption _selectedDensityPreset;

    // 树尺寸微调参数
    [ObservableProperty]
    private double _treeItemHeight = 22.0;

    [ObservableProperty]
    private double _treeFontSize = 12.0;

    [ObservableProperty]
    private double _treeIconSize = 14.0;

    [ObservableProperty]
    private double _treeIndent = 12.0;

    // 是否为自定义模式（当非自定义时，微调选项处于只读或联动修改）
    public bool IsCustomDensity => SelectedDensityPreset?.Key == "Custom";

    partial void OnSelectedDensityPresetChanged(TreeDensityPresetOption value)
    {
        OnPropertyChanged(nameof(IsCustomDensity));
        if (_isSyncingPreset || value == null) return;

        switch (value.Key)
        {
            case "UltraCompact":
                _isSyncingPreset = true;
                TreeItemHeight = 18.0;
                TreeFontSize = 11.5;
                TreeIconSize = 12.0;
                TreeIndent = 10.0;
                _isSyncingPreset = false;
                break;
            case "Compact":
                _isSyncingPreset = true;
                TreeItemHeight = 22.0;
                TreeFontSize = 12.0;
                TreeIconSize = 14.0;
                TreeIndent = 12.0;
                _isSyncingPreset = false;
                break;
            case "Normal":
                _isSyncingPreset = true;
                TreeItemHeight = 28.0;
                TreeFontSize = 13.0;
                TreeIconSize = 16.0;
                TreeIndent = 16.0;
                _isSyncingPreset = false;
                break;
        }
    }

    partial void OnTreeItemHeightChanged(double value) => CheckIfCustomPreset();
    partial void OnTreeFontSizeChanged(double value) => CheckIfCustomPreset();
    partial void OnTreeIconSizeChanged(double value) => CheckIfCustomPreset();
    partial void OnTreeIndentChanged(double value) => CheckIfCustomPreset();

    private void CheckIfCustomPreset()
    {
        if (_isSyncingPreset) return;
        if (SelectedDensityPreset?.Key != "Custom")
        {
            var custom = DensityPresetOptions.FirstOrDefault(o => o.Key == "Custom");
            if (custom != null)
            {
                _isSyncingPreset = true;
                SelectedDensityPreset = custom;
                _isSyncingPreset = false;
                OnPropertyChanged(nameof(IsCustomDensity));
            }
        }
    }

    public void SetTreeDensity(string? preset, double itemHeight, double fontSize, double iconSize, double indent)
    {
        _isSyncingPreset = true;
        TreeItemHeight = itemHeight > 0 ? itemHeight : 22.0;
        TreeFontSize = fontSize > 0 ? fontSize : 12.0;
        TreeIconSize = iconSize > 0 ? iconSize : 14.0;
        TreeIndent = indent > 0 ? indent : 12.0;

        SelectedDensityPreset = DensityPresetOptions.FirstOrDefault(o => o.Key.Equals(preset, StringComparison.OrdinalIgnoreCase))
            ?? DensityPresetOptions[1];
        _isSyncingPreset = false;
        OnPropertyChanged(nameof(IsCustomDensity));
    }

    // 界面全局字体（UI Font）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiPreviewFontFamily))]
    private string _uiFontFamily = "Noto Sans CJK SC";

    [ObservableProperty]
    private FontFamilyOption? _selectedUiFont;

    partial void OnSelectedUiFontChanged(FontFamilyOption? value)
    {
        if (value != null)
        {
            UiFontFamily = value.Name;
        }
    }

    partial void OnUiFontFamilyChanged(string value)
    {
        if (SelectedUiFont?.Name != value)
        {
            SelectedUiFont = AvailableFonts.FirstOrDefault(f => string.Equals(f.Name, value, StringComparison.OrdinalIgnoreCase))
                ?? AvailableFonts.FirstOrDefault(f => value.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase))
                ?? new FontFamilyOption(value, false);
        }
    }

    // 界面回退字体有序列表
    public ObservableCollection<string> UiFallbackFonts { get; } = [];

    // 预览采用当前草稿的完整字体链，排序和删除回退字体时也实时刷新。
    public FontFamily UiPreviewFontFamily
    {
        get
        {
            List<string> fonts = [];
            if (!string.IsNullOrWhiteSpace(UiFontFamily)) fonts.Add(UiFontFamily);
            fonts.AddRange(UiFallbackFonts);
            fonts.Add("sans-serif");
            return new FontFamily(string.Join(", ", fonts));
        }
    }

    [ObservableProperty]
    private string? _selectedUiFallbackFont;

    [ObservableProperty]
    private FontFamilyOption? _uiFallbackFontCandidate;

    public void SetUiFallbackFonts(string? commaSeparated)
    {
        UiFallbackFonts.Clear();
        if (!string.IsNullOrWhiteSpace(commaSeparated))
        {
            var parts = commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (!UiFallbackFonts.Contains(part))
                {
                    UiFallbackFonts.Add(part);
                }
            }
        }
    }

    public string GetUiFallbackFontsString() => string.Join(", ", UiFallbackFonts);

    public void AddUiFallbackFont()
    {
        if (UiFallbackFontCandidate != null && !UiFallbackFonts.Contains(UiFallbackFontCandidate.Name))
        {
            UiFallbackFonts.Add(UiFallbackFontCandidate.Name);
        }
    }

    public void RemoveUiFallbackFont()
    {
        if (SelectedUiFallbackFont != null)
        {
            UiFallbackFonts.Remove(SelectedUiFallbackFont);
        }
    }

    public void MoveUpUiFallbackFont()
    {
        if (SelectedUiFallbackFont == null) return;
        var idx = UiFallbackFonts.IndexOf(SelectedUiFallbackFont);
        if (idx > 0)
        {
            UiFallbackFonts.Move(idx, idx - 1);
        }
    }

    public void MoveDownUiFallbackFont()
    {
        if (SelectedUiFallbackFont == null) return;
        var idx = UiFallbackFonts.IndexOf(SelectedUiFallbackFont);
        if (idx >= 0 && idx < UiFallbackFonts.Count - 1)
        {
            UiFallbackFonts.Move(idx, idx + 1);
        }
    }

    // 终端主字体（Terminal Primary Font）
    [ObservableProperty]
    private string _fontFamily = "JetBrainsMono Nerd Font Mono";

    partial void OnFontFamilyChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewFontFamily));
        NotifyDraftFontSnapshotChanged();
        if (_isSyncingFont) return;
        _isSyncingFont = true;
        SelectedFont = AvailableFonts.FirstOrDefault(f => string.Equals(f.Name, value, StringComparison.OrdinalIgnoreCase))
            ?? AvailableFonts.FirstOrDefault(f => value.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase))
            ?? new FontFamilyOption(value, SystemFontScanner.IsRecommendedMonospace(value));
        _isSyncingFont = false;
    }

    // 终端回退字体有序列表
    public ObservableCollection<string> TerminalFallbackFonts { get; } = [];

    [ObservableProperty]
    private string? _selectedTerminalFallbackFont;

    [ObservableProperty]
    private FontFamilyOption? _terminalFallbackFontCandidate;

    public void SetTerminalFallbackFonts(string? commaSeparated)
    {
        TerminalFallbackFonts.Clear();
        if (!string.IsNullOrWhiteSpace(commaSeparated))
        {
            var parts = commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (!TerminalFallbackFonts.Contains(part))
                {
                    TerminalFallbackFonts.Add(part);
                }
            }
        }
        OnPropertyChanged(nameof(PreviewFontFamily));
        NotifyDraftFontSnapshotChanged();
    }

    public string GetTerminalFallbackFontsString() => string.Join(", ", TerminalFallbackFonts);

    public void AddTerminalFallbackFont()
    {
        if (TerminalFallbackFontCandidate != null && !TerminalFallbackFonts.Contains(TerminalFallbackFontCandidate.Name))
        {
            TerminalFallbackFonts.Add(TerminalFallbackFontCandidate.Name);
            OnPropertyChanged(nameof(PreviewFontFamily));
            NotifyDraftFontSnapshotChanged();
        }
    }

    public void RemoveTerminalFallbackFont()
    {
        if (SelectedTerminalFallbackFont != null)
        {
            TerminalFallbackFonts.Remove(SelectedTerminalFallbackFont);
            OnPropertyChanged(nameof(PreviewFontFamily));
            NotifyDraftFontSnapshotChanged();
        }
    }

    public void MoveUpTerminalFallbackFont()
    {
        if (SelectedTerminalFallbackFont == null) return;
        var idx = TerminalFallbackFonts.IndexOf(SelectedTerminalFallbackFont);
        if (idx > 0)
        {
            TerminalFallbackFonts.Move(idx, idx - 1);
            OnPropertyChanged(nameof(PreviewFontFamily));
            NotifyDraftFontSnapshotChanged();
        }
    }

    public void MoveDownTerminalFallbackFont()
    {
        if (SelectedTerminalFallbackFont == null) return;
        var idx = TerminalFallbackFonts.IndexOf(SelectedTerminalFallbackFont);
        if (idx >= 0 && idx < TerminalFallbackFonts.Count - 1)
        {
            TerminalFallbackFonts.Move(idx, idx + 1);
            OnPropertyChanged(nameof(PreviewFontFamily));
            NotifyDraftFontSnapshotChanged();
        }
    }

    // 兼容保留字段
    public string FallbackFontFamily
    {
        get => GetTerminalFallbackFontsString();
        set => SetTerminalFallbackFonts(value);
    }

    [ObservableProperty]
    private double _fontSize = 14.0;

    partial void OnFontSizeChanged(double value)
    {
        OnPropertyChanged(nameof(FontSize));
        NotifyDraftFontSnapshotChanged();
    }

    [ObservableProperty]
    private bool _cursorBlink = true;

    partial void OnCursorBlinkChanged(bool value)
    {
        NotifyDraftFontSnapshotChanged();
    }

    // 归一化主题键：仅接受 Dark/System，其余一律回退 Dark
    public string NormalizedThemeKey => SelectedTheme?.Key == "System" ? "System" : "Dark";

    // 控件库统一使用内置紧凑桌面 Fluent 风格
    public string NormalizedControlLibraryKey => "KeiClassic";

    // 按持久化键恢复选中项，未知值回退 Dark
    public void SetTheme(string? key)
    {
        SelectedTheme = ThemeOptions.FirstOrDefault(o => o.Key == key) ?? ThemeOptions[0];
    }

    public void SetControlLibrary(string? key)
    {
        // 兼容保留接口，已收拢为内置统一风格
    }
}
