using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;

namespace Kei.Term.Transport.Go;

// Lazy 确保纯 .NET 默认路径不初始化 Go 工厂或进程监管器。
public sealed class CompositeSshSessionFactory : ISshSessionFactory
{
    private readonly ISshSessionFactory _builtIn;
    private readonly Lazy<ISshSessionFactory> _go;
    private readonly Func<TransportEngine> _defaultEngine;
    public CompositeSshSessionFactory(ISshSessionFactory builtIn, Func<ISshSessionFactory> go,
        Func<TransportEngine>? defaultEngine = null)
    { _builtIn = builtIn; _go = new(go); _defaultEngine = defaultEngine ?? (() => TransportEngine.BuiltIn); }
    private ISshSessionFactory Resolve(ResolvedSessionConfig config) => (config.TransportEngineOverride ?? _defaultEngine()) switch
    {
        TransportEngine.BuiltIn => _builtIn,
        TransportEngine.GoCore => _go.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(config), "未知传输引擎")
    };
    public Task<ISshSession> CreateSessionAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
        SshConnectOptions? options = null, CancellationToken ct = default) => Resolve(config).CreateSessionAsync(config, methods, options, ct);
    public Task<IRemoteFileSystem> CreateFileSystemAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null, SshConnectOptions? options = null, CancellationToken ct = default)
        => Resolve(config).CreateFileSystemAsync(config, methods, activeSession, options, ct);
    public Task<IRemoteTransferChannel> CreateTransferChannelAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null, SshConnectOptions? options = null, CancellationToken ct = default)
        => Resolve(config).CreateTransferChannelAsync(config, methods, activeSession, options, ct);
}
