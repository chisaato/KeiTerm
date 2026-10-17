namespace Kei.Term.Core.Transfers;

// 连接上的非交互 exec。校验与归档都经它执行，不占用终端视图。
public interface IRemoteShell
{
    Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default);
}
