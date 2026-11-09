using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Protocols;
using Kei.Term.Transport.Go;

namespace Kei.Term.App.Services.Connection;

// 只构造未连接的终端；订阅、连接与所有权转交由 orchestrator 负责。
public sealed class BuiltInProtocolSessionFactory : IProtocolSessionFactory, ISerialPortCatalog
{
    private const string SerialCategory = "serial";
    private readonly DeviceConfigurationService _devices;
    private readonly ISyncDeviceId _deviceId;
    private readonly Func<IInteractionService> _interaction;
    private readonly string _os;
    private readonly GoProtocolFactory? _go;

    public BuiltInProtocolSessionFactory(DeviceConfigurationService devices, ISyncDeviceId deviceId,
        Func<IInteractionService> interaction, string? operatingSystem = null, GoProtocolFactory? go = null)
    {
        _devices = devices;
        _deviceId = deviceId;
        _interaction = interaction;
        _go = go;
        _os = (operatingSystem ?? CurrentOperatingSystem()).ToLowerInvariant();
        DeviceConfigurationTarget.ForOperatingSystem(_os).Validate();
    }

    public bool Supports(string protocol, TransportEngine engine) => (engine == TransportEngine.BuiltIn || engine == TransportEngine.GoCore && _go is not null)
        && protocol is SessionProtocols.Telnet or SessionProtocols.Serial;

    public async Task<IReadOnlyList<string>> GetPortNamesAsync(TransportEngine engine, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return engine switch
        {
            TransportEngine.BuiltIn => await Task.Run(() => (IReadOnlyList<string>)System.IO.Ports.SerialPort.GetPortNames(), ct),
            TransportEngine.GoCore when _go is not null => await _go.ListSerialPortsAsync(ct),
            _ => throw new NotSupportedException($"不支持串口枚举传输引擎：{engine}")
        };
    }

    public async Task<ITerminalSession> CreateSessionAsync(ResolvedSessionConfig config, TransportEngine engine, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Supports(config.Protocol, engine)) throw new NotSupportedException($"不支持连接类型/传输引擎：{config.Protocol} / {engine}");
        if (config.Protocol == SessionProtocols.Telnet)
        {
            var telnet = new TelnetConnectionOptions
            {
                Host = config.Host, Port = config.Port, TerminalType = config.TerminalType,
                Timeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds)
            };
            return engine == TransportEngine.BuiltIn
                ? new TelnetTerminalSession(config.SessionId, telnet)
                : _go!.CreateTelnet(config.SessionId, telnet);
        }

        var baseline = config.Serial ?? throw new ArgumentException("串口会话缺少完整的通用参数，请先编辑串口配置。", nameof(config));
        string installation = _deviceId.EnsureCreated();
        var target = DeviceConfigurationTarget.ForDevice(installation);
        var resolvedFields = await _devices.ResolveAsync(config.SessionId, SerialCategory, _os, SerialConfigurationFields.FromOptions(baseline), ct).ConfigureAwait(false);
        // 先逐字段合并，再校验，允许设备/OS 覆盖修复缺失端口或被覆盖的无效基底值。
        var options = SerialConfigurationFields.Apply(baseline, resolvedFields);
        var records = await _devices.GetOverridesAsync(config.SessionId, SerialCategory, ct).ConfigureAwait(false);
        var current = records.SingleOrDefault(r => r.Target == target);
        EnsureSameInstallation(installation, ct);
        if (current?.Fields.ContainsKey("portName") != true)
        {
            bool confirmed = await _interaction().PromptConfirmSerialBindingAsync(config.SessionName, options.PortName, ct).WaitAsync(ct).ConfigureAwait(false);
            EnsureSameInstallation(installation, ct);
            if (!confirmed) throw new OperationCanceledException("已取消本机串口绑定。", ct);

            // 确认期间可能发生编辑；只补端口字段，不把完整解析结果固化为设备覆盖。
            records = await _devices.GetOverridesAsync(config.SessionId, SerialCategory, ct).ConfigureAwait(false);
            current = records.SingleOrDefault(r => r.Target == target);
            var fields = current?.Fields.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (fields.TryGetValue("portName", out var changedPort) && changedPort.GetString() != options.PortName)
                throw new InvalidOperationException("确认期间串口绑定已更改，请重新连接。");
            fields["portName"] = JsonSerializer.SerializeToElement(options.PortName);
            EnsureSameInstallation(installation, ct);
            await _devices.SaveOverrideAsync(config.SessionId, SerialCategory, target, fields, ct).ConfigureAwait(false);
        }
        EnsureSameInstallation(installation, ct);
        return engine == TransportEngine.BuiltIn
            ? new SerialTerminalSession(config.SessionId, options)
            : _go!.CreateSerial(config.SessionId, options);
    }

    private void EnsureSameInstallation(string installation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_deviceId.EnsureCreated() != installation) throw new OperationCanceledException("安装实例身份已更改，请重新确认串口绑定。", ct);
    }

    private static string CurrentOperatingSystem() => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : throw new PlatformNotSupportedException("当前系统尚未支持串口设备配置。");
}
