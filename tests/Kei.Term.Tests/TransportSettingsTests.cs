using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class TransportSettingsTests
{
    [Fact]
    public void TransportSettingsPage_InitializesWithDefaults_AndTogglesState()
    {
        var page = new TransportSettingsPage();

        Assert.Equal(TransportEngine.BuiltIn, page.SelectedEngine);
        Assert.False(page.IsGoCoreSelected);
        Assert.NotEmpty(page.CurrentEngineDescription);
        Assert.Equal(2, page.EngineOptions.Count);
        Assert.NotEmpty(page.Features);

        // 切换到 Go 核心引擎
        page.SelectedEngine = TransportEngine.GoCore;
        Assert.True(page.IsGoCoreSelected);
        Assert.Equal(TransportEngine.GoCore, page.SelectedEngineOption?.Engine);
    }

    [Fact]
    public void SettingsViewModel_RegistersTransportPageInConnectionGroup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_transport_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var service = new JsonSettingsService(Path.Combine(directory, "settings.json"));
            var vm = new SettingsViewModel(service, directory);

            var transportCategory = vm.Categories.FirstOrDefault(c => c.Page is TransportSettingsPage);
            Assert.NotNull(transportCategory);
            Assert.Equal(Strings.Get("Settings.Groups.Connection"), transportCategory.Group);
            Assert.Equal(SettingsIcons.Transport, transportCategory.Icon);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void SettingsViewModel_SyncsTimeoutAndKeepAlive_BetweenTransportAndSsh()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_transport_sync_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var service = new JsonSettingsService(Path.Combine(directory, "settings.json"));
            var vm = new SettingsViewModel(service, directory);

            // 修改 Transport 上的超时与保活，验证 SSH 页面自动同步
            vm.Transport.ConnectTimeoutSeconds = 85;
            vm.Transport.KeepAliveIntervalSeconds = 45;

            var sshCategory = vm.Categories.First(c => c.Page is SshSettingsPage);
            var sshPage = (SshSettingsPage)sshCategory.Page!;
            Assert.Equal(85, sshPage.ConnectTimeoutSeconds);
            Assert.Equal(45, sshPage.KeepAliveIntervalSeconds);

            // 反向在 SSH 页面修改，Transport 页面同步
            sshPage.ConnectTimeoutSeconds = 120;
            sshPage.KeepAliveIntervalSeconds = 15;
            Assert.Equal(120, vm.Transport.ConnectTimeoutSeconds);
            Assert.Equal(15, vm.Transport.KeepAliveIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task SettingsViewModel_SavesSelectedTransportEngine()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_transport_save_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            string settingsPath = Path.Combine(directory, "settings.json");
            var service = new JsonSettingsService(settingsPath);
            await service.SaveSettingsAsync(new AppSettings { TransportEngine = TransportEngine.BuiltIn });

            var vm = new SettingsViewModel(service, directory);
            Assert.Equal(TransportEngine.BuiltIn, vm.Transport.SelectedEngine);

            // 切换为 GoCore 并保存
            vm.Transport.SelectedEngine = TransportEngine.GoCore;
            bool applied = await vm.ApplyChangesAsync();
            Assert.True(applied);

            // 重新从磁盘加载校验
            var reloaded = new JsonSettingsService(settingsPath);
            await reloaded.LoadSettingsAsync();
            Assert.Equal(TransportEngine.GoCore, reloaded.Current.TransportEngine);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void AppSettings_LegacySshTransportEngine_DeserializesCorrectly()
    {
        // 验证旧版包含 SshTransportEngine 键的 JSON 配置能够顺利反序列化并映射到新版 TransportEngine
        string legacyJson = """
        {
            "SshTransportEngine": 1
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.NotNull(settings);
        Assert.Equal(TransportEngine.GoCore, settings.TransportEngine);
        Assert.Equal(TransportEngine.GoCore, settings.SshTransportEngine);
    }

    [Fact]
    public void SessionBehaviorOptions_DescribeAndInherit_TransportEngine()
    {
        string builtInDesc = SessionBehaviorOptions.Describe(TransportEngine.BuiltIn);
        string goCoreDesc = SessionBehaviorOptions.Describe(TransportEngine.GoCore);

        Assert.NotEmpty(builtInDesc);
        Assert.NotEmpty(goCoreDesc);
        Assert.NotEqual(builtInDesc, goCoreDesc);

        var options = SessionBehaviorOptions.InheritableTransportEngine(TransportEngine.BuiltIn);
        Assert.Equal(3, options.Count);
        Assert.Null(options[0].Value); // 继承项
        Assert.Equal(TransportEngine.BuiltIn, options[1].Value);
        Assert.Equal(TransportEngine.GoCore, options[2].Value);
    }

    [Fact]
    public Task SessionEditWindow_TransportEngineComboBox_ExistsAndIsPopulated()
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        var session = new SessionNode { Name = "test-node", Host = "127.0.0.1" };
        var editVm = new SessionEditViewModel(session, null, [], new AppSettings());
        var editWindow = new SessionEditWindow(editVm);
        try
        {
            editWindow.Show();
            editWindow.UpdateLayout();

            var combo = editWindow.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>()
                .FirstOrDefault(c => c.Name == "SessionTransportEngineComboBox");
            Assert.NotNull(combo);
            Assert.NotNull(combo.ItemsSource);
            Assert.Equal(3, combo.ItemCount);
        }
        finally
        {
            editWindow.Close();
        }
    });
}
