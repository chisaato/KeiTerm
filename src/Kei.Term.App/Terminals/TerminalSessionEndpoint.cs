namespace Kei.Term.App.Terminals;

using System;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;
using Kei.Term.Core.Abstractions;

// 协议无关的 RoyalTerminal 端点桥：任意 ITerminalSession 的字节流 ⇄ TerminalControl
public sealed class TerminalSessionEndpoint : ITerminalEndpoint, IDisposable
{
    private readonly ITerminalSession _session;
    private readonly TerminalControl _terminal;

    public TerminalSessionEndpoint(ITerminalSession session, TerminalControl terminal)
    {
        _session = session;
        _terminal = terminal;

        _session.OutputReceived += OnSessionOutputReceived;
    }

    private void OnSessionOutputReceived(byte[] data)
    {
        // RoyalTerminal.Avalonia 的 TerminalControl.WriteOutput 内部安全调度到 UI 线程
        _terminal.WriteOutput(data);
    }

    public void SendText(ReadOnlySpan<byte> utf8)
    {
        _ = _session.SendInputAsync(utf8.ToArray());
    }

    public void SetSize(int widthPx, int heightPx)
    {
        int cols = _terminal.Columns;
        int rows = _terminal.Rows;
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
