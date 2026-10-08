using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using RoyalTerminal.Avalonia.Rendering;

namespace Kei.Term.Tests;

public class TerminalReconnectInteractionTests
{
    private static (Window Window, TerminalTabViewModel Tab) CreateHost()
    {
        TerminalTabViewModel tab = new("test", "DejaVu Sans Mono", 14);
        tab.BindConfig(new ResolvedSessionConfig(Guid.NewGuid(), "test", "localhost", 22, "user", null,
            "xterm-256color", null, null, new Dictionary<string, string>()));
        Window window = new() { Width = 800, Height = 800, Content = new ScrollViewer { Content = tab.Terminal } };
        return (window, tab);
    }

    [Fact]
    public Task InitialError_BeforeFirstLayout_StaysAtFirstRowAfterResize() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (window, tab) = CreateHost();
        try
        {
            tab.ReportError("No route to host");
            window.Show();
            HeadlessAvalonia.Pump();
            Assert.Contains("No route to host", ReadRow(tab, 0));
            Assert.Contains("Enter", ReadRow(tab, 1));
            Assert.Equal(0, tab.Terminal.Padding.Top);

            window.Height = 1000;
            HeadlessAvalonia.Pump();
            Assert.Contains("No route to host", ReadRow(tab, 0));
            Assert.Contains("Enter", ReadRow(tab, 1));
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    [Theory]
    [InlineData(ConnectionState.Error)]
    [InlineData(ConnectionState.Disconnected)]
    public Task Enter_OnInactiveTerminal_ReconnectsSameTabOnce(ConnectionState state) => HeadlessAvalonia.RunAsync(async () =>
    {
        var (window, tab) = CreateHost();
        int requests = 0;
        tab.ActionRequested += (sender, action) =>
        {
            Assert.Same(tab, sender);
            Assert.Equal(TabAction.Reconnect, action);
            requests++;
            tab.State = ConnectionState.Connecting;
        };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            tab.State = state;
            tab.Terminal.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.Equal(1, requests);
            Assert.Equal(ConnectionState.Connecting, tab.State);
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    [Fact]
    public Task Enter_OnConnectedTerminal_IsSentToRemoteShell() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (window, tab) = CreateHost();
        RecordingSession session = new();
        int reconnects = 0;
        tab.ActionRequested += (_, _) => reconnects++;
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            tab.AttachSession(session);
            tab.MarkConnected();
            tab.Terminal.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.Contains("\r", session.Input);
            Assert.Equal(0, reconnects);
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    [Fact]
    public Task ErrorAfterRemoteOutput_PreservesSessionHistory() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (window, tab) = CreateHost();
        RecordingSession session = new();
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            tab.AttachSession(session);
            tab.MarkConnected();
            session.Emit("KEEP-REMOTE-HISTORY\r\n");
            HeadlessAvalonia.Pump();
            await tab.DetachSessionAsync();
            tab.ReportError("Connection lost");
            HeadlessAvalonia.Pump();
            string viewport = string.Join("\n", Enumerable.Range(0, tab.Terminal.Rows).Select(row => ReadRow(tab, row)));
            Assert.Contains("KEEP-REMOTE-HISTORY", viewport);
            Assert.Contains("Connection lost", viewport);
            Assert.Contains("Enter", viewport);
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    private static string ReadRow(TerminalTabViewModel tab, int index)
    {
        TerminalScreen screen = tab.Terminal.Screen!;
        StringBuilder text = new();
        lock (screen.SyncRoot)
        {
            foreach (TerminalCell cell in screen.GetViewportRow(index).ReadOnlyCells)
            {
                if (!string.IsNullOrEmpty(cell.Grapheme)) text.Append(cell.Grapheme);
                else if (cell.Codepoint > 0) text.Append(char.ConvertFromUtf32(cell.Codepoint));
            }
        }
        return text.ToString();
    }

    private sealed class RecordingSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public List<string> Input { get; } = [];
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public void Emit(string text) => OutputReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Input.Add(Encoding.UTF8.GetString(data.Span));
            return Task.CompletedTask;
        }
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
