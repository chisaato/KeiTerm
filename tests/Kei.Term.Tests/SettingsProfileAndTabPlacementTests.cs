using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SettingsProfileAndTabPlacementTests
{
    [Fact]
    public async Task Settings_SaveReloadPreservesProfilesAndTabPlacement_ExportPreservesProfileSelection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"keiterm_profiles_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var settingsService = new JsonSettingsService(settingsPath);
            var profileManager = new Kei.Term.App.Services.ProfileManagerService(settingsService, tempDir);
            await profileManager.InitializeAsync();

            var vm = new SettingsViewModel(settingsService, tempDir, profileManager: profileManager);
            var appearancePage = Assert.Single(vm.Categories, c => c.Page is AppearanceSettingsPage).Page as AppearanceSettingsPage;
            Assert.NotNull(appearancePage);

            // 切换为其他 Profile
            var targetGui = appearancePage.GuiProfiles.Last();
            var targetTerm = appearancePage.TerminalProfiles.Last();
            appearancePage.SelectedGuiProfile = targetGui;
            appearancePage.SelectedTerminalProfile = targetTerm;
            appearancePage.SetTabPlacement("Bottom");

            // 保存设置
            await vm.SaveCommand.ExecuteAsync(null);

            // 新建服务从磁盘重读，避免只验证同一个内存对象。
            JsonSettingsService reader = new(settingsPath);
            await reader.LoadSettingsAsync();
            Assert.Equal(targetGui.Id, reader.Current.ActiveGuiProfileId);
            Assert.Equal(targetTerm.Id, reader.Current.ActiveTerminalProfileId);
            Assert.Equal("Bottom", reader.Current.TabPlacement);

            // 验证 ProfileManagerService 中已生效
            Assert.Equal(targetGui.Id, profileManager.ActiveGuiProfile.Id);
            Assert.Equal(targetTerm.Id, profileManager.DefaultTerminalProfile.Id);

            string bundlePath = Path.Combine(tempDir, "profiles.json");
            vm.SaveBundleFileDialogAsync = () => Task.FromResult<string?>(bundlePath);
            await vm.ExportBundleCommand.ExecuteAsync(null);
            ProfileBundle bundle = ProfileBundleSerializer.Deserialize(await File.ReadAllTextAsync(bundlePath));
            Assert.Equal(targetGui.Id, bundle.SelectedGuiProfileId);
            Assert.Equal(targetTerm.Id, bundle.DefaultTerminalProfileId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ApplyChanges_SavesTerminalBehavior_AndKeepsFieldsThisWindowDoesNotEdit()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"keiterm_settings_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new FixedSettingsService(new AppSettings
            {
                LastSessionManagerVisible = false,
                UiFontSize = 15,
                FileTransfer = new FileTransferSettings { PollingIntervalSeconds = 17, IsFileManagerOnLeft = true }
            });
            var vm = new SettingsViewModel(service, tempDir);
            TerminalSettingsPage terminal = vm.Categories.Select(c => c.Page).OfType<TerminalSettingsCombinedPage>().Single().Terminal;

            terminal.TabTitleFollowsRemote = false;
            terminal.SetCwdFollow(CwdFollowMode.Always);
            Assert.True(await vm.ApplyChangesAsync());

            Assert.False(service.Current.TabTitleFollowsRemote);
            Assert.Equal(CwdFollowMode.Always, service.Current.CwdFollowMode);
            // 设置窗口不编辑的字段必须原样保留（曾因整对象重建被重置为默认值）
            Assert.False(service.Current.LastSessionManagerVisible);
            Assert.Equal(15, service.Current.UiFontSize);
            Assert.Equal(17, service.Current.FileTransfer.PollingIntervalSeconds);
            Assert.True(service.Current.FileTransfer.IsFileManagerOnLeft);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // 通过真实设置页和文件验证开关双向保存，直到启动时的 X11 选项。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UseNativeGlobalMenu_SettingsWindowSave_ReloadsAndConfiguresStartup(bool enabled)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"keiterm_globalmenu_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        try
        {
            JsonSettingsService service = new(settingsPath);
            await service.SaveSettingsAsync(new AppSettings
            {
                UseNativeGlobalMenu = !enabled,
                LastSessionManagerVisible = false,
                FileTransfer = new FileTransferSettings { PollingIntervalSeconds = 17 }
            });
            SettingsViewModel vm = new(service, tempDir);
            GeneralSettingsPage general = vm.Categories.Select(c => c.Page).OfType<GeneralSettingsPage>().Single();

            Assert.Equal(!enabled, general.UseNativeGlobalMenu);

            general.UseNativeGlobalMenu = enabled;
            Assert.True(await vm.ApplyChangesAsync());

            JsonSettingsService reader = new(settingsPath);
            await reader.LoadSettingsAsync();
            SettingsViewModel reloaded = new(reader, tempDir);
            GeneralSettingsPage reloadedPage = reloaded.Categories.Select(c => c.Page).OfType<GeneralSettingsPage>().Single();

            Assert.Equal(enabled, reloadedPage.UseNativeGlobalMenu);
            Assert.Equal(enabled, NativeMenuSettings.CreateX11Options(settingsPath).UseDBusMenu);

            // 保存开关不应重置设置页未编辑的字段。
            Assert.False(reader.Current.LastSessionManagerVisible);
            Assert.Equal(17, reader.Current.FileTransfer.PollingIntervalSeconds);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // 首次启动、旧配置和损坏的配置都不得阻断窗口创建。
    [Theory]
    [InlineData(null)]
    [InlineData("{\"ConfirmBeforeClose\": false}")]
    [InlineData("{\"UseNativeGlobalMenu\":")]
    public async Task UseNativeGlobalMenu_MissingLegacyOrCorruptFile_AllowsDesktopMenu(string? json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"keiterm_globalmenu_json_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        try
        {
            if (json != null)
            {
                await File.WriteAllTextAsync(settingsPath, json);
            }

            Assert.True(NativeMenuSettings.CreateX11Options(settingsPath).UseDBusMenu);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UseNativeGlobalMenu_SettingsControl_ReflectsPlatformAndPreservesPreference(bool preference)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"keiterm_globalmenu_ui_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            JsonSettingsService service = new(Path.Combine(tempDir, "settings.json"));
            await service.SaveSettingsAsync(new AppSettings { UseNativeGlobalMenu = preference });

            await HeadlessAvalonia.RunAsync(() =>
            {
                SettingsViewModel vm = new(service, tempDir);
                GeneralSettingsPage general = vm.Categories.Select(c => c.Page).OfType<GeneralSettingsPage>().Single();
                SettingsWindow window = new(vm);
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    CheckBox toggle = window.GetVisualDescendants().OfType<CheckBox>().Single(c =>
                        Equals(c.Content, Strings.Get("Settings.General.UseNativeGlobalMenu")));

                    bool supported = OperatingSystem.IsLinux();
                    bool expected = supported ? preference : OperatingSystem.IsMacOS();
                    Assert.Equal(supported, toggle.IsEnabled);
                    Assert.Equal(expected, toggle.IsChecked);

                    if (supported)
                    {
                        // 操作真实控件验证双向绑定，反向赋值再验证变更通知。
                        toggle.IsChecked = !preference;
                        Assert.Equal(!preference, general.UseNativeGlobalMenu);
                        general.UseNativeGlobalMenu = preference;
                        Assert.Equal(preference, toggle.IsChecked);
                    }
                    else
                    {
                        // 不可切换的平台不能让显示状态覆盖已存储的 Linux 偏好。
                        general.IsGlobalMenuChecked = !expected;
                        Assert.Equal(preference, general.UseNativeGlobalMenu);
                        general.UseNativeGlobalMenu = !preference;
                        Assert.Equal(expected, toggle.IsChecked);
                    }
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
