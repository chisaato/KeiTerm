using System;
using System.Threading.Tasks;
using Kei.Term.Ssh.Services;
using Renci.SshNet;
using Xunit;

namespace Kei.Term.Tests;

// SshNetSession 释放路径回归测试：
// 关闭标签时曾因二次释放对已 Dispose 的 SshClient 访问 IsConnected 抛 ObjectDisposedException
public class SshNetSessionDisposeTests
{
    // 构造但不连接，不触发任何网络 I/O
    private static SshNetSession CreateUnconnectedSession()
    {
        return new SshNetSession(
            Guid.NewGuid(),
            new ConnectionInfo("127.0.0.1", 22, "user", new PasswordAuthenticationMethod("user", "pass")),
            "xterm-256color");
    }

    [Fact]
    public async Task DisposeAsync_SequentialTwice_DoesNotThrow()
    {
        var session = CreateUnconnectedSession();

        await session.DisposeAsync();
        var exception = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());

        Assert.Null(exception);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCalls_DoesNotThrow()
    {
        var session = CreateUnconnectedSession();

        var exception = await Record.ExceptionAsync(
            () => Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask()));

        Assert.Null(exception);
    }
}
