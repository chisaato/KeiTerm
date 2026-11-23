using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public sealed record CwdFollowOption(CwdFollowMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record TerminalMultiplexerOption(TerminalMultiplexerMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record ConnectionProtocolOption(string Protocol, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record SerialFlowControlOption(SerialFlowControl Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public enum SerialScope
{
    Generic = 0,
    OperatingSystem = 1,
    Device = 2
}

public sealed record SerialScopeOption(SerialScope Scope, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record SerialPinOption(bool? Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

// 会话级「可继承」选项：Value 为 null 表示继承全局设置
public sealed record InheritableOption<T>(T? Value, string DisplayName) where T : struct
{
    public override string ToString() => DisplayName;
}

// 会话行为相关下拉项的唯一来源：设置页、会话编辑、批量修改共用，保证文案一致
public static class SessionBehaviorOptions
{
    private const string MultiplexerOffKey = "Behavior.Multiplexer.Off";
    private const string MultiplexerAutoKey = "Behavior.Multiplexer.Auto";
    private const string MultiplexerTmuxKey = "Behavior.Multiplexer.Tmux";
    private const string MultiplexerScreenKey = "Behavior.Multiplexer.Screen";

    public static IReadOnlyList<CwdFollowOption> CwdFollowModes { get; } =
    [
        new(CwdFollowMode.Off, Strings.Get("Behavior.CwdFollow.Off")),
        new(CwdFollowMode.OnceOnOpen, Strings.Get("Behavior.CwdFollow.OnceOnOpen")),
        new(CwdFollowMode.Always, Strings.Get("Behavior.CwdFollow.Always"))
    ];

    public static IReadOnlyList<TerminalMultiplexerOption> MultiplexerModes { get; } =
    [
        new(TerminalMultiplexerMode.Off, Strings.Get(MultiplexerOffKey)),
        new(TerminalMultiplexerMode.Auto, Strings.Get(MultiplexerAutoKey)),
        new(TerminalMultiplexerMode.Tmux, Strings.Get(MultiplexerTmuxKey)),
        new(TerminalMultiplexerMode.Screen, Strings.Get(MultiplexerScreenKey))
    ];

    public static string Describe(CwdFollowMode mode)
        => CwdFollowModes.FirstOrDefault(o => o.Mode == mode)?.DisplayName ?? mode.ToString();

    public static string Describe(TerminalMultiplexerMode mode)
        => MultiplexerModes.FirstOrDefault(option => option.Mode == mode)?.DisplayName ?? mode.ToString();

    public static string Describe(bool followRemoteTitle)
        => Strings.Get(followRemoteTitle ? "Behavior.TabTitle.Remote" : "Behavior.TabTitle.SessionName");

    public static string Describe(TransportEngine engine)
        => engine switch
        {
            TransportEngine.BuiltIn => Strings.Get("Settings.Transport.Engine.BuiltIn"),
            TransportEngine.GoCore => $"{Strings.Get("Settings.Transport.Engine.GoCore")}（{Strings.Get("Settings.Transport.Engine.ExperimentalTag")}）",
            _ => engine.ToString()
        };

    // 「继承（当前全局：xxx）」+ 各显式值
    public static IReadOnlyList<InheritableOption<TransportEngine>> InheritableTransportEngine(TransportEngine globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", Describe(globalDefault))),
            new(TransportEngine.BuiltIn, Describe(TransportEngine.BuiltIn)),
            new(TransportEngine.GoCore, Describe(TransportEngine.GoCore))
        ];

    // 「继承（当前全局：xxx）」+ 各显式值
    public static IReadOnlyList<InheritableOption<CwdFollowMode>> InheritableCwdFollow(CwdFollowMode globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", Describe(globalDefault))),
            .. CwdFollowModes.Select(o => new InheritableOption<CwdFollowMode>(o.Mode, o.DisplayName))
        ];

    public static IReadOnlyList<InheritableOption<TerminalMultiplexerMode>> InheritableMultiplexerMode(TerminalMultiplexerMode globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", Describe(globalDefault))),
            .. MultiplexerModes.Select(option => new InheritableOption<TerminalMultiplexerMode>(option.Mode, option.DisplayName))
        ];

    public static IReadOnlyList<InheritableOption<bool>> InheritableTitleFollow(bool globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", Describe(globalDefault))),
            new(true, Describe(true)),
            new(false, Describe(false))
        ];

    // 连接超时（秒）选项：继承全局、30秒、60秒、90秒、120秒、180秒、300秒
    public static IReadOnlyList<InheritableOption<int>> InheritableConnectTimeout(int globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", $"{globalDefault}s")),
            new(15, "15s"),
            new(30, "30s"),
            new(60, "60s"),
            new(90, "90s"),
            new(120, "120s"),
            new(180, "180s"),
            new(300, "300s")
        ];

    // 会话支持的连接协议
    public static IReadOnlyList<ConnectionProtocolOption> ConnectionProtocols { get; } =
    [
        new(SessionProtocols.Ssh, Strings.Get("SessionEdit.Protocol.Ssh")),
        new(SessionProtocols.Telnet, Strings.Get("SessionEdit.Protocol.Telnet")),
        new(SessionProtocols.Serial, Strings.Get("SessionEdit.Protocol.Serial"))
    ];

    // 串口参数常用选项
    public static IReadOnlyList<int> SerialBaudRates { get; } =
        [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public static IReadOnlyList<int> SerialDataBits { get; } = [5, 6, 7, 8];

    public static IReadOnlyList<Kei.Term.Core.Models.SerialParity> SerialParities { get; } =
        [Kei.Term.Core.Models.SerialParity.None, Kei.Term.Core.Models.SerialParity.Odd, Kei.Term.Core.Models.SerialParity.Even, Kei.Term.Core.Models.SerialParity.Mark, Kei.Term.Core.Models.SerialParity.Space];

    public static IReadOnlyList<Kei.Term.Core.Models.SerialStopBits> SerialStopBitsOptions { get; } =
        [Kei.Term.Core.Models.SerialStopBits.One, Kei.Term.Core.Models.SerialStopBits.Two, Kei.Term.Core.Models.SerialStopBits.OnePointFive];

    public static IReadOnlyList<SerialFlowControlOption> SerialFlowControls { get; } =
    [
        new(Kei.Term.Core.Models.SerialFlowControl.None, Strings.Get("SessionEdit.Serial.FlowControl.None")),
        new(Kei.Term.Core.Models.SerialFlowControl.RtsCts, Strings.Get("SessionEdit.Serial.FlowControl.RtsCts")),
        new(Kei.Term.Core.Models.SerialFlowControl.XonXoff, Strings.Get("SessionEdit.Serial.FlowControl.XonXoff"))
    ];

    public static IReadOnlyList<SerialScopeOption> SerialScopes { get; } =
    [
        new(SerialScope.Generic, Strings.Get("SessionEdit.Serial.Scope.Generic")),
        new(SerialScope.OperatingSystem, string.Format(Strings.Get("SessionEdit.Serial.Scope.Os"), OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")),
        new(SerialScope.Device, Strings.Get("SessionEdit.Serial.Scope.Device"))
    ];

    public static IReadOnlyList<SerialPinOption> SerialPinOptions { get; } =
    [
        new(null, Strings.Get("SessionEdit.Serial.Pin.DriverDefault")),
        new(true, Strings.Get("SessionEdit.Serial.Pin.Enabled")),
        new(false, Strings.Get("SessionEdit.Serial.Pin.Disabled"))
    ];
}
