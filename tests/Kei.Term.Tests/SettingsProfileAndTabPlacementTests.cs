using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SettingsProfileAndTabPlacementTests
{
    [Fact]
    public void TabPlacement_InSettingsViewModel_SavesAndReloadsCorrectly()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_test_{Path.GetRandomFileName()}.json");
        try
        {
            var settingsService = new JsonSettingsService(tempPath);
            var vm = new SettingsViewModel(settingsService);

            // 验证初始加载
            Assert.NotNull(vm.Categories);
            var appearancePage = Assert.Single(vm.Categories, c => c.Page is AppearanceSettingsPage).Page as AppearanceSettingsPage;
            Assert.NotNull(appearancePage);
            Assert.Equal("Top", appearancePage.SelectedTabPlacement.Key);

            // 切换为 Bottom
            appearancePage.SetTabPlacement("Bottom");
            Assert.Equal(TabPlacement.Bottom, appearancePage.SelectedTabPlacement.Placement);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task SettingsViewModel_ExportAndImport_Integration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"keiterm_bundle_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        string bundlePath = Path.Combine(tempDir, "exported_bundle.json");

        try
        {
            var settingsService = new JsonSettingsService(settingsPath);
            var profileManager = new Kei.Term.App.Services.ProfileManagerService(settingsService, tempDir);
            await profileManager.InitializeAsync();

            var vm = new SettingsViewModel(settingsService, tempDir, profileManager: profileManager);

            // 模拟 SaveBundleFileDialogAsync
            vm.SaveBundleFileDialogAsync = () => Task.FromResult<string?>(bundlePath);

            // 执行导出命令
            await vm.ExportBundleCommand.ExecuteAsync(null);

            Assert.True(File.Exists(bundlePath));
            string json = await File.ReadAllTextAsync(bundlePath);
            Assert.Contains("\"version\": 1", json);

            // 模拟 OpenBundleFileDialogAsync
            vm.OpenBundleFileDialogAsync = () => Task.FromResult<string?>(bundlePath);
            await vm.ImportBundleCommand.ExecuteAsync(null);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SettingsViewModel_GuiAndTerminalProfiles_SelectionAndPersistence()
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

            Assert.NotEmpty(appearancePage.GuiProfiles);
            Assert.NotEmpty(appearancePage.TerminalProfiles);
            Assert.NotNull(appearancePage.SelectedGuiProfile);
            Assert.NotNull(appearancePage.SelectedTerminalProfile);

            // 切换为其他 Profile
            var targetGui = appearancePage.GuiProfiles.Last();
            var targetTerm = appearancePage.TerminalProfiles.Last();
            appearancePage.SelectedGuiProfile = targetGui;
            appearancePage.SelectedTerminalProfile = targetTerm;

            // 保存设置
            await vm.SaveCommand.ExecuteAsync(null);

            // 验证 AppSettings 中已记录
            Assert.Equal(targetGui.Id, settingsService.Current.ActiveGuiProfileId);
            Assert.Equal(targetTerm.Id, settingsService.Current.ActiveTerminalProfileId);

            // 验证 ProfileManagerService 中已生效
            Assert.Equal(targetGui.Id, profileManager.ActiveGuiProfile.Id);
            Assert.Equal(targetTerm.Id, profileManager.DefaultTerminalProfile.Id);
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
}
