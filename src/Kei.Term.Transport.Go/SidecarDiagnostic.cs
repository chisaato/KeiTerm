namespace Kei.Term.Transport.Go;

// 只允许元数据进入应用日志；不传递原始输出、异常消息或 RPC 参数。
public sealed record SidecarDiagnostic(string Kind, string ConnectionId = "", int? ProcessId = null,
    string? BinaryPath = null, int? ExitCode = null, uint? StreamId = null,
    string? Reason = null, bool Expected = false, string? Endpoint = null);

internal static class SidecarDiagnostics
{
    internal static void Emit(Action<SidecarDiagnostic>? sink, SidecarDiagnostic entry)
    {
        // 日志目标失败不得改变传输生命周期。
        try { sink?.Invoke(entry); }
        catch { }
    }
}
