using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;

namespace Kei.Term.App.ViewModels;

// 设置窗口主 VM：聚合左侧分类树与右侧各设置子页，负责统一校验与保存
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly GeneralSettingsPage _general;
    private readonly AppearanceSettingsPage _appearance;
    private readonly TerminalSettingsPage _terminal;
    private readonly SshSettingsPage _ssh;
    private readonly FileTransferSettingsPage _fileTransfer;

    // 左侧分类树数据源
    public IReadOnlyList<SettingsCategoryItem> Categories { get; }

    // 当前选中的分类，决定右侧内容
    [ObservableProperty]
    private SettingsCategoryItem _selectedCategory;

    public bool IsConfirmed { get; private set; }
    public event Action? RequestClose;

    // 弹窗与选择器委托（由 SettingsWindow 注入）
    public Func<Task<string?>>? SaveBundleFileDialogAsync { get; set; }
    public Func<Task<string?>>? OpenBundleFileDialogAsync { get; set; }
    public Func<Task<string?>>? OpenKonsoleFileDialogAsync { get; set; }
    public Func<TerminalProfile?, Task<TerminalProfile?>>? OpenTerminalProfileEditDialogAsync { get; set; }
    public Func<string, string, Task>? ShowNotificationAsync { get; set; }

    private readonly Services.ProfileManagerService _profileManager;

    // 记录打开窗口时的初始设置快照与 Profile，供取消时回滚
    private AppSettings _initialSettings = new();
    private GuiProfile? _initialGuiProfile;

    // dataDirectory / identityRepo 带默认值，保证并行车道的既有调用点在合入前也能编译
    public SettingsViewModel(
        ISettingsService settingsService,
        string dataDirectory = "",
        IIdentityRepository? identityRepo = null,
        Services.ProfileManagerService? profileManager = null,
        IExternalEditorRepository? editorRepo = null)
    {
        _settingsService = settingsService;
        _profileManager = profileManager ?? new Services.ProfileManagerService(settingsService, dataDirectory);
        _general = new GeneralSettingsPage(dataDirectory);
        _appearance = new AppearanceSettingsPage();
        _terminal = new TerminalSettingsPage();
        _ssh = new SshSettingsPage(identityRepo);
        _fileTransfer = new FileTransferSettingsPage(editorRepo);

        Categories = new[]
        {
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.General"),
                Strings.Get("Settings.Categories.GeneralDesc"),
                _general,
                SettingsIcons.General),
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.Appearance"),
                Strings.Get("Settings.Categories.AppearanceDesc"),
                _appearance,
                SettingsIcons.Appearance),
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.Tree"),
                Strings.Get("Settings.Categories.TreeDesc"),
                new TreeSettingsProxyPage(_appearance),
                SettingsIcons.Tree),
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.Terminal"),
                Strings.Get("Settings.Categories.TerminalDesc"),
                new TerminalSettingsCombinedPage(_terminal, _appearance),
                SettingsIcons.Terminal),
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.TabBar"),
                Strings.Get("Settings.Categories.TabBarDesc"),
                new TabBarSettingsProxyPage(_appearance),
                SettingsIcons.TabBar),
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.Ssh"),
                Strings.Get("Settings.Categories.SshDesc"),
                _ssh,
                SettingsIcons.Ssh),
            new SettingsCategoryItem(
                "文件传输",
                "传输缓存、变动监视与外部编辑器关联",
                _fileTransfer,
                SettingsIcons.FileTransfer),
        };
        SelectedCategory = Categories[0];

        Reload();
    }

    // 从设置服务重读当前值，丢弃未保存的编辑（每次打开窗口时调用）
    public void Reload()
    {
        var current = _settingsService.Current;
        _general.ConfirmBeforeClose = current.ConfirmBeforeClose;
        _general.SetTreeSortMode(current.TreeSortMode);
        _fileTransfer.CacheDirectory = current.FileTransfer.CacheDirectory;
        _fileTransfer.SelectedWatcherMode = current.FileTransfer.WatcherMode.ToString();
        _ = _fileTransfer.ReloadAsync();
        _general.SelectedSessionManagerMode = current.SessionManagerVisibilityMode switch
        {
            PanelVisibilityMode.AlwaysVisible => "常开 (Always Visible)",
            PanelVisibilityMode.AlwaysHidden => "常关 (Always Hidden)",
            _ => "保持上次状态 (Remember Last)"
        };
        _general.SelectedComposeBarMode = current.ComposeBarVisibilityMode switch
        {
            PanelVisibilityMode.AlwaysVisible => "常开 (Always Visible)",
            PanelVisibilityMode.AlwaysHidden => "常关 (Always Hidden)",
            _ => "保持上次状态 (Remember Last)"
        };

        _appearance.SetTheme(current.UiTheme);
        _appearance.SetControlLibrary(current.ControlLibraryTheme);
        _appearance.SetTreeDensity(
            current.TreeDensityPreset,
            current.TreeItemHeight,
            current.TreeFontSize,
            current.TreeIconSize,
            current.TreeIndent);
        _appearance.UiFontFamily = current.UiFontFamily;
        _appearance.SetUiFallbackFonts(current.UiFallbackFontFamily);
        _appearance.FontFamily = current.FontFamily;
        _appearance.FallbackFontFamily = current.TerminalFallbackFontFamily;
        _appearance.FontSize = current.FontSize;
        _appearance.CursorBlink = current.CursorBlink;
        _appearance.SetTabPlacement(current.TabPlacement);

        // 初始化并同步 ProfileManagerService 中的 GUI/Terminal Profile 列表与选中项
        var activeGuiId = !string.IsNullOrWhiteSpace(current.ActiveGuiProfileId)
            ? current.ActiveGuiProfileId
            : _profileManager.ActiveGuiProfile.Id;
        var activeTermId = !string.IsNullOrWhiteSpace(current.ActiveTerminalProfileId)
            ? current.ActiveTerminalProfileId
            : _profileManager.DefaultTerminalProfile.Id;

        _appearance.SetProfiles(
            _profileManager.AllGuiProfiles,
            activeGuiId,
            _profileManager.AllTerminalProfiles,
            activeTermId);

        // 记录初始快照以支持“取消”时回滚
        _initialSettings = new AppSettings
        {
            ConfirmBeforeClose = current.ConfirmBeforeClose,
            TreeSortMode = current.TreeSortMode,
            UiTheme = current.UiTheme,
            ControlLibraryTheme = current.ControlLibraryTheme,
            TreeDensityPreset = current.TreeDensityPreset,
            TreeItemHeight = current.TreeItemHeight,
            TreeFontSize = current.TreeFontSize,
            TreeIconSize = current.TreeIconSize,
            TreeIndent = current.TreeIndent,
            UiFontFamily = current.UiFontFamily,
            FontFamily = current.FontFamily,
            TerminalFallbackFontFamily = current.TerminalFallbackFontFamily,
            FontSize = current.FontSize,
            CursorBlink = current.CursorBlink,
            TabPlacement = current.TabPlacement,
            ActiveGuiProfileId = current.ActiveGuiProfileId,
            ActiveTerminalProfileId = current.ActiveTerminalProfileId,
            DefaultPort = current.DefaultPort,
            DefaultUsername = current.DefaultUsername,
            DefaultIdentityId = current.DefaultIdentityId,
            PreferSystemAgent = current.PreferSystemAgent,
            LockTimeoutMinutes = current.LockTimeoutMinutes,
            ConnectTimeoutSeconds = current.ConnectTimeoutSeconds,
            KeepAliveIntervalSeconds = current.KeepAliveIntervalSeconds,
            EnableAgentForwarding = current.EnableAgentForwarding,
            CustomAgentSocketPath = current.CustomAgentSocketPath,
            ScrollbackLines = current.ScrollbackLines,
            DefaultTerminalType = current.DefaultTerminalType
        };
        _initialGuiProfile = _profileManager.ActiveGuiProfile;

        _terminal.ScrollbackLines = current.ScrollbackLines;
        _terminal.DefaultTerminalType = string.IsNullOrWhiteSpace(current.DefaultTerminalType)
            ? "xterm-256color"
            : current.DefaultTerminalType;

        _ssh.DefaultPort = current.DefaultPort;
        _ssh.DefaultUsername = current.DefaultUsername;
        _ssh.SelectedIdentityId = current.DefaultIdentityId;
        _ssh.PreferSystemAgent = current.PreferSystemAgent;
        _ssh.LockTimeoutMinutes = current.LockTimeoutMinutes;
        _ssh.ConnectTimeoutSeconds = current.ConnectTimeoutSeconds;
        _ssh.KeepAliveIntervalSeconds = current.KeepAliveIntervalSeconds;
        _ssh.EnableAgentForwarding = current.EnableAgentForwarding;
        _ssh.CustomAgentSocketPath = current.CustomAgentSocketPath ?? string.Empty;

        // 异步加载身份列表填充下拉，加载完成后按 SelectedIdentityId 恢复选中
        _ = _ssh.LoadIdentitiesAsync();
    }

    // 保存当前设置的核心逻辑
    public async Task ApplyChangesAsync()
    {
        AppSettings settings = new AppSettings
        {
            // 常规
            ConfirmBeforeClose = _general.ConfirmBeforeClose,
            TreeSortMode = _general.SelectedTreeSort?.Mode ?? "AsciiFirst",
            SessionManagerVisibilityMode = _general.SelectedSessionManagerMode switch
            {
                "常开 (Always Visible)" => PanelVisibilityMode.AlwaysVisible,
                "常关 (Always Hidden)" => PanelVisibilityMode.AlwaysHidden,
                _ => PanelVisibilityMode.RememberLastState
            },
            LastSessionManagerVisible = _settingsService.Current.LastSessionManagerVisible,
            ComposeBarVisibilityMode = _general.SelectedComposeBarMode switch
            {
                "常开 (Always Visible)" => PanelVisibilityMode.AlwaysVisible,
                "常关 (Always Hidden)" => PanelVisibilityMode.AlwaysHidden,
                _ => PanelVisibilityMode.RememberLastState
            },
            LastComposeBarVisible = _settingsService.Current.LastComposeBarVisible,
            FileTransfer = new FileTransferSettings
            {
                CacheDirectory = _fileTransfer.CacheDirectory,
                CustomEditorPath = string.Empty,
                WatcherMode = Enum.TryParse<FileWatcherMode>(_fileTransfer.SelectedWatcherMode, out var wm) ? wm : FileWatcherMode.Auto
            },

            // 外观：主题值归一化，仅接受 Dark/System
            UiTheme = _appearance.NormalizedThemeKey,
            ControlLibraryTheme = _appearance.NormalizedControlLibraryKey,
            TreeDensityPreset = _appearance.SelectedDensityPreset?.Key ?? "Compact",
            TreeItemHeight = Math.Clamp(_appearance.TreeItemHeight, 16.0, 40.0),
            TreeFontSize = Math.Clamp(_appearance.TreeFontSize, 9.0, 18.0),
            TreeIconSize = Math.Clamp(_appearance.TreeIconSize, 10.0, 24.0),
            TreeIndent = Math.Clamp(_appearance.TreeIndent, 6.0, 32.0),
            UiFontFamily = string.IsNullOrWhiteSpace(_appearance.UiFontFamily)
                ? "Noto Sans CJK SC"
                : _appearance.UiFontFamily.Trim(),
            UiFallbackFontFamily = _appearance.GetUiFallbackFontsString(),
            FontFamily = string.IsNullOrWhiteSpace(_appearance.FontFamily) ? "monospace" : _appearance.FontFamily.Trim(),
            TerminalFallbackFontFamily = string.IsNullOrWhiteSpace(_appearance.FallbackFontFamily)
                ? "Noto Sans Mono CJK SC, Source Han Sans HW SC, Microsoft YaHei, monospace"
                : _appearance.FallbackFontFamily.Trim(),
            FontSize = Math.Clamp(_appearance.FontSize, 8.0, 36.0),
            CursorBlink = _appearance.CursorBlink,
            TabPlacement = _appearance.SelectedTabPlacement?.Key ?? "Top",

            ActiveGuiProfileId = _appearance.SelectedGuiProfile?.Id,
            ActiveTerminalProfileId = _appearance.SelectedTerminalProfile?.Id,

            // 终端
            ScrollbackLines = Math.Clamp(_terminal.ScrollbackLines, 500, 50000),
            DefaultTerminalType = string.IsNullOrWhiteSpace(_terminal.DefaultTerminalType)
                ? "xterm-256color"
                : _terminal.DefaultTerminalType.Trim(),

            // SSH
            DefaultPort = _ssh.DefaultPort is >= 1 and <= 65535 ? _ssh.DefaultPort : 22,
            DefaultUsername = string.IsNullOrWhiteSpace(_ssh.DefaultUsername) ? "" : _ssh.DefaultUsername.Trim(),
            DefaultIdentityId = _ssh.SelectedIdentityId,
            PreferSystemAgent = _ssh.PreferSystemAgent,
            LockTimeoutMinutes = Math.Clamp(_ssh.LockTimeoutMinutes, 0, 120),
            ConnectTimeoutSeconds = Math.Clamp(_ssh.ConnectTimeoutSeconds, 3, 120),
            KeepAliveIntervalSeconds = Math.Clamp(_ssh.KeepAliveIntervalSeconds, 0, 300),
            EnableAgentForwarding = _ssh.EnableAgentForwarding,
            CustomAgentSocketPath = string.IsNullOrWhiteSpace(_ssh.CustomAgentSocketPath)
                ? null
                : _ssh.CustomAgentSocketPath.Trim()
        };

        await _settingsService.SaveSettingsAsync(settings);

        // 如果用户选择了 GUI Profile，立即通知 ProfileManagerService 生效
        if (_appearance.SelectedGuiProfile != null)
        {
            _profileManager.SetActiveGuiProfile(_appearance.SelectedGuiProfile.Id);
        }
        if (_appearance.SelectedTerminalProfile != null)
        {
            _profileManager.SetDefaultTerminalProfile(_appearance.SelectedTerminalProfile.Id);
        }
        _ = _profileManager.SaveProfilesAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await ApplyChangesAsync();
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        await ApplyChangesAsync();
        IsConfirmed = true;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        // 回滚回打开时的初始状态
        await _settingsService.SaveSettingsAsync(_initialSettings);
        if (_initialGuiProfile != null)
        {
            _profileManager.SetActiveGuiProfile(_initialGuiProfile.Id);
            Services.ProfileManagerService.ApplyGuiProfile(_initialGuiProfile);
        }
        IsConfirmed = false;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        if (SaveBundleFileDialogAsync == null) return;
        var filePath = await SaveBundleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            var json = _profileManager.ExportBundle();
            await System.IO.File.WriteAllTextAsync(filePath, json);
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    string.Format(Strings.Get("Settings.Appearance.BundleExportSuccess"), System.IO.Path.GetFileName(filePath)));
            }
        }
        catch (Exception ex)
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        if (OpenBundleFileDialogAsync == null) return;
        var filePath = await OpenBundleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            await _profileManager.ImportBundleAsync(json, overwrite: true);
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    Strings.Get("Settings.Appearance.BundleImportSuccess"));
            }
        }
        catch (Exception ex)
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    string.Format(Strings.Get("Settings.Appearance.BundleImportError"), ex.Message));
            }
        }
    }

    [RelayCommand]
    private async Task NewTerminalProfileAsync()
    {
        if (OpenTerminalProfileEditDialogAsync == null) return;
        var newProfile = await OpenTerminalProfileEditDialogAsync(new TerminalProfile { Name = "新建终端主题" });
        if (newProfile != null)
        {
            _profileManager.AddCustomTerminalProfile(newProfile);
            await _profileManager.SaveProfilesAsync();
            RefreshTerminalProfiles(newProfile.Id);
        }
    }

    [RelayCommand]
    private async Task EditTerminalProfileAsync()
    {
        if (OpenTerminalProfileEditDialogAsync == null || _appearance.SelectedTerminalProfile == null) return;
        var updated = await OpenTerminalProfileEditDialogAsync(_appearance.SelectedTerminalProfile);
        if (updated != null)
        {
            _profileManager.AddCustomTerminalProfile(updated);
            await _profileManager.SaveProfilesAsync();
            RefreshTerminalProfiles(updated.Id);
        }
    }

    [RelayCommand]
    private async Task ImportKonsoleSchemeAsync()
    {
        if (OpenKonsoleFileDialogAsync == null || OpenTerminalProfileEditDialogAsync == null) return;
        var filePath = await OpenKonsoleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            var content = await System.IO.File.ReadAllTextAsync(filePath);
            var defaultName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var parsed = Kei.Term.Core.Services.KonsoleColorSchemeParser.Parse(content, defaultName);

            var confirmed = await OpenTerminalProfileEditDialogAsync(parsed);
            if (confirmed != null)
            {
                _profileManager.AddCustomTerminalProfile(confirmed);
                await _profileManager.SaveProfilesAsync();
                RefreshTerminalProfiles(confirmed.Id);
                if (ShowNotificationAsync != null)
                {
                    await ShowNotificationAsync(
                        Strings.Get("TerminalProfileEdit.Title"),
                        Strings.Get("Settings.Appearance.BundleImportSuccess"));
                }
            }
        }
        catch (Exception ex)
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("TerminalProfileEdit.Title"),
                    ex.Message);
            }
        }
    }

    private void RefreshTerminalProfiles(string selectedId)
    {
        _appearance.TerminalProfiles.Clear();
        foreach (var p in _profileManager.AllTerminalProfiles)
        {
            _appearance.TerminalProfiles.Add(p);
        }
        _appearance.SelectedTerminalProfile = _appearance.TerminalProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _appearance.TerminalProfiles.FirstOrDefault();
    }
}
