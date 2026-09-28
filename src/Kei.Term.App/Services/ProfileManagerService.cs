using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.Services;

// 终端配色变更原因：区分“默认选择变化”与“同 ID 内容编辑”，两者刷新范围不同
public enum TerminalProfileChangeReason
{
    DefaultSelectionChanged,
    ProfileContentEdited
}

public readonly record struct TerminalProfileChange(string? ProfileId, TerminalProfileChangeReason Reason);

// profiles.json 写入抽象：生产用原子写，测试可注入失败实现验证旧文件保留
public interface IProfileStore
{
    Task WriteAsync(string filePath, string json, CancellationToken ct = default);
}

// 原子写：同目录临时文件 + flush 后替换正式文件；失败不触碰正式文件，也不删除用户文件
public sealed class AtomicProfileStore : IProfileStore
{
    public async Task WriteAsync(string filePath, string json, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), ct);
                await writer.FlushAsync(ct);
                // 刷到磁盘后再替换，避免半截文件顶替旧文件
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            // 清理临时文件；正式文件保持原样
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 清理失败不影响“保留旧文件”的语义
            }

            throw;
        }
    }
}

public class ProfileManagerService
{
    private readonly ISettingsService _settingsService;
    private readonly IProfileStore _profileStore;
    private readonly string _profilesDirectory;
    private readonly string _customProfilesFilePath;

    // 有效（working）状态：包含设置页预览草稿，供解析与标签刷新
    private readonly List<GuiProfile> _customGuiProfiles = [];
    private readonly List<TerminalProfile> _customTerminalProfiles = [];
    private TabBarSettings _tabBarSettings = new();

    private string? _selectedGuiProfileId;
    private string? _defaultTerminalProfileId;

    // 已提交权威：只有成功持久化（或磁盘加载）过的内容；是回滚与导出的唯一来源
    private List<GuiProfile> _committedGuiProfiles = [];
    private List<TerminalProfile> _committedTerminalProfiles = [];
    private TabBarSettings _committedTabBarSettings = new();
    private string? _committedSelectedGuiProfileId;
    private string? _committedDefaultTerminalProfileId;

    public ProfileManagerService(ISettingsService settingsService, string? dataDirectory = null, IProfileStore? profileStore = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _profileStore = profileStore ?? new AtomicProfileStore();
        _profilesDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "KeiTerm");
        _customProfilesFilePath = Path.Combine(_profilesDirectory, "profiles.json");
    }

    public TabBarSettings TabBarSettings => _tabBarSettings;

    // 终端配色变更唯一广播源（MainViewModel 单点订阅）
    public event Action<TerminalProfileChange>? TerminalProfileChanged;

    public IReadOnlyList<GuiProfile> AllGuiProfiles =>
        BuiltInPresets.DefaultGuiProfiles.Concat(_customGuiProfiles).ToList().AsReadOnly();

    public IReadOnlyList<TerminalProfile> AllTerminalProfiles =>
        BuiltInPresets.DefaultTerminalProfiles.Concat(_customTerminalProfiles).ToList().AsReadOnly();

    public GuiProfile ActiveGuiProfile
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_selectedGuiProfileId))
            {
                var found = AllGuiProfiles.FirstOrDefault(p => string.Equals(p.Id, _selectedGuiProfileId, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found;
            }
            return BuiltInPresets.GetDefaultGuiProfile();
        }
    }

    public TerminalProfile DefaultTerminalProfile
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_defaultTerminalProfileId))
            {
                var found = AllTerminalProfiles.FirstOrDefault(p => string.Equals(p.Id, _defaultTerminalProfileId, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found;
            }
            return BuiltInPresets.GetDefaultTerminalProfile();
        }
    }

    public TerminalProfile GetTerminalProfile(string? profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var found = AllTerminalProfiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
        }
        return DefaultTerminalProfile;
    }

    // 当前有效（可能是预览草稿）的全局默认终端配色 ID；null 表示使用内置默认
    public string? DefaultTerminalProfileId => _defaultTerminalProfileId;

    // 统一解析顺序：会话显式 ID → 全局默认 ID → 内置默认
    public TerminalProfile ResolveEffectiveTerminalProfile(string? explicitProfileId)
    {
        if (!string.IsNullOrWhiteSpace(explicitProfileId))
        {
            var explicitProfile = AllTerminalProfiles.FirstOrDefault(
                p => string.Equals(p.Id, explicitProfileId, StringComparison.OrdinalIgnoreCase));
            if (explicitProfile != null) return explicitProfile;
        }

        return DefaultTerminalProfile;
    }

    // 广播“默认选择变化”：设置页选择即预览时使用，不落盘
    public void NotifyDefaultProfileSelectionChanged(string? profileId)
    {
        _defaultTerminalProfileId = profileId;
        TerminalProfileChanged?.Invoke(new TerminalProfileChange(profileId, TerminalProfileChangeReason.DefaultSelectionChanged));
    }

    public async Task InitializeAsync()
    {
        // 加载持久化的自定义 Profile 与选中的 Profile ID
        if (File.Exists(_customProfilesFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_customProfilesFilePath);
                var bundle = ProfileBundleSerializer.Deserialize(json);
                _customGuiProfiles.Clear();
                _customGuiProfiles.AddRange(bundle.GuiProfiles.Where(p => !p.IsBuiltIn));
                _customTerminalProfiles.Clear();
                _customTerminalProfiles.AddRange(bundle.TerminalProfiles.Where(p => !p.IsBuiltIn));
                _tabBarSettings = bundle.TabBarSettings ?? new();
                _selectedGuiProfileId = bundle.SelectedGuiProfileId;
                _defaultTerminalProfileId = bundle.DefaultTerminalProfileId;
            }
            catch
            {
                // 若用户配置文件损坏，保留内置默认
            }
        }

        // 磁盘加载即为已提交权威
        CaptureCommittedFromWorking();

        // 应用当前的 GUI Profile 笔刷
        ApplyGuiProfile(ActiveGuiProfile);
    }

    // 持久化当前“已提交”权威（补偿写回用；不推进权威）
    public Task SaveCommittedProfilesAsync(CancellationToken ct = default)
    {
        var bundle = BuildBundle(
            _committedTerminalProfiles,
            _committedGuiProfiles,
            _committedSelectedGuiProfileId,
            _committedDefaultTerminalProfileId,
            _committedTabBarSettings);
        return _profileStore.WriteAsync(_customProfilesFilePath, ProfileBundleSerializer.Serialize(bundle), ct);
    }

    // 将当前有效（working）状态原子落盘；仅写入成功后推进“已提交”权威。
    // 可选 terminalProfilesOverride：提交前捕获的不可变快照，避免提交期间被并发草稿污染。
    public async Task CommitAndSaveProfilesAsync(
        IReadOnlyList<TerminalProfile>? terminalProfilesOverride = null,
        CancellationToken ct = default)
    {
        var terminal = terminalProfilesOverride != null
            ? terminalProfilesOverride.Select(p => p.DeepCopy()).ToList()
            : _customTerminalProfiles.Select(p => p.DeepCopy()).ToList();
        var gui = _customGuiProfiles.Select(CloneGui).ToList();
        var tabBar = CloneTabBar(_tabBarSettings);
        var selectedGui = _selectedGuiProfileId ?? ActiveGuiProfile.Id;
        var defaultTerminal = _defaultTerminalProfileId ?? DefaultTerminalProfile.Id;

        var bundle = BuildBundle(terminal, gui, selectedGui, defaultTerminal, tabBar);
        await _profileStore.WriteAsync(_customProfilesFilePath, ProfileBundleSerializer.Serialize(bundle), ct);

        // 写入成功后才推进权威
        _committedTerminalProfiles = terminal;
        _committedGuiProfiles = gui;
        _committedTabBarSettings = tabBar;
        _committedSelectedGuiProfileId = selectedGui;
        _committedDefaultTerminalProfileId = defaultTerminal;
    }

    // 回滚有效状态到“已提交”权威；对受影响终端方案 ID 广播内容编辑（确保显式标签也刷新）
    public IReadOnlyList<string> RestoreWorkingToCommitted()
    {
        var affected = _customTerminalProfiles.Select(p => p.Id)
            .Concat(_committedTerminalProfiles.Select(p => p.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _customGuiProfiles.Clear();
        _customGuiProfiles.AddRange(_committedGuiProfiles.Select(CloneGui));
        _customTerminalProfiles.Clear();
        _customTerminalProfiles.AddRange(_committedTerminalProfiles.Select(p => p.DeepCopy()));
        _tabBarSettings = CloneTabBar(_committedTabBarSettings);
        _selectedGuiProfileId = _committedSelectedGuiProfileId;
        _defaultTerminalProfileId = _committedDefaultTerminalProfileId;

        foreach (var id in affected)
        {
            TerminalProfileChanged?.Invoke(new TerminalProfileChange(id, TerminalProfileChangeReason.ProfileContentEdited));
        }

        return affected;
    }

    public void SetActiveGuiProfile(string profileId)
    {
        _selectedGuiProfileId = profileId;
        ApplyGuiProfile(ActiveGuiProfile);
    }

    public void SetDefaultTerminalProfile(string profileId)
    {
        _defaultTerminalProfileId = profileId;
    }

    public void SetTabBarSettings(TabBarSettings settings)
    {
        _tabBarSettings = settings ?? new();
    }

    public void AddCustomGuiProfile(GuiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.IsBuiltIn = false;
        _customGuiProfiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        _customGuiProfiles.Add(profile);
    }

    public void AddCustomTerminalProfile(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.IsBuiltIn = false;
        _customTerminalProfiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        _customTerminalProfiles.Add(profile);
    }

    // 新增或按同 ID 覆盖自定义方案（仅进有效状态，不落盘）；无论 ID 是否变化都广播内容编辑，
    // 否则“同 ID 修改”不会通知已使用该 ID 的标签刷新。
    public void AddOrUpdateCustomTerminalProfile(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.IsBuiltIn = false;
        _customTerminalProfiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        _customTerminalProfiles.Add(profile);
        TerminalProfileChanged?.Invoke(new TerminalProfileChange(profile.Id, TerminalProfileChangeReason.ProfileContentEdited));
    }

    // 从有效状态移除自定义方案（丢弃未提交草稿）
    public void RemoveCustomTerminalProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _customTerminalProfiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
    }

    // 当前有效终端方案深拷贝快照：设置事务提交前冻结，避免 await 期间被修改
    public IReadOnlyList<TerminalProfile> SnapshotWorkingTerminalProfiles()
        => _customTerminalProfiles.Select(p => p.DeepCopy()).ToList().AsReadOnly();

    // 导出“已提交”权威；不含未应用草稿
    public string ExportBundle()
    {
        var bundle = BuildBundle(
            _committedTerminalProfiles,
            _committedGuiProfiles,
            _committedSelectedGuiProfileId ?? BuiltInPresets.GetDefaultGuiProfile().Id,
            _committedDefaultTerminalProfileId ?? BuiltInPresets.GetDefaultTerminalProfile().Id,
            _committedTabBarSettings);
        return ProfileBundleSerializer.Serialize(bundle);
    }

    // persist=false：仅并入有效状态（设置页父事务），由父流程 Apply 时统一提交
    // persist=true ：独立导入，合并后立即原子落盘并推进权威
    public async Task ImportBundleAsync(string json, bool overwrite, bool persist = true, CancellationToken ct = default)
    {
        var bundle = ProfileBundleSerializer.Deserialize(json);

        // 记录合并前的有效默认与受影响 terminal ID（旧 ∪ 新），用于合并后广播
        var previousEffectiveDefaultId = ResolveEffectiveTerminalProfile(null).Id;
        var affectedTerminalIds = new HashSet<string>(
            _customTerminalProfiles.Select(p => p.Id),
            StringComparer.OrdinalIgnoreCase);

        var incomingGui = bundle.GuiProfiles.Where(p => !p.IsBuiltIn).ToList();
        var mergedGui = ProfileBundleSerializer.MergeProfiles(_customGuiProfiles, incomingGui, overwrite);
        _customGuiProfiles.Clear();
        _customGuiProfiles.AddRange(mergedGui);

        var incomingTerm = bundle.TerminalProfiles.Where(p => !p.IsBuiltIn).ToList();
        var mergedTerm = ProfileBundleSerializer.MergeProfiles(_customTerminalProfiles, incomingTerm, overwrite);
        _customTerminalProfiles.Clear();
        _customTerminalProfiles.AddRange(mergedTerm);

        if (bundle.TabBarSettings != null && (overwrite || _tabBarSettings == null))
        {
            _tabBarSettings = bundle.TabBarSettings;
        }

        if (!string.IsNullOrWhiteSpace(bundle.SelectedGuiProfileId))
        {
            _selectedGuiProfileId = bundle.SelectedGuiProfileId;
        }
        if (!string.IsNullOrWhiteSpace(bundle.DefaultTerminalProfileId))
        {
            _defaultTerminalProfileId = bundle.DefaultTerminalProfileId;
        }

        // persist=true：必须先成功落盘，成功之后才广播（不引入失败前的错误成功广播）
        if (persist)
        {
            await CommitAndSaveProfilesAsync(ct: ct);
        }

        foreach (var id in _customTerminalProfiles.Select(p => p.Id))
        {
            affectedTerminalIds.Add(id);
        }

        // 合并完成后按受影响 ID 广播内容变化：显式使用该 ID 的标签也会刷新
        foreach (var id in affectedTerminalIds)
        {
            TerminalProfileChanged?.Invoke(new TerminalProfileChange(id, TerminalProfileChangeReason.ProfileContentEdited));
        }

        // 有效默认变化时广播默认选择：已开继承标签与新标签/Demo 保持一致
        var newEffectiveDefaultId = ResolveEffectiveTerminalProfile(null).Id;
        if (!string.Equals(previousEffectiveDefaultId, newEffectiveDefaultId, StringComparison.OrdinalIgnoreCase))
        {
            TerminalProfileChanged?.Invoke(new TerminalProfileChange(_defaultTerminalProfileId, TerminalProfileChangeReason.DefaultSelectionChanged));
        }

        ApplyGuiProfile(ActiveGuiProfile);
    }

    public static void ApplyGuiProfile(GuiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyGuiProfile(profile));
            return;
        }

        var app = Application.Current;
        if (app == null) return;

        UpdateBrush(app, "Kei.Bg.Window", profile.WindowBackground);
        UpdateBrush(app, "Kei.Bg.Panel", profile.PanelBackground);
        UpdateBrush(app, "Kei.Bg.PanelAlt", profile.PanelAltBackground);
        UpdateBrush(app, "Kei.Border", profile.BorderBrush);
        UpdateBrush(app, "Kei.Text.Primary", profile.PrimaryText);
        UpdateBrush(app, "Kei.Text.Secondary", profile.SecondaryText);
        UpdateBrush(app, "Kei.Accent", profile.AccentColor);
        UpdateBrush(app, "Kei.Accent.Hover", profile.AccentHover);
    }

    private void CaptureCommittedFromWorking()
    {
        _committedGuiProfiles = _customGuiProfiles.Select(CloneGui).ToList();
        _committedTerminalProfiles = _customTerminalProfiles.Select(p => p.DeepCopy()).ToList();
        _committedTabBarSettings = CloneTabBar(_tabBarSettings);
        _committedSelectedGuiProfileId = _selectedGuiProfileId;
        _committedDefaultTerminalProfileId = _defaultTerminalProfileId;
    }

    private static ProfileBundle BuildBundle(
        IEnumerable<TerminalProfile> terminalProfiles,
        IEnumerable<GuiProfile> guiProfiles,
        string? selectedGuiProfileId,
        string? defaultTerminalProfileId,
        TabBarSettings tabBarSettings) => new()
        {
            Version = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            SelectedGuiProfileId = selectedGuiProfileId,
            DefaultTerminalProfileId = defaultTerminalProfileId,
            TabBarSettings = tabBarSettings ?? new(),
            GuiProfiles = guiProfiles.ToList(),
            TerminalProfiles = terminalProfiles.ToList()
        };

    private static GuiProfile CloneGui(GuiProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        IsBuiltIn = source.IsBuiltIn,
        WindowBackground = source.WindowBackground,
        PanelBackground = source.PanelBackground,
        PanelAltBackground = source.PanelAltBackground,
        BorderBrush = source.BorderBrush,
        PrimaryText = source.PrimaryText,
        SecondaryText = source.SecondaryText,
        AccentColor = source.AccentColor,
        AccentHover = source.AccentHover
    };

    private static TabBarSettings CloneTabBar(TabBarSettings? source) => source == null
        ? new TabBarSettings()
        : new TabBarSettings
        {
            Placement = source.Placement,
            ConnectingColor = source.ConnectingColor,
            ConnectedColor = source.ConnectedColor,
            DisconnectedColor = source.DisconnectedColor,
            ErrorColor = source.ErrorColor
        };

    private static void UpdateBrush(Application app, string key, string hexColor)
    {
        if (Color.TryParse(hexColor, out var color))
        {
            app.Resources[key] = new SolidColorBrush(color);
        }
    }
}
