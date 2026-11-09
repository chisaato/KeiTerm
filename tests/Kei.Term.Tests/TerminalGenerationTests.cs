using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Protocols;
using Xunit;

namespace Kei.Term.Tests;

public class TerminalGenerationTests
{
    [Fact]
    public Task NewAttachmentAfterManualDisconnectStillHandlesImmediateFailure() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("generation", "monospace", 14);
        try
        {
            tab.AttachSession(new GenerationSession());
            tab.MarkConnected();
            await tab.DisconnectAsync();
            var current = new GenerationSession();
            tab.AttachSession(current);
            current.End(new IOException("new connection failed"));
            HeadlessAvalonia.Pump();
            Assert.Equal(ConnectionState.Error, tab.State);
            Assert.Equal("new connection failed", tab.StatusMessage);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task QueuedOldOutputDoesNotMarkNewAttachmentActive() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("generation", "monospace", 14);
        var old = new GenerationSession();
        try
        {
            tab.AttachSession(old);
            old.Emit(new byte[] { 65 });
            tab.AttachSession(new GenerationSession());
            HeadlessAvalonia.Pump();
            Assert.False(tab.HasActivity);
            old.Emit(new byte[] { 66 });
            HeadlessAvalonia.Pump();
            Assert.False(tab.HasActivity);
        }
        finally { await tab.DisposeAsync(); await old.DisposeAsync(); }
    });

    [Theory]
    [InlineData("DTR")]
    [InlineData("RTS")]
    [InlineData("Break")]
    public Task OptionalControlFailureIsReportedWithoutFaultingCommandOrDisconnecting(string operation) => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("serial", "monospace", 14);
        var session = new ControlSession { FailControls = true };
        try
        {
            tab.AttachSession(session);
            tab.MarkConnected();
            Exception? error = await Record.ExceptionAsync(() => operation switch
            {
                "DTR" => tab.ToggleDtrCommand.ExecuteAsync(null),
                "RTS" => tab.ToggleRtsCommand.ExecuteAsync(null),
                _ => tab.SendBreakCommand.ExecuteAsync(null)
            });
            Assert.Null(error);
            Assert.Contains("ENOTTY", tab.StatusMessage);
            Assert.Equal(ConnectionState.Connected, tab.State);
            Assert.True(session.IsConnected);
            if (operation == "Break") Assert.False(session.BreakAsserted);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task PinStateIsUnknownUntilSuccessfulControlAndResetsOnDetach() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("serial", "monospace", 14);
        var session = new ControlSession();
        try
        {
            tab.AttachSession(session);
            Assert.Null((bool?)tab.IsDtrEnabled);
            Assert.Null((bool?)tab.IsRtsEnabled);
            await tab.ToggleDtrAsync();
            await tab.ToggleRtsAsync();
            Assert.True(tab.IsDtrEnabled);
            Assert.True(tab.IsRtsEnabled);
            Assert.True(session.Dtr);
            Assert.True(session.Rts);
            await tab.DetachSessionAsync();
            Assert.Null((bool?)tab.IsDtrEnabled);
            Assert.Null((bool?)tab.IsRtsEnabled);
            Assert.False(tab.IsHardwareFlowControl);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task ActualResolvedFlowControlDisablesManualRtsRegardlessOfBaseConfig() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("serial", "monospace", 14);
        tab.BindConfig(new ResolvedSessionConfig(Guid.NewGuid(), "serial", "", 0, "", null, "xterm", null, null,
            new System.Collections.Generic.Dictionary<string, string>(), Protocol: SessionProtocols.Serial,
            Serial: new SerialOptions { PortName = "/base", FlowControl = SerialFlowControl.None }));
        var session = new SerialTerminalSession(new SerialOptions { PortName = "/resolved", FlowControl = SerialFlowControl.RtsCts });
        try
        {
            tab.AttachSession(session);
            Assert.True(tab.IsHardwareFlowControl);
            Assert.False(tab.CanToggleRts);
            await tab.ToggleRtsAsync();
            Assert.Null((bool?)tab.IsRtsEnabled);
            await tab.DetachSessionAsync();
            Assert.False(tab.IsHardwareFlowControl);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task PendingOldPinControlCannotPublishStateToReplacement() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("serial", "monospace", 14);
        var old = new ControlSession { PendingDtr = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            tab.AttachSession(old);
            Task operation = tab.ToggleDtrAsync();
            tab.AttachSession(new ControlSession());
            old.PendingDtr.SetResult();
            await operation;
            Assert.Null((bool?)tab.IsDtrEnabled);
            Assert.Null(tab.StatusMessage);
        }
        finally { await tab.DisposeAsync(); await old.DisposeAsync(); }
    });

    [Fact]
    public Task BreakReleaseTargetsOriginalSessionAfterReplacement() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("serial", "monospace", 14);
        var old = new ControlSession();
        var current = new ControlSession();
        try
        {
            tab.AttachSession(old);
            Task operation = tab.SendBreakAsync();
            Assert.True(old.BreakAsserted);
            tab.AttachSession(current);
            await operation;
            Assert.False(old.BreakAsserted);
            Assert.Null(current.BreakAsserted);
        }
        finally { await tab.DisposeAsync(); await old.DisposeAsync(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task QueuedOldDisconnectCannotOverwriteReplacement(bool reuseSessionObject) => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("generation", "monospace", 14);
        var old = new GenerationSession();
        var current = reuseSessionObject ? old : new GenerationSession();
        int reconnects = 0;
        tab.AutoReconnectEnabled = () => true;
        tab.UnexpectedDisconnectAsync = (_, _, _) => { reconnects++; return Task.CompletedTask; };
        try
        {
            tab.AttachSession(old);
            tab.MarkConnected();
            old.End(new InvalidOperationException("old connection"));
            // 不推进 Dispatcher：替换会话后才执行旧世代排队的断开。
            await current.ConnectAsync();
            tab.AttachSession(current);
            tab.MarkConnected();
            HeadlessAvalonia.Pump();
            Assert.Equal(ConnectionState.Connected, tab.State);
            Assert.Null(tab.StatusMessage);
            Assert.Equal(0, reconnects);
            if (!reuseSessionObject) old.End(new InvalidOperationException("unsubscribed connection"));
            HeadlessAvalonia.Pump();
            Assert.Equal(ConnectionState.Connected, tab.State);
            Assert.Equal(0, reconnects);
        }
        finally { await tab.DisposeAsync(); await old.DisposeAsync(); }
    });

    [Fact]
    public Task CurrentEstablishedConnectionDisconnectsAndRequestsReconnectOnce() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("generation", "monospace", 14);
        var session = new GenerationSession();
        int reconnects = 0;
        tab.AutoReconnectEnabled = () => true;
        tab.UnexpectedDisconnectAsync = (_, _, _) => { reconnects++; return Task.CompletedTask; };
        try
        {
            tab.AttachSession(session);
            tab.MarkConnected();
            Assert.Equal(ConnectionState.Connected, tab.State);
            session.End(new InvalidOperationException("current connection"));
            session.End(new InvalidOperationException("duplicate event"));
            HeadlessAvalonia.Pump();
            Assert.Equal(ConnectionState.Error, tab.State);
            Assert.Equal("current connection", tab.StatusMessage);
            Assert.Equal(1, reconnects);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task DetachedGenerationCannotAffectReattachedSameSession() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("generation", "monospace", 14);
        var session = new GenerationSession();
        int reconnects = 0;
        tab.AutoReconnectEnabled = () => true;
        tab.UnexpectedDisconnectAsync = (_, _, _) => { reconnects++; return Task.CompletedTask; };
        try
        {
            tab.AttachSession(session);
            session.End(new InvalidOperationException("old attachment"));
            await tab.DetachSessionAsync();
            HeadlessAvalonia.Pump();
            Assert.Equal(0, reconnects);
            await session.ConnectAsync();
            tab.AttachSession(session);
            tab.MarkConnected();
            HeadlessAvalonia.Pump();
            Assert.Equal(ConnectionState.Connected, tab.State);
            Assert.Null(tab.StatusMessage);
            Assert.Equal(0, reconnects);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task CurrentDisconnectIsHandledOnceAndCannotBeMarkedConnectedAgain() => HeadlessAvalonia.RunAsync(async () =>
    {
        var tab = new TerminalTabViewModel("generation", "monospace", 14);
        var session = new GenerationSession();
        int reconnects = 0;
        tab.AutoReconnectEnabled = () => true;
        tab.UnexpectedDisconnectAsync = (_, _, _) => { reconnects++; return Task.CompletedTask; };
        try
        {
            tab.AttachSession(session);
            session.End(new InvalidOperationException("current connection"));
            tab.MarkConnected();
            Assert.NotEqual(ConnectionState.Connected, tab.State);
            session.End(new InvalidOperationException("duplicate event"));
            HeadlessAvalonia.Pump();
            Assert.Equal(ConnectionState.Error, tab.State);
            Assert.Equal("current connection", tab.StatusMessage);
            Assert.Equal(1, reconnects);
            tab.MarkConnected();
            Assert.Equal(ConnectionState.Error, tab.State);
        }
        finally { await tab.DisposeAsync(); }
    });

    private class GenerationSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected { get; private set; } = true;
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
        public void End(Exception? error) { IsConnected = false; Disconnected?.Invoke(error); }
        public void Emit(byte[] bytes) => OutputReceived?.Invoke(bytes);
        public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }

    private sealed class ControlSession : GenerationSession, ISerialControl
    {
        public bool FailControls { get; init; }
        public TaskCompletionSource? PendingDtr { get; init; }
        public bool? Dtr { get; private set; }
        public bool? Rts { get; private set; }
        public bool? BreakAsserted { get; private set; }
        public async Task SetDtrAsync(bool enabled, CancellationToken ct = default)
        {
            if (PendingDtr != null) await PendingDtr.Task;
            if (FailControls) throw new IOException("ENOTTY");
            Dtr = enabled;
        }
        public Task SetRtsAsync(bool enabled, CancellationToken ct = default)
        {
            if (FailControls) throw new IOException("ENOTTY");
            Rts = enabled;
            return Task.CompletedTask;
        }
        public Task SetBreakStateAsync(bool enabled, CancellationToken ct = default)
        {
            BreakAsserted = enabled;
            if (FailControls && enabled) throw new IOException("ENOTTY");
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken ct = default) { End(null); return Task.CompletedTask; }
    }
}
