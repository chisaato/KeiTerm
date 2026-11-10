using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Ssh.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

public sealed class SessionConfigurationCommitTests
{
    [Fact]
    public Task NewSession_CommitsAfterOwnerSave_AndSurvivesRestart() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        Task operation = fixture.Model.CreateSessionCommand.ExecuteAsync(null);
        SessionEditViewModel draft = fixture.Editor();
        draft.Name = "serial";
        draft.SelectedConnectionProtocol = SessionProtocols.Serial;
        draft.SerialOsPort = "/dev/ttyOS";
        draft.SerialDevicePort = "/dev/ttyDevice";
        Assert.Empty(await fixture.Configuration.GetOverridesAsync(draft.NodeId, "serial"));
        draft.SaveCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.IsType<SessionNode>(Assert.Single(await fixture.Tree.GetAllNodesAsync()));
        var restarted = new DeviceConfigurationService(new SqliteDeviceConfigurationStore(fixture.Database),
            new PersistentDeviceId(Path.Combine(fixture.Root, "device-id")));
        var fields = await restarted.ResolveAsync(saved.Id, "serial", SessionEditViewModel.CurrentOperatingSystemName, new Dictionary<string, JsonElement>());
        Assert.Equal("/dev/ttyDevice", fields["portName"].GetString());
        Assert.Equal(2, (await restarted.GetOverridesAsync(saved.Id, "serial")).Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelOrOwnerSaveFailure_DoesNotPersistOrRetainDraft(bool failOwner) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        if (failOwner)
        {
            // 不存在的父节点触发真实 SQLite 外键失败，而不是替身回显。
            fixture.Model.SelectedTreeNode = new FolderNode { Name = "missing parent" };
        }
        Task operation = fixture.Model.CreateSessionCommand.ExecuteAsync(null);
        SessionEditViewModel draft = fixture.Editor();
        draft.Name = "not saved";
        draft.SelectedConnectionProtocol = SessionProtocols.Serial;
        draft.SerialDevicePort = "/dev/ttyDraft";
        (failOwner ? draft.SaveCommand : draft.CancelCommand).Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await fixture.Tree.GetAllNodesAsync());
        // 即使后续调用提交，也不能复活被取消或父保存失败的草稿。
        await fixture.Interaction.CommitPendingSessionConfigurationAsync(draft.NodeId);
        Assert.Empty(await fixture.Configuration.GetOverridesAsync(draft.NodeId, "serial"));
        if (failOwner) Assert.Contains(fixture.Model.ToastManager.ActiveToasts, t => t.Level == NotificationLevel.Error);
    });

    [Fact]
    public Task OverrideFailure_IsVisible_AndPendingDraftIsDiscarded() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(failOverrides: true);
        Task operation = fixture.Model.CreateSessionCommand.ExecuteAsync(null);
        SessionEditViewModel draft = fixture.Editor();
        draft.Name = "owner saved";
        draft.SelectedConnectionProtocol = SessionProtocols.Serial;
        draft.SerialOsPort = "/dev/ttyOS";
        draft.SaveCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(await fixture.Tree.GetAllNodesAsync());
        Assert.Contains(fixture.Model.ToastManager.ActiveToasts, t => t.Level == NotificationLevel.Error && t.Message.Contains("设备覆盖写入失败"));
        await fixture.Interaction.CommitPendingSessionConfigurationAsync(draft.NodeId);
        Assert.Empty(await fixture.Configuration.GetOverridesAsync(draft.NodeId, "serial"));
    });

    [Fact]
    public Task EditingDevicePort_PreservesUneditedFieldsAndOtherTargets() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = new SessionNode { Name = "serial", Protocol = SessionProtocols.Serial };
        await fixture.Tree.SaveNodeAsync(saved);
        var os = DeviceConfigurationTarget.ForOperatingSystem(SessionEditViewModel.CurrentOperatingSystemName);
        var current = DeviceConfigurationTarget.ForDevice(fixture.Device.EnsureCreated());
        var other = DeviceConfigurationTarget.ForDevice(Guid.NewGuid().ToString("N"));
        await fixture.Configuration.SaveOverrideAsync(saved.Id, "serial", os, Fields(new { baudRate = 57600, portName = "/dev/ttyOS" }));
        await fixture.Configuration.SaveOverrideAsync(saved.Id, "serial", current, Fields(new { dataBits = 7, portName = "/dev/ttyOld" }));
        await fixture.Configuration.SaveOverrideAsync(saved.Id, "serial", other, Fields(new { portName = "COM9" }));
        fixture.Model.SelectedTreeNode = saved;
        Task operation = fixture.Model.EditSelectedNodeCommand.ExecuteAsync(null);
        SessionEditViewModel draft = fixture.Editor();
        Assert.Equal("/dev/ttyOld", draft.SerialDevicePort);
        draft.SerialDevicePort = "/dev/ttyNew";
        draft.SaveCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        var records = await fixture.Configuration.GetOverridesAsync(saved.Id, "serial");
        Assert.Equal(57600, records.Single(r => r.Target == os).Fields["baudRate"].GetInt32());
        Assert.Equal("/dev/ttyOS", records.Single(r => r.Target == os).Fields["portName"].GetString());
        Assert.Equal(7, records.Single(r => r.Target == current).Fields["dataBits"].GetInt32());
        Assert.Equal("/dev/ttyNew", records.Single(r => r.Target == current).Fields["portName"].GetString());
        Assert.Equal("COM9", records.Single(r => r.Target == other).Fields["portName"].GetString());
    });

    [Fact]
    public Task PendingDraft_RejectsDifferentOwner() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        Task<SessionNode?> operation = fixture.Interaction.EditSessionAsync(null, null, []);
        var draft = fixture.Editor();
        draft.Name = "draft";
        draft.SelectedConnectionProtocol = SessionProtocols.Serial;
        draft.SerialDevicePort = "/dev/ttyDraft";
        draft.SaveCommand.Execute(null);
        Assert.NotNull(await operation);
        var other = new SessionNode { Name = "other" };
        await fixture.Tree.SaveNodeAsync(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Interaction.CommitPendingSessionConfigurationAsync(other.Id));
        Assert.Empty(await fixture.Configuration.GetOverridesAsync(other.Id, "serial"));
        fixture.Interaction.DiscardPendingSessionConfiguration(draft.NodeId);
    });

    private static Dictionary<string, JsonElement> Fields(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required SqliteConnectionFactory Database { get; init; }
        public required SqliteTreeRepository Tree { get; init; }
        public required PersistentDeviceId Device { get; init; }
        public required DeviceConfigurationService Configuration { get; init; }
        public required MainViewModel Model { get; init; }
        public required MainWindow Window { get; init; }
        public required MainWindowInteractionService Interaction { get; init; }

        public static async Task<Fixture> CreateAsync(bool failOverrides = false)
        {
            UiDesignSystemService.Apply();
            string root = Path.Combine("/tmp/opencode", "keiterm-session-commit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var device = new PersistentDeviceId(Path.Combine(root, "device-id"));
            var db = new SqliteConnectionFactory($"Data Source={Path.Combine(root, "db")};Pooling=False", device);
            var tree = new SqliteTreeRepository(db);
            await tree.InitializeAsync();
            var identities = new SqliteIdentityRepository(db);
            var vault = VaultTestDb.CreateVault(db);
            var settings = new FixedSettingsService(new AppSettings { PreferSystemAgent = false, ConfirmBeforeClose = false });
            IDeviceConfigurationStore store = new SqliteDeviceConfigurationStore(db);
            var configuration = new DeviceConfigurationService(failOverrides ? new FailingWriteStore(store) : store, device);
            var model = new MainViewModel(tree, identities, vault, vault, settings, new OfflineFactory(), uiDispatch: a => a(),
                deviceConfigurationService: configuration, syncDeviceId: device);
            var window = new MainWindow { DataContext = model };
            var interaction = new MainWindowInteractionService(window, model, new IdentityManagerViewModel(identities, vault, vault),
                new SettingsViewModel(settings), null, NullLogger.Instance);
            model.Interaction = interaction;
            window.Show();
            HeadlessAvalonia.Pump();
            return new() { Root = root, Database = db, Tree = tree, Device = device, Configuration = configuration, Model = model, Window = window, Interaction = interaction };
        }

        public SessionEditViewModel Editor()
        {
            HeadlessAvalonia.Pump();
            return Assert.IsType<SessionEditViewModel>(Assert.Single(Window.OwnedWindows.OfType<SessionEditWindow>()).DataContext);
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeAsync();
            Window.Close();
            Directory.Delete(Root, true);
        }
    }

    // 仅故障注入持久化写边界，读取、owner 外键及成功路径全部使用真实 SQLite。
    private sealed class FailingWriteStore(IDeviceConfigurationStore inner) : IDeviceConfigurationStore
    {
        public Task<IReadOnlyList<DeviceConfiguration>> GetAsync(Guid ownerId, string category, CancellationToken ct = default) => inner.GetAsync(ownerId, category, ct);
        public Task SaveAsync(DeviceConfiguration configuration, string sourceDeviceId, CancellationToken ct = default) => Task.FromException(new IOException("设备覆盖写入失败"));
        public Task DeleteAsync(Guid ownerId, string category, DeviceConfigurationTarget target, string sourceDeviceId, CancellationToken ct = default) => Task.FromException(new IOException("设备覆盖写入失败"));
    }

    private sealed class OfflineFactory : ISshSessionFactory
    {
        public Task<ISshSession> CreateSessionAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods, SshConnectOptions? options = null, CancellationToken ct = default)
            => Task.FromException<ISshSession>(new IOException("Offline test connection"));
        public Task<IRemoteFileSystem> CreateFileSystemAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods, ISshSession? activeSession = null, SshConnectOptions? options = null, CancellationToken ct = default)
            => Task.FromException<IRemoteFileSystem>(new IOException("Offline test connection"));
    }
}
