using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.Logging;

// 把 System.Diagnostics.Trace 输出桥接进文件日志：
// Avalonia 的 LogToTrace() 将框架内部诊断（绑定错误、XAML、布局等）写入 Trace，
// 而 .NET 下无 TraceListener 时这些输出会被静默丢弃；挂上本监听器后随滚动文件落盘。
public sealed class TraceToLogBridge(ILogger logger) : TraceListener
{
    // Trace.Write 的分片：缓存到行缓冲，WriteLine 时整行落盘
    private readonly System.Text.StringBuilder _lineBuffer = new();

    public override void Write(string? message)
    {
        if (message != null)
        {
            _lineBuffer.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        _lineBuffer.Append(message);
        var line = _lineBuffer.ToString();
        _lineBuffer.Clear();

        if (!string.IsNullOrWhiteSpace(line))
        {
            // Info 级即可：Avalonia 默认 LogToTrace 只透传 Warning 以上（含绑定错误）
            logger.LogInformation("[Avalonia] {TraceLine}", line);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // 尽力把未换行的残余缓冲落盘
        var residual = _lineBuffer.ToString();
        if (!string.IsNullOrWhiteSpace(residual))
        {
            logger.LogInformation("[Avalonia] {TraceLine}", residual);
            _lineBuffer.Clear();
        }

        base.Dispose(disposing);
    }
}
