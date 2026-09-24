using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.Services;

public class ProfileManagerService
{
    private readonly ISettingsService _settingsService;
    private readonly string _profilesDirectory;
    private readonly string _customProfilesFilePath;

    private readonly List<GuiProfile> _customGuiProfiles = [];
    private readonly List<TerminalProfile> _customTerminalProfiles = [];
    private TabBarSettings _tabBarSettings = new();

    private string? _selectedGuiProfileId;
    private string? _defaultTerminalProfileId;

    public ProfileManagerService(ISettingsService settingsService, string? dataDirectory = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _profilesDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "KeiTerm");
        _customProfilesFilePath = Path.Combine(_profilesDirectory, "profiles.json");
    }

    public TabBarSettings TabBarSettings => _tabBarSettings;

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

        // 应用当前的 GUI Profile 笔刷
        ApplyGuiProfile(ActiveGuiProfile);
    }

    public async Task SaveProfilesAsync()
    {
        Directory.CreateDirectory(_profilesDirectory);
        var bundle = new ProfileBundle
        {
            Version = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            SelectedGuiProfileId = _selectedGuiProfileId ?? ActiveGuiProfile.Id,
            DefaultTerminalProfileId = _defaultTerminalProfileId ?? DefaultTerminalProfile.Id,
            TabBarSettings = _tabBarSettings,
            GuiProfiles = _customGuiProfiles,
            TerminalProfiles = _customTerminalProfiles
        };
        var json = ProfileBundleSerializer.Serialize(bundle);
        await File.WriteAllTextAsync(_customProfilesFilePath, json);
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

    public string ExportBundle()
    {
        var bundle = new ProfileBundle
        {
            Version = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            SelectedGuiProfileId = ActiveGuiProfile.Id,
            DefaultTerminalProfileId = DefaultTerminalProfile.Id,
            TabBarSettings = _tabBarSettings,
            GuiProfiles = AllGuiProfiles.ToList(),
            TerminalProfiles = AllTerminalProfiles.ToList()
        };
        return ProfileBundleSerializer.Serialize(bundle);
    }

    public async Task ImportBundleAsync(string json, bool overwrite)
    {
        var bundle = ProfileBundleSerializer.Deserialize(json);

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

        await SaveProfilesAsync();
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

    private static void UpdateBrush(Application app, string key, string hexColor)
    {
        if (Color.TryParse(hexColor, out var color))
        {
            app.Resources[key] = new SolidColorBrush(color);
        }
    }
}
