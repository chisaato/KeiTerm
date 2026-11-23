using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Xunit;

namespace Kei.Term.Tests;

public class MultiProtocolUiTests
{
    [Theory]
    [InlineData("telnet", 22)]
    [InlineData("ssh", 23)]
    [InlineData("telnet", 2323)]
    public void ExistingNetworkPort_RoundTripsWithoutProtocolSuggestion(string protocol, int port)
    {
        var node = new SessionNode { Name = "saved", Host = "localhost", Protocol = protocol, Port = port };
        var vm = new SessionEditViewModel(node, null, []);
        Assert.Equal(port, vm.Port);
        Assert.Equal(port, vm.ApplyToModel(node).Port);
    }

    [Fact]
    public void FlowControlChange_RejectsInheritedManualRtsWithoutChangingAnyLayer()
    {
        var vm = new SessionEditViewModel(null, null, []);
        vm.SerialRtsEnable = true;
        vm.SelectedSerialScopeOption = vm.SerialScopes.First(s => s.Scope == SerialScope.Device);
        vm.CurrentScopeFlowControl = SerialFlowControl.RtsCts;
        Assert.Equal(SerialFlowControl.None, vm.CurrentScopeFlowControl);
        Assert.True(vm.IsLowerLevelManualRtsConflict);
        Assert.True(vm.SerialRtsEnable);
        Assert.Empty(vm.DeviceExplicitFields);
        Assert.Empty(vm.OsExplicitFields);
    }

    [Fact]
    public void DeviceScope_InheritsEveryOsField_AndViewingDoesNotRewriteGeneric()
    {
        var vm = new SessionEditViewModel(null, null, []);
        vm.SelectedConnectionProtocol = SessionProtocols.Serial;
        vm.SerialGenericPort = "/dev/generic";
        vm.SerialRtsEnable = true;
        vm.SelectedSerialScopeOption = vm.SerialScopes.First(s => s.Scope == SerialScope.OperatingSystem);
        vm.CurrentScopePortName = "/dev/os";
        vm.CurrentScopeBaudRate = 57600;
        vm.CurrentScopeDataBits = 7;
        vm.CurrentScopeParity = SerialParity.Even;
        vm.CurrentScopeStopBits = SerialStopBits.Two;
        vm.CurrentScopeFlowControl = SerialFlowControl.XonXoff;
        vm.CurrentScopeDtrEnable = true;
        vm.CurrentScopeRtsEnable = false;
        vm.SelectedSerialScopeOption = vm.SerialScopes.First(s => s.Scope == SerialScope.Device);
        Assert.Equal("/dev/os", vm.CurrentScopePortName);
        Assert.Equal(57600, vm.CurrentScopeBaudRate);
        Assert.Equal(7, vm.CurrentScopeDataBits);
        Assert.Equal(SerialParity.Even, vm.CurrentScopeParity);
        Assert.Equal(SerialStopBits.Two, vm.CurrentScopeStopBits);
        Assert.Equal(SerialFlowControl.XonXoff, vm.CurrentScopeFlowControl);
        Assert.True(vm.CurrentScopeDtrEnable);
        Assert.False(vm.CurrentScopeRtsEnable);
        vm.SetExplicitOverride(DeviceConfigurationTarget.ForDevice(Guid.NewGuid().ToString()), "flowControl", JsonSerializer.SerializeToElement("RtsCts"));
        var saved = vm.ApplyToModel();
        Assert.True(saved.Overrides.Serial!.RtsEnable);
        Assert.Equal(SerialFlowControl.None, saved.Overrides.Serial.FlowControl);
        vm.SelectedSerialScopeOption = vm.SerialScopes.First(s => s.Scope == SerialScope.Generic);
        Assert.True(vm.ApplyToModel().Overrides.Serial!.RtsEnable);
        Assert.Equal(57600, vm.OsExplicitFields["baudRate"].GetInt32());
    }
    private sealed class StubSyncDeviceId(string initialId) : ISyncDeviceId
    {
        public string CurrentId { get; set; } = initialId;
        public string? DeviceId => CurrentId;
        public string EnsureCreated() => CurrentId;
    }

    private sealed class StubTerminalSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected { get; set; } = true;
        public TerminalSessionCapabilities Capabilities { get; set; } = TerminalSessionCapabilities.None;
        public List<byte[]> ReceivedInputs { get; } = [];

        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ReceivedInputs.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void SessionEditViewModel_ProtocolSwitch_AdjustsDefaultPortsAndVisibility()
    {
        var vm = new SessionEditViewModel(
            existing: null,
            parentId: null,
            availableIdentities: [],
            globalSettings: new AppSettings());

        // 默认初始化为 SSH
        Assert.True(vm.IsSshProtocol);
        Assert.False(vm.IsTelnetProtocol);
        Assert.False(vm.IsSerialProtocol);
        Assert.True(vm.IsNetworkProtocol);
        Assert.Equal(22, vm.Port);
        Assert.Contains(vm.Categories, c => c.Page is "FileTransfer");
        Assert.Contains(vm.Categories, c => c.Page is "Ports");

        // 切换为 Telnet：端口自动调整为 23，隐藏文件传输与端口转发
        vm.SelectedConnectionProtocol = SessionProtocols.Telnet;
        Assert.True(vm.IsTelnetProtocol);
        Assert.False(vm.IsSshProtocol);
        Assert.False(vm.IsSerialProtocol);
        Assert.True(vm.IsNetworkProtocol);
        Assert.Equal(23, vm.Port);
        Assert.DoesNotContain(vm.Categories, c => c.Page is "FileTransfer");
        Assert.DoesNotContain(vm.Categories, c => c.Page is "Ports");

        // 切换为 Serial：隐藏网络主机/端口与文件/端口转发
        vm.SelectedConnectionProtocol = SessionProtocols.Serial;
        Assert.True(vm.IsSerialProtocol);
        Assert.False(vm.IsNetworkProtocol);
        Assert.DoesNotContain(vm.Categories, c => c.Page is "FileTransfer");
        Assert.DoesNotContain(vm.Categories, c => c.Page is "Ports");

        // Serial 没有网络默认端口：切回 SSH 不推测先前端口来源，保留值并恢复文件入口。
        vm.SelectedConnectionProtocol = SessionProtocols.Ssh;
        Assert.True(vm.IsSshProtocol);
        Assert.Equal(23, vm.Port);
        Assert.Contains(vm.Categories, c => c.Page is "FileTransfer");
        Assert.Contains(vm.Categories, c => c.Page is "Ports");
    }

    [Fact]
    public void SessionEditViewModel_SerialBindingPriority_ResolvesDeviceThenOsThenGeneric()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "keiterm-dev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var persistentDevice = new PersistentDeviceId(Path.Combine(tempDir, "device-id"));
            var vm = new SessionEditViewModel(
                existing: null,
                parentId: null,
                availableIdentities: [],
                globalSettings: new AppSettings(),
                jumpCandidates: null,
                proxies: null,
                sessionTree: null,
                portForwards: null,
                metadataRepo: null,
                deviceConfigService: null,
                syncDeviceId: persistentDevice);

            vm.SelectedConnectionProtocol = SessionProtocols.Serial;
            vm.SerialGenericPort = "/dev/ttyGeneric";
            Assert.Equal("/dev/ttyGeneric", vm.EffectiveSerialPortName);

            // 设置 OS 默认端口覆盖通用默认
            vm.SerialOsPort = "/dev/ttyOS";
            Assert.Equal("/dev/ttyOS", vm.EffectiveSerialPortName);

            // 设置当前设备专属覆盖，优先于 OS 默认
            vm.SerialDevicePort = "/dev/ttyDevice";
            Assert.Equal("/dev/ttyDevice", vm.EffectiveSerialPortName);

            // 重建设备身份：清除当前设备专属覆盖，回到 OS 默认
            Assert.True(vm.CanRegenerateDeviceId);
            vm.RegenerateDeviceIdCommand.Execute(null);
            Assert.Empty(vm.SerialDevicePort);
            Assert.Equal("/dev/ttyOS", vm.EffectiveSerialPortName);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SessionEditViewModel_SerialFlowControl_ConflictDisablesManualRts()
    {
        var vm = new SessionEditViewModel(
            existing: null,
            parentId: null,
            availableIdentities: [],
            globalSettings: new AppSettings());

        vm.SelectedConnectionProtocol = SessionProtocols.Serial;

        // 默认无流控：允许手动控制 RTS
        var noneOption = vm.SerialFlowControls.First(o => o.Mode == SerialFlowControl.None);
        vm.SelectedSerialFlowControl = noneOption;
        Assert.False(vm.IsSerialRtsConflict);
        Assert.True(vm.IsSerialRtsManualAllowed);

        // 切换为硬件流控 RTS/CTS：手动 RTS 冲突并禁用
        var rtsCtsOption = vm.SerialFlowControls.First(o => o.Mode == SerialFlowControl.RtsCts);
        vm.SelectedSerialFlowControl = rtsCtsOption;
        Assert.True(vm.IsSerialRtsConflict);
        Assert.False(vm.IsSerialRtsManualAllowed);
        Assert.False(string.IsNullOrWhiteSpace(vm.SerialRtsConflictWarning));
    }

    [Fact]
    public void SessionEditViewModel_ApplyToModel_SavesProtocolAndParameters()
    {
        var vm = new SessionEditViewModel(
            existing: null,
            parentId: null,
            availableIdentities: [],
            globalSettings: new AppSettings());

        // 1. Telnet 保存
        vm.Name = "MyTelnet";
        vm.Host = "192.168.1.1";
        vm.Port = 2323;
        vm.SelectedConnectionProtocol = SessionProtocols.Telnet;
        var telnetNode = vm.ApplyToModel();
        Assert.Equal(SessionProtocols.Telnet, telnetNode.Protocol);
        Assert.Equal("192.168.1.1", telnetNode.Host);
        Assert.Equal(2323, telnetNode.Port);

        // 2. Serial 保存
        vm.Name = "MySerial";
        vm.SelectedConnectionProtocol = SessionProtocols.Serial;
        vm.SerialGenericPort = "/dev/ttyUSB0";
        vm.SerialBaudRate = 115200;
        vm.SerialDataBits = 8;
        vm.SerialParity = SerialParity.None;
        vm.SerialStopBits = SerialStopBits.One;
        vm.SelectedSerialFlowControl = vm.SerialFlowControls.First(o => o.Mode == SerialFlowControl.None);

        var serialNode = vm.ApplyToModel();
        Assert.Equal(SessionProtocols.Serial, serialNode.Protocol);
        // 串口连接不将本机端点写入通用 SessionNode.Host
        Assert.Empty(serialNode.Host);
        Assert.Null(serialNode.Port);
        Assert.NotNull(serialNode.Overrides.Serial);
        Assert.Equal("/dev/ttyUSB0", serialNode.Overrides.Serial.PortName);
        Assert.Equal(115200, serialNode.Overrides.Serial.BaudRate);
        Assert.Equal(8, serialNode.Overrides.Serial.DataBits);
        Assert.Equal(SerialFlowControl.None, serialNode.Overrides.Serial.FlowControl);
    }

    [Fact]
    public async Task TerminalTab_CapabilitiesGating_HidesFileManagerAndBlocksUpload()
    {
        var tab = new TerminalTabViewModel("TestTab", "monospace", 14);

        // 1. 无文件能力会话（如 Telnet / Serial）
        var noFsSession = new StubTerminalSession
        {
            Capabilities = new TerminalSessionCapabilities(CanResize: true, HasFileSystem: false)
        };

        tab.AttachSession(noFsSession);
        tab.MarkConnected();

        Assert.False(tab.HasFileSystem);

        // 文件管理器入口被禁止打开
        tab.ToggleFileManager();
        Assert.False(tab.IsFileManagerVisible);

        // 拖放文件不触发上传，直接作为路径粘贴给会话
        string tempDir = Path.Combine(Path.GetTempPath(), "keiterm_ui_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string testFile = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(testFile, "test data");

            await tab.HandleTerminalFileDropAsync([testFile]);

            // 会话直接收到了路径文本，而不是上传命令或弹窗上传
            Assert.NotEmpty(noFsSession.ReceivedInputs);
            string pasted = Encoding.UTF8.GetString(noFsSession.ReceivedInputs[0]);
            Assert.Contains("sample.txt", pasted);
            Assert.False(tab.IsTerminalUploadActive);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        // 2. 具备文件能力会话（如 SSH）
        var fsSession = new StubTerminalSession
        {
            Capabilities = new TerminalSessionCapabilities(CanResize: true, HasFileSystem: true)
        };

        await tab.DetachSessionAsync();
        Assert.False(tab.HasFileSystem);

        tab.AttachSession(fsSession);
        tab.MarkConnected();
        Assert.True(tab.HasFileSystem);

        tab.ToggleFileManager();
        Assert.True(tab.IsFileManagerVisible);
    }

    [Fact]
    public async Task SessionEditViewModel_RealDeviceConfigService_SaveAndReloadBehavior()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keiterm-ui-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var id = new PersistentDeviceId(Path.Combine(dir, "device-id"));
            var db = new SqliteConnectionFactory($"Data Source={Path.Combine(dir, "db")};Pooling=False", id);
            var tree = new SqliteTreeRepository(db);
            await tree.InitializeAsync();
            var service = new DeviceConfigurationService(new SqliteDeviceConfigurationStore(db), id);

            var session = new SessionNode { Name = "test-serial", Protocol = SessionProtocols.Serial };
            await tree.SaveNodeAsync(session);

            // 1. 初次编辑：通过 UI 控件绑定的属性操作
            var vm = new SessionEditViewModel(
                existing: session,
                parentId: null,
                availableIdentities: [],
                globalSettings: new AppSettings(),
                jumpCandidates: null,
                proxies: null,
                sessionTree: null,
                portForwards: null,
                metadataRepo: null,
                deviceConfigService: service,
                syncDeviceId: id);

            // 在通用作用域下输入通用默认端口与通用波特率
            Assert.True(vm.IsGenericScopeSelected);
            vm.CurrentScopePortName = "/dev/ttyGeneric";
            vm.CurrentScopeBaudRate = 115200;

            // 切换作用域到操作系统默认层
            vm.SelectedSerialScopeOption = vm.SerialScopes.First(s => s.Scope == SerialScope.OperatingSystem);
            Assert.True(vm.IsOsScopeSelected);
            Assert.False(vm.IsBaudRateOverriddenInCurrentScope); // 尚未覆盖，继承通用
            Assert.Equal(115200, vm.CurrentScopeBaudRate);

            // 在 UI 控件上覆盖 OS 波特率为 57600，并输入 OS 默认端口与 DTR/RTS 状态
            vm.CurrentScopeBaudRate = 57600;
            vm.CurrentScopeDataBits = 7;
            vm.CurrentScopeParity = SerialParity.Odd;
            vm.CurrentScopeStopBits = SerialStopBits.Two;
            vm.CurrentScopeFlowControl = SerialFlowControl.XonXoff;
            Assert.True(vm.IsBaudRateOverriddenInCurrentScope); // 界面徽章状态更新为已覆盖
            vm.CurrentScopePortName = "/dev/ttyOS";
            Assert.True(vm.IsPortOverriddenInCurrentScope);
            vm.CurrentScopeDtrEnable = true;
            Assert.True(vm.IsDtrOverriddenInCurrentScope);
            vm.CurrentScopeRtsEnable = false;
            Assert.True(vm.IsRtsOverriddenInCurrentScope);

            // 切换作用域到本机设备专属层
            vm.SelectedSerialScopeOption = vm.SerialScopes.First(s => s.Scope == SerialScope.Device);
            Assert.True(vm.IsDeviceScopeSelected);
            // 本机设备只覆盖端口，不覆盖波特率与引脚
            vm.CurrentScopePortName = "/dev/ttyDevice";
            Assert.True(vm.IsPortOverriddenInCurrentScope);
            Assert.False(vm.IsBaudRateOverriddenInCurrentScope);
            Assert.False(vm.IsDtrOverriddenInCurrentScope);

            // 保存到模型并持久化
            session = vm.ApplyToModel(session);
            await tree.SaveNodeAsync(session);

            // 提交设备覆盖到真实的 SQLite
            await vm.CommitSerialOverridesAsync(session.Id);

            // 2. 模拟重新打开会话编辑窗口并异步加载显式字段
            var reopenedVm = new SessionEditViewModel(
                existing: session,
                parentId: null,
                availableIdentities: [],
                globalSettings: new AppSettings(),
                jumpCandidates: null,
                proxies: null,
                sessionTree: null,
                portForwards: null,
                metadataRepo: null,
                deviceConfigService: service,
                syncDeviceId: id);

            await reopenedVm.LoadSerialOverridesAsync(session.Id);

            reopenedVm.SelectedSerialScopeOption = reopenedVm.SerialScopes.First(s => s.Scope == SerialScope.Device);
            var resolvedFields = await service.ResolveAsync(session.Id, "serial", SessionEditViewModel.CurrentOperatingSystemName, SerialConfigurationFields.FromOptions(session.Overrides.Serial!));
            var runtime = SerialConfigurationFields.Apply(session.Overrides.Serial!, resolvedFields);
            Assert.Equal(runtime.PortName, reopenedVm.CurrentScopePortName);
            Assert.Equal(runtime.BaudRate, reopenedVm.CurrentScopeBaudRate);
            Assert.Equal(runtime.DataBits, reopenedVm.CurrentScopeDataBits);
            Assert.Equal(runtime.Parity, reopenedVm.CurrentScopeParity);
            Assert.Equal(runtime.StopBits, reopenedVm.CurrentScopeStopBits);
            Assert.Equal(runtime.FlowControl, reopenedVm.CurrentScopeFlowControl);
            Assert.Equal(runtime.DtrEnable, reopenedVm.CurrentScopeDtrEnable);
            Assert.Equal(runtime.RtsEnable, reopenedVm.CurrentScopeRtsEnable);

            // 验证显式字段在 UI 上完整还原
            Assert.Equal("/dev/ttyDevice", reopenedVm.EffectiveSerialPortName);
            Assert.Equal(57600, reopenedVm.EffectiveSerialBaudRate);

            // 切换到 OS 作用域查看
            reopenedVm.SelectedSerialScopeOption = reopenedVm.SerialScopes.First(s => s.Scope == SerialScope.OperatingSystem);
            Assert.True(reopenedVm.IsBaudRateOverriddenInCurrentScope);
            Assert.Equal(57600, reopenedVm.CurrentScopeBaudRate);
            Assert.Equal("/dev/ttyOS", reopenedVm.CurrentScopePortName);
            Assert.True(reopenedVm.IsDtrOverriddenInCurrentScope);
            Assert.True(reopenedVm.CurrentScopeDtrEnable);
            Assert.True(reopenedVm.IsRtsOverriddenInCurrentScope);
            Assert.False(reopenedVm.CurrentScopeRtsEnable);

            // 3. 用户在 UI 上点击波特率与 DTR 旁的“恢复继承”按钮
            reopenedVm.ResetBaudRateOverrideCommand.Execute(null);
            reopenedVm.ResetDtrOverrideCommand.Execute(null);
            Assert.False(reopenedVm.IsBaudRateOverriddenInCurrentScope); // 状态变回继承
            Assert.Equal(115200, reopenedVm.CurrentScopeBaudRate);       // 回退到通用层波特率
            Assert.False(reopenedVm.IsDtrOverriddenInCurrentScope);      // DTR 状态变回继承
            Assert.True(reopenedVm.IsPortOverriddenInCurrentScope);       // 同层的端口覆盖不受影响！
            Assert.True(reopenedVm.IsRtsOverriddenInCurrentScope);        // 同层的 RTS 覆盖不受影响！
            Assert.Equal("/dev/ttyOS", reopenedVm.CurrentScopePortName);
            Assert.False(reopenedVm.CurrentScopeRtsEnable);

            // 切换到设备作用域，清除设备端口覆盖
            reopenedVm.SelectedSerialScopeOption = reopenedVm.SerialScopes.First(s => s.Scope == SerialScope.Device);
            reopenedVm.ResetPortOverrideCommand.Execute(null);
            Assert.False(reopenedVm.IsPortOverriddenInCurrentScope);

            await reopenedVm.CommitSerialOverridesAsync(session.Id);

            // 4. 第三次重新打开会话：设备端口覆盖已删除，自动回退到 OS 默认端口；OS 波特率已恢复继承，RTS 依然保持覆盖
            var thirdVm = new SessionEditViewModel(
                existing: session,
                parentId: null,
                availableIdentities: [],
                globalSettings: new AppSettings(),
                jumpCandidates: null,
                proxies: null,
                sessionTree: null,
                portForwards: null,
                metadataRepo: null,
                deviceConfigService: service,
                syncDeviceId: id);

            await thirdVm.LoadSerialOverridesAsync(session.Id);
            Assert.Equal("/dev/ttyOS", thirdVm.EffectiveSerialPortName);
            Assert.Equal(115200, thirdVm.EffectiveSerialBaudRate);

            thirdVm.SelectedSerialScopeOption = thirdVm.SerialScopes.First(s => s.Scope == SerialScope.OperatingSystem);
            Assert.False(thirdVm.IsBaudRateOverriddenInCurrentScope);
            Assert.False(thirdVm.IsDtrOverriddenInCurrentScope);
            Assert.True(thirdVm.IsPortOverriddenInCurrentScope);
            Assert.True(thirdVm.IsRtsOverriddenInCurrentScope);
            Assert.Equal("/dev/ttyOS", thirdVm.CurrentScopePortName);
            Assert.False(thirdVm.CurrentScopeRtsEnable);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public Task InteractionService_PreCancelledBindingCreatesNoWindow() => HeadlessAvalonia.RunAsync(async () =>
    {
        var owner = new MainWindow(deferStartupLayout: true);
        owner.Show();
        try
        {
            var service = new MainWindowInteractionService(owner, null!, null!, null!, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.False(await Task.Run(() => service.PromptConfirmSerialBindingAsync("session", "COM1", new CancellationToken(true))));
            Assert.Empty(owner.OwnedWindows);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task InteractionService_PromptConfirmSerialBinding_BackgroundCancellationClosesRealDialog() => HeadlessAvalonia.RunAsync(async () =>
    {
        var owner = new MainWindow(deferStartupLayout: true);
        owner.Show();
        var service = new MainWindowInteractionService(owner, null!, null!, null!, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        try
        {
            var pending = Task.Run(() => service.PromptConfirmSerialBindingAsync("Session1", "COM1", cts.Token));
            for (int i = 0; i < 200 && !owner.OwnedWindows.Any(); i++) await Task.Delay(10);
            var dialog = Assert.Single(owner.OwnedWindows);
            Assert.True(dialog.IsVisible);
            await Task.Run(() => cts.Cancel());
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(dialog.IsVisible);
            Assert.Empty(owner.OwnedWindows);
        }
        finally { foreach (var child in owner.OwnedWindows.ToArray()) child.Close(); owner.Close(); }
    });

    [Fact]
    public void SessionEditViewModel_UnknownProtocol_PreservesOriginalProtocolOnSave()
    {
        // 节点为未知/不受支持协议（例如 "vnc"）
        var existing = new SessionNode
        {
            Name = "LegacyVncNode",
            Protocol = "vnc"
        };

        var vm = new SessionEditViewModel(
            existing: existing,
            parentId: null,
            availableIdentities: [],
            globalSettings: new AppSettings());

        // 验证原协议保留，未被自动降级为 SSH
        Assert.Equal("vnc", vm.SelectedConnectionProtocol);
        Assert.True(vm.IsUnsupportedProtocol);
        Assert.False(vm.IsSshProtocol);

        // 下拉列表中有明确的“不支持的连接类型”选项
        Assert.Contains(vm.AvailableConnectionProtocols, p => p.Protocol == "vnc");

        // 用户仅修改会话名称并保存
        vm.Name = "RenamedVncNode";
        var saved = vm.ApplyToModel(existing);

        // 验证协议原样保留为 "vnc"，没有被静默转为 SSH
        Assert.Equal("vnc", saved.Protocol);
        Assert.Equal("RenamedVncNode", saved.Name);
    }
}
