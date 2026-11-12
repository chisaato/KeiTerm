using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Ssh.Abstractions;

namespace Kei.Term.Transport.Go;

public sealed class GoSshSessionFactory : ISshSessionFactory
{
    private readonly Func<CancellationToken, Task<GoIpcClient>> _getClient;
    private readonly IHostKeyVerifier? _hostKeyVerifier;
    public GoSshSessionFactory(GoSidecarProcessManager manager, IHostKeyVerifier? hostKeyVerifier = null)
        : this(manager.GetClientAsync, hostKeyVerifier) { }

    internal GoSshSessionFactory(Func<CancellationToken, Task<GoIpcClient>> getClient,
        IHostKeyVerifier? hostKeyVerifier = null)
    { _getClient = getClient; _hostKeyVerifier = hostKeyVerifier; }

    public Task<ISshSession> CreateSessionAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
        SshConnectOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ISshSession>(new GoSshSession(config, token => ConnectAsync(config, methods, options, token)));
    }

    public async Task<IRemoteFileSystem> CreateFileSystemAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null, SshConnectOptions? options = null, CancellationToken ct = default)
    {
        if (config.FileTransferProtocol != FileTransferProtocol.Sftp) throw new NotSupportedException("Go Sidecar 当前仅支持 SFTP");
        var session = (GoSshSession)await CreateSessionAsync(config, methods, options, ct);
        return new GoRemoteFileSystem(session);
    }

    public Task<IRemoteTransferChannel> CreateTransferChannelAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null, SshConnectOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IRemoteTransferChannel>(new GoRemoteTransferChannel(async token =>
            (GoRemoteFileSystem)await CreateFileSystemAsync(config with { FileTransferProtocol = FileTransferProtocol.Sftp }, methods, null, options, token)));
    }

    private async Task<(GoIpcClient Client, string Id, GoIpcStream Stream)> ConnectAsync(ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods, SshConnectOptions? options, CancellationToken ct)
    {
        options ??= SshConnectOptions.Default;
        var verifier = options.HostKeyVerifier ?? _hostKeyVerifier
            ?? throw new NotSupportedException("Go Sidecar 需要主机密钥验证服务");
        if (options.Socks5Host != null || options.PortForwards.Count > 0)
            throw new NotSupportedException("当前 Go IPC 尚不支持代理或端口转发");

        var jumps = new List<object>();

        async Task<object> Endpoint(ResolvedSessionConfig target, IReadOnlyList<MaterializedAuthMethod> auth)
        {
            if (auth.Count == 0 || auth.Any(a => a.Kind is AuthMaterialKind.Agent or AuthMaterialKind.Interactive))
                throw new NotSupportedException("当前 Go IPC 仅支持显式密码和普通私钥认证");
            if (auth.GroupBy(a => a.Kind).Any(group => group.Count() > 1))
                throw new NotSupportedException("当前 Go IPC 不支持同类认证材料的有序多次尝试");
            foreach (var key in auth.Where(a => a.Kind == AuthMaterialKind.PrivateKey))
            {
                // 仅允许能被现有普通私钥解析器确认的类型；不把 SK 材料传给 Go 直接使用。
                if (Kei.Term.Ssh.Services.SshKeyFingerprint.Compute(key.Secret?.PrivateKeyContent ?? "", key.Secret?.Passphrase) == null)
                    throw new NotSupportedException("Go IPC 仅接受有效普通私钥；硬件 SK 密钥必须经 SshNet.Agent 桥接");
            }
            // 只探测密钥，不发送目标凭据；人工确认统一交给连接编排器在握手外处理。
            var probeClient = await _getClient(ct);
            var probedKey = await probeClient.CallAsync("session.probeHostKey", new {
                host = target.Host, port = target.Port, user = target.Username, jumpHosts = jumps.ToArray()
            }, ct);
            var presented = new PresentedHostKey(target.Host, target.Port, probedKey.GetProperty("keyType").GetString()!,
                Convert.FromBase64String(probedKey.GetProperty("publicKeyBase64").GetString()!));
            var outcome = await verifier.VerifyAsync(presented, ct);
            if (!outcome.Accepted) throw new HostKeyRejectedException(outcome);
            string fingerprint = presented.FingerprintSha256;
            if (string.IsNullOrWhiteSpace(fingerprint) || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
                throw new InvalidOperationException("缺少有效的已审批主机密钥指纹");
            return new { host = target.Host, port = target.Port, user = target.Username, hostKeySha256 = fingerprint,
                auth = new { password = auth.FirstOrDefault(a => a.Kind == AuthMaterialKind.Password)?.Secret?.Password,
                    privateKey = auth.FirstOrDefault(a => a.Kind == AuthMaterialKind.PrivateKey)?.Secret?.PrivateKeyContent,
                    passphrase = auth.FirstOrDefault(a => a.Kind == AuthMaterialKind.PrivateKey)?.Secret?.Passphrase } };
        }
        foreach (var hop in options.JumpHosts)
        {
            if (hop.Socks5Host != null) throw new NotSupportedException("Go IPC 尚不支持跳板代理");
            jumps.Add(await Endpoint(hop.Config, hop.Methods));
        }
        var endpoint = await Endpoint(config, methods);
        // 使用扁平 wire DTO，字段名严格匹配 Go Endpoint 内嵌结构。
        var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(System.Text.Json.JsonSerializer.Serialize(endpoint))!;
        fields["jumpHosts"] = jumps;
        fields["terminalType"] = config.TerminalType;
        fields["cols"] = 80;
        fields["rows"] = 24;
        fields["keepAliveSeconds"] = options.KeepAliveInterval > TimeSpan.Zero ? (int)options.KeepAliveInterval.TotalSeconds : 0;
        fields["env"] = config.EnvironmentVariables;
        fields["startupScript"] = config.StartupScript;
        var client = await _getClient(ct);
        var result = await client.CallAsync("session.connect", fields, ct);
        return (client, result.GetProperty("sessionId").GetString()!, client.GetStream(result.GetProperty("streamId").GetUInt32()));
    }
}
