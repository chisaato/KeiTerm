using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels.Settings;

// 传输引擎下拉选项模型
public sealed record TransportEngineOption(TransportEngine Engine, string DisplayName, string Description, string Tag)
{
    public override string ToString() => DisplayName;
}

// 引擎特性清单项模型
public sealed record TransportFeatureItem(string FeatureName, string BuiltInSupport, string GoCoreSupport, string Note);

// 「传输引擎」独立设置页 VM：负责通用底层传输核心管理与通道参数配置
public partial class TransportSettingsPage : ViewModelBase
{
    public IReadOnlyList<TransportEngineOption> EngineOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEngine))]
    [NotifyPropertyChangedFor(nameof(IsGoCoreSelected))]
    [NotifyPropertyChangedFor(nameof(CurrentEngineDescription))]
    private TransportEngineOption? _selectedEngineOption;

    // 当前选中的引擎枚举
    public TransportEngine SelectedEngine
    {
        get => SelectedEngineOption?.Engine ?? TransportEngine.BuiltIn;
        set => SelectedEngineOption = EngineOptions.FirstOrDefault(o => o.Engine == value) ?? EngineOptions[0];
    }

    public bool IsGoCoreSelected => SelectedEngine == TransportEngine.GoCore;

    public string CurrentEngineDescription => SelectedEngine == TransportEngine.GoCore
        ? Strings.Get("Settings.Transport.Engine.GoCoreDesc")
        : Strings.Get("Settings.Transport.Engine.BuiltInDesc");

    // 通用传输参数：连接超时（秒）与保活心跳（秒）
    [ObservableProperty]
    private int _connectTimeoutSeconds = 60;

    [ObservableProperty]
    private int _keepAliveIntervalSeconds = 30;

    // 特性概览对比数据源
    public IReadOnlyList<TransportFeatureItem> Features { get; }

    public TransportSettingsPage()
    {
        EngineOptions =
        [
            new(
                TransportEngine.BuiltIn,
                Strings.Get("Settings.Transport.Engine.BuiltIn"),
                Strings.Get("Settings.Transport.Engine.BuiltInDesc"),
                string.Empty),
            new(
                TransportEngine.GoCore,
                Strings.Get("Settings.Transport.Engine.GoCore"),
                Strings.Get("Settings.Transport.Engine.GoCoreDesc"),
                "Beta")
        ];

        SelectedEngineOption = EngineOptions[0];

        Features =
        [
            new(
                Strings.Get("Settings.Transport.Feature.SshSftp"),
                Strings.Get("Settings.Transport.Status.Stable"),
                Strings.Get("Settings.Transport.Status.Supported"),
                "SSH.NET / Go SSH+SFTP"),
            new(
                Strings.Get("Settings.Transport.Feature.ConcurrentChunk"),
                Strings.Get("Settings.Transport.Status.Standard"),
                Strings.Get("Settings.Transport.Status.Supported"),
                "Go 核心多路并发切片加速"),
            new(
                Strings.Get("Settings.Transport.Feature.Fido2"),
                Strings.Get("Settings.Transport.Status.AgentBridge"),
                Strings.Get("Settings.Transport.Status.Direct"),
                "ed25519-sk / ecdsa-sk"),
            new(
                Strings.Get("Settings.Transport.Feature.ExtendedProtocols"),
                "—",
                Strings.Get("Settings.Transport.Status.Planned"),
                "Telnet、Serial 串口统一通道")
        ];
    }
}
