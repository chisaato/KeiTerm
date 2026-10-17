namespace Kei.Term.Core.Transfers;

// 一次远端 exec 的结果。退出码为 null 表示进程被信号打断或通道没有给出状态。
public sealed record RemoteCommandResult(string Output, string Error, int? ExitStatus, string? ExitSignal = null);
