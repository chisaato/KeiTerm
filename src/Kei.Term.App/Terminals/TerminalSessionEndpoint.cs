namespace Kei.Term.App.Terminals;

using System;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;
using Kei.Term.Core.Abstractions;

// 协议无关的 RoyalTerminal 端点桥：任意 ITerminalSession 的字节流 ⇄ TerminalControl
public sealed class TerminalSessionEndpoint : ITerminalEndpoint, IDisposable
{
    private readonly ITerminalSession _session;
    private readonly Action<byte[]> _writeOutput;
    private readonly Func<(int Columns, int Rows)> _getSize;

    public TerminalSessionEndpoint(ITerminalSession session, TerminalControl terminal)
        : this(session, terminal.WriteOutput, () => (terminal.Columns, terminal.Rows))
    {
    }

    // 字节流桥与控件调度解耦；生产构造器仍使用 TerminalControl 的安全输出调度。
    internal TerminalSessionEndpoint(ITerminalSession session, Action<byte[]> writeOutput, Func<(int Columns, int Rows)> getSize)
    {
        _session = session;
        _writeOutput = writeOutput;
        _getSize = getSize;

        _session.OutputReceived += OnSessionOutputReceived;
    }

    private void OnSessionOutputReceived(byte[] data)
    {
        // RoyalTerminal.Avalonia 的 TerminalControl.WriteOutput 内部安全调度到 UI 线程
        _writeOutput(data);
    }

    public void SendText(ReadOnlySpan<byte> utf8)
    {
        _ = _session.SendInputAsync(utf8.ToArray());
    }

    public void SetSize(int widthPx, int heightPx)
    {
        if (!_session.Capabilities.CanResize) return;
        (int cols, int rows) = _getSize();
        _ = _session.ResizeTerminalAsync(cols, rows, widthPx, heightPx);
    }

    public void SetFocus(bool focused)
    {
    }

    public void Dispose()
    {
        _session.OutputReceived -= OnSessionOutputReceived;
    }
}
