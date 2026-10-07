using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Terminals;
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
    public IReadOnlyList<SettingsCategoryItem> Categories { get; private set; }

    public ProxySettingsPage? ProxyPage { get; }

    public Func<IReadOnlyList<TreeNodeBase>>? SessionTreeSnapshot { get; }

    // 当前选中的分类，决定右侧内容
    [ObservableProperty]
    private SettingsCategoryItem _selectedCategory;

    public bool IsConfirmed { get; private set; }
    public event Action? RequestClose;

    // 弹窗与选择器委托（由 SettingsWindow 注入）
    public Func<Task<string?>>? SaveBundleFileDialogAsync { get; set; }
    public Func<Task<string?>>? OpenBundleFileDialogAsync { get; set; }
    public Func<Task<string?>>? OpenKonsoleFileDialogAsync { get; set; }
    // 调色弹窗：参数为（源方案, 当前字体快照）；返回 null 表示取消
    public Func<TerminalProfile?, TerminalFontSnapshot, Task<TerminalProfile?>>? OpenTerminalProfileEditDialogAsync { get; set; }
    public Func<string, string, Task>? ShowNotificationAsync { get; set; }

    private readonly Services.ProfileManagerService _profileManager;

    // 最后一次成功提交的设置快照：仅用于 settings.json 的失败补偿
    private AppSettings _lastAppliedSettings = new();

    // 全局事务门控：Apply/Cancel 期间为 true；C 在 View 层绑定以禁用表单与按钮
    [ObservableProperty]
    private bool _isBusy;

    // 程序性重载期间抑制草稿预览广播，避免初始化赋值触发无意义刷新
    private bool _isReloading;

    // dataDirectory / identityRepo 带默认值，保证并行车道的既有调用点在合入前也能编译
    public SettingsViewModel(
        ISettingsService settingsService,
        string dataDirectory = "",
        IIdentityRepository? identityRepo = null,
        Services.ProfileManagerService? profileManager = null,
        IExternalEditorRepository? editorRepo = null,
        IProxyRepository? proxyRepo = null,
        Func<IReadOnlyList<SessionNode>>? sessionSnapshot = null,
        Func<IReadOnlyList<TreeNodeBase>>? sessionTree = null)
    {
        _settingsService = settingsService;
        _profileManager = profileManager ?? new Services.ProfileManagerService(settingsService, dataDirectory);
        _general = new GeneralSettingsPage(dataDirectory);
        _appearance = new AppearanceSettingsPage();
        _terminal = new TerminalSettingsPage();
        _ssh = new SshSettingsPage(identityRepo);
        _fileTransfer = new FileTransferSettingsPage(editorRepo);
        if (proxyRepo != null)
        {
            ProxyPage = new ProxySettingsPage(proxyRepo, sessionSnapshot ?? (() => []));
            SessionTreeSnapshot = sessionTree;
        }

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
                new TerminalSettingsCombinedPage(_terminal),
                SettingsIcons.Terminal),
            new SettingsCategoryItem(
                Strings.Get("Settings.Categories.TerminalAppearance"),
                Strings.Get("Settings.Categories.TerminalAppearanceDesc"),
                new TerminalAppearanceSettingsPage(_appearance),
                SettingsIcons.Appearance),
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
        if (ProxyPage != null)
        {
            Categories = Categories.Append(new SettingsCategoryItem(
                Strings.Get("Settings.Categories.Proxy"),
                Strings.Get("Settings.Categories.ProxyDesc"),
                ProxyPage,
                SettingsIcons.Ssh)).ToArray();
        }

        SelectedCategory = Categories[0];

        // 选择即草稿预览：选择回调交回本 VM 统一广播，但不落盘
        _appearance.SetDraftPreviewHandler(OnTerminalProfileDraftPreview);

        Reload();
    }

    // 从设置服务重读当前值，丢弃未保存的编辑（每次打开窗口时调用）
    public void Reload()
    {
        _isReloading = true;
        try
        {
            ReloadCore();
        }
        finally
        {
            _isReloading = false;
        }
    }

    private void ReloadCore()
    {
        var current = _settingsService.Current;
        _general.ConfirmBeforeClose = current.ConfirmBeforeClose;
        _general.AutoReconnectOnDisconnect = current.AutoReconnectOnDisconnect;
        _general.UseNativeGlobalMenu = current.UseNativeGlobalMenu;
        _general.UseNativeContextMenus = current.UseNativeContextMenus;
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

        // 记录“最后一次成功提交”的设置快照（settings 补偿用）；Profile 基准由管理器已提交权威持有
        _lastAppliedSettings = CloneSettings(current);

        _terminal.ScrollbackLines = current.ScrollbackLines;
        _terminal.DefaultTerminalType = string.IsNullOrWhiteSpace(current.DefaultTerminalType)
            ? "xterm-256color"
            : current.DefaultTerminalType;
        _terminal.TabTitleFollowsRemote = current.TabTitleFollowsRemote;
        _terminal.SetCwdFollow(current.CwdFollowMode);

        _ssh.DefaultPort = current.DefaultPort;
        _ssh.DefaultUsername = current.DefaultUsername;
        _ssh.SelectedIdentityId = current.DefaultIdentityId;
        _ssh.PreferSystemAgent = current.PreferSystemAgent;
        _ssh.LockTimeoutMinutes = current.LockTimeoutMinutes;
        _ssh.ConnectTimeoutSeconds = current.ConnectTimeoutSeconds;
        _ssh.KeepAliveIntervalSeconds = current.KeepAliveIntervalSeconds;
        _ssh.EnableAgentForwarding = current.EnableAgentForwarding;
        _ssh.CustomAgentSocketPath = current.CustomAgentSocketPath ?? string.Empty;
        _ssh.HostKeyPolicy = current.HostKeyPolicy;

        // 异步加载身份列表填充下拉，加载完成后按 SelectedIdentityId 恢复选中
        _ = _ssh.LoadIdentitiesAsync();
    }

    // 保存当前设置的核心逻辑；返回是否成功（失败时不推进权威）
    public async Task<bool> ApplyChangesAsync()
    {
        // 真实命令层互斥：busy 期间拒绝新的提交（不是只靠 UI 禁用）
        if (IsBusy)
        {
            return false;
        }

        // 以当前设置的完整副本为底稿，只覆盖本窗口编辑的字段：新增设置项无需在此登记也不会被保存重置
        //（曾因整对象重建漏列导致文件侧栏位置、轮询参数等保存后丢失）
        AppSettings settings = CloneSettings(_settingsService.Current);
        settings.HostKeyPolicy = _ssh.HostKeyPolicy;

        // 常规
        settings.ConfirmBeforeClose = _general.ConfirmBeforeClose;
        settings.AutoReconnectOnDisconnect = _general.AutoReconnectOnDisconnect;
        settings.UseNativeGlobalMenu = _general.UseNativeGlobalMenu;
        settings.UseNativeContextMenus = _general.UseNativeContextMenus;
        settings.TreeSortMode = _general.SelectedTreeSort?.Mode ?? "AsciiFirst";
        settings.SessionManagerVisibilityMode = _general.SelectedSessionManagerMode switch
        {
            "常开 (Always Visible)" => PanelVisibilityMode.AlwaysVisible,
            "常关 (Always Hidden)" => PanelVisibilityMode.AlwaysHidden,
            _ => PanelVisibilityMode.RememberLastState
        };
        settings.ComposeBarVisibilityMode = _general.SelectedComposeBarMode switch
        {
            "常开 (Always Visible)" => PanelVisibilityMode.AlwaysVisible,
            "常关 (Always Hidden)" => PanelVisibilityMode.AlwaysHidden,
            _ => PanelVisibilityMode.RememberLastState
        };
        settings.FileTransfer.CacheDirectory = _fileTransfer.CacheDirectory;
        settings.FileTransfer.WatcherMode = Enum.TryParse<FileWatcherMode>(_fileTransfer.SelectedWatcherMode, out var wm) ? wm : FileWatcherMode.Auto;

        // 外观：主题值归一化，仅接受 Dark/System
        settings.UiTheme = _appearance.NormalizedThemeKey;
        settings.ControlLibraryTheme = _appearance.NormalizedControlLibraryKey;
        settings.TreeDensityPreset = _appearance.SelectedDensityPreset?.Key ?? "Compact";
        settings.TreeItemHeight = Math.Clamp(_appearance.TreeItemHeight, 16.0, 40.0);
        settings.TreeFontSize = Math.Clamp(_appearance.TreeFontSize, 9.0, 18.0);
        settings.TreeIconSize = Math.Clamp(_appearance.TreeIconSize, 10.0, 24.0);
        settings.TreeIndent = Math.Clamp(_appearance.TreeIndent, 6.0, 32.0);
        settings.UiFontFamily = string.IsNullOrWhiteSpace(_appearance.UiFontFamily)
            ? "Noto Sans CJK SC"
            : _appearance.UiFontFamily.Trim();
        settings.UiFallbackFontFamily = _appearance.GetUiFallbackFontsString();
        settings.FontFamily = string.IsNullOrWhiteSpace(_appearance.FontFamily) ? "monospace" : _appearance.FontFamily.Trim();
        settings.TerminalFallbackFontFamily = string.IsNullOrWhiteSpace(_appearance.FallbackFontFamily)
            ? "Noto Sans Mono CJK SC, Source Han Sans HW SC, Microsoft YaHei, monospace"
            : _appearance.FallbackFontFamily.Trim();
        settings.FontSize = Math.Clamp(_appearance.FontSize, TerminalFontZoom.MinFontSize, TerminalFontZoom.MaxFontSize);
        settings.CursorBlink = _appearance.CursorBlink;
        settings.TabPlacement = _appearance.SelectedTabPlacement?.Key ?? "Top";
        settings.ActiveGuiProfileId = _appearance.SelectedGuiProfile?.Id;
        settings.ActiveTerminalProfileId = _appearance.SelectedTerminalProfile?.Id;

        // 终端
        settings.ScrollbackLines = Math.Clamp(_terminal.ScrollbackLines, 500, 50000);
        settings.DefaultTerminalType = string.IsNullOrWhiteSpace(_terminal.DefaultTerminalType)
            ? "xterm-256color"
            : _terminal.DefaultTerminalType.Trim();
        settings.TabTitleFollowsRemote = _terminal.TabTitleFollowsRemote;
        settings.CwdFollowMode = _terminal.SelectedCwdFollow?.Mode ?? CwdFollowMode.Off;

        // SSH
        settings.DefaultPort = _ssh.DefaultPort is >= 1 and <= 65535 ? _ssh.DefaultPort : 22;
        settings.DefaultUsername = string.IsNullOrWhiteSpace(_ssh.DefaultUsername) ? "" : _ssh.DefaultUsername.Trim();
        settings.DefaultIdentityId = _ssh.SelectedIdentityId;
        settings.PreferSystemAgent = _ssh.PreferSystemAgent;
        settings.LockTimeoutMinutes = Math.Clamp(_ssh.LockTimeoutMinutes, 0, 120);
        settings.ConnectTimeoutSeconds = Math.Clamp(_ssh.ConnectTimeoutSeconds, 3, 300);
        settings.KeepAliveIntervalSeconds = Math.Clamp(_ssh.KeepAliveIntervalSeconds, 0, 300);
        settings.EnableAgentForwarding = _ssh.EnableAgentForwarding;
        settings.CustomAgentSocketPath = string.IsNullOrWhiteSpace(_ssh.CustomAgentSocketPath)
            ? null
            : _ssh.CustomAgentSocketPath.Trim();

        IsBusy = true;
        try
        {
            var selectedGuiId = _appearance.SelectedGuiProfile?.Id;
            var selectedTermId = _appearance.SelectedTerminalProfile?.Id;

            // 冻结提交快照，await 期间不再读取可变草稿
            var terminalSnapshot = _profileManager.SnapshotWorkingTerminalProfiles();

            try
            {
                // 先写设置，再原子写 profiles；两份都成功才推进已提交权威
                await _settingsService.SaveSettingsAsync(settings);

                if (!string.IsNullOrWhiteSpace(selectedGuiId))
                {
                    _profileManager.SetActiveGuiProfile(selectedGuiId);
                }

                // 确认默认选择，并把冻结快照原子落盘（成功后管理器内部推进权威）
                _profileManager.NotifyDefaultProfileSelectionChanged(selectedTermId);
                await _profileManager.CommitAndSaveProfilesAsync(terminalSnapshot);
            }
            catch (Exception ex)
            {
                // 任一写入失败：回滚到已提交权威（内存 + 尽力补偿），不推进基准、不假装成功
                var compensationError = await RestoreCommittedStateAsync(persistSettings: true);
                var message = compensationError == null
                    ? ex.Message
                    : $"{ex.Message}（恢复已提交状态失败：{compensationError}）";
                await NotifyAsync("设置保存失败", message);
                return false;
            }

            _lastAppliedSettings = CloneSettings(settings);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 选择即预览：只更新管理器有效默认并广播，不落盘、不改已提交权威
    private void OnTerminalProfileDraftPreview(TerminalProfile? value)
    {
        // busy 期间（提交/取消进行中）阻止草稿变更广播
        if (_isReloading || IsBusy || value == null || string.IsNullOrWhiteSpace(value.Id))
        {
            return;
        }

        _profileManager.NotifyDefaultProfileSelectionChanged(value.Id);
    }

    // 回滚到“已提交”权威：恢复 working + 广播内容/默认变化 + 界面同步；可选补偿写回
    private async Task<string?> RestoreCommittedStateAsync(bool persistSettings)
    {
        // 1. 恢复有效状态；管理器对受影响 ID 广播 ProfileContentEdited（显式标签也刷新）
        _profileManager.RestoreWorkingToCommitted();

        // 2. 广播默认选择恢复（继承标签刷新）
        _profileManager.NotifyDefaultProfileSelectionChanged(_profileManager.DefaultTerminalProfileId);
        Services.ProfileManagerService.ApplyGuiProfile(_profileManager.ActiveGuiProfile);

        // 3. 界面与权威对齐（抑制预览广播）
        SyncAppearanceToCommitted();

        if (!persistSettings)
        {
            return null;
        }

        // 4. 补偿写回：settings 恢复基准；profiles 写回权威。失败明确上报，绝不谎报成功
        var errors = new List<string>();
        try
        {
            await _settingsService.SaveSettingsAsync(_lastAppliedSettings);
        }
        catch (Exception ex)
        {
            errors.Add($"settings: {ex.Message}");
        }

        try
        {
            await _profileManager.SaveCommittedProfilesAsync();
        }
        catch (Exception ex)
        {
            errors.Add($"profiles: {ex.Message}");
        }

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    // 用管理器的已提交权威刷新设置界面选中项（含 GUI/终端）
    private void SyncAppearanceToCommitted()
    {
        _isReloading = true;
        try
        {
            _appearance.SetProfiles(
                _profileManager.AllGuiProfiles,
                _profileManager.ActiveGuiProfile.Id,
                _profileManager.AllTerminalProfiles,
                _profileManager.DefaultTerminalProfile.Id);
        }
        finally
        {
            _isReloading = false;
        }
    }

    private async Task NotifyAsync(string title, string message)
    {
        if (ShowNotificationAsync != null)
        {
            await ShowNotificationAsync(title, message);
        }
    }

    // 完整深拷贝（与设置文件同一套 JSON 序列化），新增字段自动覆盖，无需逐项登记
    private static AppSettings CloneSettings(AppSettings source)
        => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(source))!;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!await ApplyChangesAsync())
        {
            return;
        }

        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!await ApplyChangesAsync())
        {
            return;
        }

        IsConfirmed = true;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        // busy 期间（提交进行中）不得触发关闭，避免与提交交叉落盘
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 回滚到“已提交”权威（预览期间的草稿/新增在此清除）
            await RestoreCommittedStateAsync(persistSettings: false);
        }
        catch (Exception ex)
        {
            await NotifyAsync("取消回滚失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        IsConfirmed = false;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        if (IsBusy || SaveBundleFileDialogAsync == null) return;
        var filePath = await SaveBundleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            // 导出“已提交”权威，不含未应用草稿
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
        if (IsBusy || OpenBundleFileDialogAsync == null) return;
        var filePath = await OpenBundleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            // 纳入父事务：只并入有效状态，Apply 时统一提交；取消无副作用、不落盘
            await _profileManager.ImportBundleAsync(json, overwrite: true, persist: false);
            RefreshAllProfiles();
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
        if (IsBusy || OpenTerminalProfileEditDialogAsync == null) return;

        var draft = new TerminalProfile { Name = "新建终端主题" };
        var confirmed = await OpenTerminalProfileEditDialogAsync(draft, _appearance.DraftFontSnapshot);
        if (confirmed == null) return;

        // 子编辑器确认：仅并入父设置草稿并广播，不提前持久化
        _profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
        RefreshTerminalProfiles(confirmed.Id);
    }

    [RelayCommand]
    private async Task EditTerminalProfileAsync()
    {
        if (IsBusy || OpenTerminalProfileEditDialogAsync == null || _appearance.SelectedTerminalProfile == null) return;

        var confirmed = await OpenTerminalProfileEditDialogAsync(
            _appearance.SelectedTerminalProfile,
            _appearance.DraftFontSnapshot);
        if (confirmed == null) return;

        // 同 ID 内容修改：并入草稿并广播内容变更（ID 不变也刷新），不提前持久化
        _profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
        RefreshTerminalProfiles(confirmed.Id);
    }

    [RelayCommand]
    private async Task ImportKonsoleSchemeAsync()
    {
        if (IsBusy || OpenKonsoleFileDialogAsync == null || OpenTerminalProfileEditDialogAsync == null) return;
        var filePath = await OpenKonsoleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            var content = await System.IO.File.ReadAllTextAsync(filePath);
            var defaultName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var parsed = Kei.Term.Core.Services.KonsoleColorSchemeParser.Parse(content, defaultName);

            // 使用当前字体草稿快照；配色导入不读取也不写回字体设置
            var confirmed = await OpenTerminalProfileEditDialogAsync(parsed, _appearance.DraftFontSnapshot);
            if (confirmed == null) return; // 取消无副作用

            // 确认后才并入父草稿并广播；落盘延后到“应用”
            _profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
            RefreshTerminalProfiles(confirmed.Id);
        }
        catch (Exception ex)
        {
            await NotifyAsync(Strings.Get("TerminalProfileEdit.Title"), ex.Message);
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

    // Bundle 导入后刷新两套 Profile 下拉（抑制预览广播）
    private void RefreshAllProfiles()
    {
        _isReloading = true;
        try
        {
            _appearance.SetProfiles(
                _profileManager.AllGuiProfiles,
                _profileManager.ActiveGuiProfile.Id,
                _profileManager.AllTerminalProfiles,
                _profileManager.DefaultTerminalProfile.Id);
        }
        finally
        {
            _isReloading = false;
        }
    }
}
