namespace Kei.Term.Tests;

using System.Reflection;
using Kei.Term.Core.Abstractions;
using Kei.Term.Ssh.Services;
using Renci.SshNet;

public sealed class SshNetSessionOutputBackpressureTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedOutputDoesNotBlockInputInvocationOrDisposal(bool replay)
    {
        await using var session = CreateSession();
        ITerminalBinaryChannel? channel = replay ? InstallChannel(session) : null;
        if (replay) Dispatch(session, [1, 2, 3]);
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        session.OutputReceived += _ =>
        {
            entered.Set();
            unblock.Wait();
        };
        Task output = StartWorker(() =>
        {
            if (channel != null) channel.CompleteAsync().AsTask().GetAwaiter().GetResult();
            else Dispatch(session, [1, 2, 3]);
        });
        Task? input = null;
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(Deadline), "输出回调未进入");
            // 不 await SendInputAsync 的结果：真实 UI 死锁发生在方法尚未返回 Task 时。
            input = StartWorker(() => { _ = session.SendInputAsync(new byte[] { 4 }); });
            disposal = StartWorker(() => session.DisposeAsync().AsTask().GetAwaiter().GetResult());
            await Task.WhenAll(input, disposal).WaitAsync(Deadline);
        }
        finally
        {
            // 即使旧代码超时失败，也必须释放背压并回收全部工作线程。
            unblock.Set();
            await Task.WhenAll(output, input ?? Task.CompletedTask, disposal ?? Task.CompletedTask).WaitAsync(Deadline);
            if (channel != null) await channel.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireDuringBlockedOutputReturnsCancelableTaskAndRestoresExclusiveGate()
    {
        await using var session = CreateSession();
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        session.OutputReceived += _ =>
        {
            entered.Set();
            unblock.Wait();
        };
        Task output = StartWorker(() => Dispatch(session, [1]));
        var invoked = new TaskCompletionSource<Task<ITerminalBinaryChannel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? caller = null;
        try
        {
            Assert.True(entered.Wait(Deadline));
            caller = StartWorker(() => invoked.SetResult(session.AcquireBinaryChannelAsync(cancellation.Token)));
            Task<ITerminalBinaryChannel> acquisition = await invoked.Task.WaitAsync(Deadline);
            Assert.False(acquisition.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.WaitAsync(Deadline));
        }
        finally
        {
            cancellation.Cancel();
            unblock.Set();
            await Task.WhenAll(output, caller ?? Task.CompletedTask).WaitAsync(Deadline);
        }
        // 未连接故应立即报 IOException，而非泄漏独占门导致永久等待。
        await Assert.ThrowsAsync<IOException>(() => session.AcquireBinaryChannelAsync().WaitAsync(Deadline));
    }

    [Fact]
    public async Task ReplayPreservesPartialChunkAndQueuedBytesBeforeConcurrentOutput()
    {
        await using var session = CreateSession();
        await using ITerminalBinaryChannel channel = InstallChannel(session);
        Dispatch(session, [1, 2, 3]);
        Dispatch(session, [4, 5]);
        var consumed = new byte[1];
        Assert.Equal(1, await channel.ReadAsync(consumed));
        Assert.Equal(1, consumed[0]);
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        using var dispatchStarted = new ManualResetEventSlim();
        var received = new List<byte>();
        session.OutputReceived += chunk =>
        {
            received.AddRange(chunk);
            if (chunk[0] == 2)
            {
                entered.Set();
                unblock.Wait();
            }
        };
        Task replay = StartWorker(() => channel.CompleteAsync().AsTask().GetAwaiter().GetResult());
        Task? next = null;
        try
        {
            Assert.True(entered.Wait(Deadline));
            using var cancellation = new CancellationTokenSource();
            Task<ITerminalBinaryChannel> acquisition = session.AcquireBinaryChannelAsync(cancellation.Token);
            Assert.False(acquisition.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.WaitAsync(Deadline));
            next = StartWorker(() =>
            {
                dispatchStarted.Set();
                Dispatch(session, [6, 7]);
            });
            Assert.True(dispatchStarted.Wait(Deadline));
        }
        finally
        {
            unblock.Set();
            await Task.WhenAll(replay, next ?? Task.CompletedTask).WaitAsync(Deadline);
        }
        Assert.Equal(new byte[] { 2, 3, 4, 5, 6, 7 }, received);
    }

    private static Task StartWorker(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static SshNetSession CreateSession() => new(Guid.NewGuid(),
        new ConnectionInfo("127.0.0.1", 22, "user", new PasswordAuthenticationMethod("user", "pass")), "xterm-256color");

    private static void Dispatch(SshNetSession session, byte[] bytes)
        => typeof(SshNetSession).GetMethod("DispatchOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [bytes]);

    // 仅绕过网络建立，实际路由、输入门禁、回放及关闭全部执行生产实现。
    private static ITerminalBinaryChannel InstallChannel(SshNetSession session)
    {
        var gate = (SemaphoreSlim)typeof(SshNetSession).GetField("_binaryGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Assert.True(gate.Wait(0));
        Type type = typeof(SshNetSession).GetNestedType("BinaryChannel", BindingFlags.NonPublic)!;
        var channel = (ITerminalBinaryChannel)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [session], null)!;
        typeof(SshNetSession).GetField("_binaryChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, channel);
        return channel;
    }
}
