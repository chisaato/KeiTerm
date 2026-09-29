using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.Tests;

// 标签标题：会话名 / 远端标题（OSC 0/2）/ 重命名 之间的优先级
public class TerminalTabTitleTests
{
    private static ResolvedSessionConfig Config(bool followRemoteTitle) => new(
        Guid.NewGuid(), "prod-db", "10.0.0.5", 22, "ops", null, "xterm-256color", null, null,
        new Dictionary<string, string>(), FollowRemoteTitle: followRemoteTitle);

    private static TerminalTabViewModel Tab(bool follow)
    {
        var tab = new TerminalTabViewModel("prod-db", "monospace", 14);
        tab.BindConfig(Config(follow));
        return tab;
    }

    [Fact]
    public void Following_ShowsRemoteTitle_AndFallsBackWhenRemoteClearsIt()
    {
        var tab = Tab(follow: true);

        tab.ApplyRemoteTitle("root@gzz-server-gz (192.168.5.25) - byobu");
        Assert.Equal("root@gzz-server-gz (192.168.5.25) - byobu", tab.Title);

        tab.ApplyRemoteTitle("");
        Assert.Equal("prod-db", tab.Title);
    }

    [Fact]
    public void NotFollowing_KeepsSessionName_ButRemembersRemoteTitleForToggle()
    {
        var tab = Tab(follow: false);

        tab.ApplyRemoteTitle("vim README.md");
        Assert.Equal("prod-db", tab.Title);

        tab.ToggleFollowRemoteTitleCommand.Execute(null);
        Assert.Equal("vim README.md", tab.Title);
    }

    [Fact]
    public void Rename_WinsOverRemoteTitle_AndSurvivesReconnect()
    {
        var tab = Tab(follow: true);
        tab.ApplyRemoteTitle("remote");

        tab.Rename("  主库  ");
        Assert.Equal("主库", tab.Title);

        // 重连会重新绑定配置（其中 FollowRemoteTitle = true），不应把用户的重命名冲掉
        tab.BindConfig(Config(followRemoteTitle: true));
        tab.ApplyRemoteTitle("remote again");
        Assert.Equal("主库", tab.Title);
    }

    [Fact]
    public void ToolTip_ShowsFullTitle_SessionAndTarget()
    {
        var tab = Tab(follow: true);
        tab.ApplyRemoteTitle("some very long remote title");

        Assert.Contains("some very long remote title", tab.ToolTip);
        Assert.Contains("prod-db", tab.ToolTip);
        Assert.Contains("ops@10.0.0.5:22", tab.ToolTip);
    }

    [Theory]
    [InlineData("a\u0007b\u001bc", "abc")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("  title  ", "title")]
    public void SanitizeRemoteTitle_StripsControlCharsAndBlank(string? raw, string? expected)
    {
        Assert.Equal(expected, TerminalTabViewModel.SanitizeRemoteTitle(raw));
    }

    [Fact]
    public void SanitizeRemoteTitle_CapsLength()
    {
        string? title = TerminalTabViewModel.SanitizeRemoteTitle(new string('x', 5000));
        Assert.NotNull(title);
        Assert.True(title!.Length <= 256);
    }

    [Fact]
    public async Task Disconnect_KeepsTabReusable()
    {
        var tab = new TerminalTabViewModel("t", "monospace", 14);

        await tab.DisconnectAsync();

        // 断开只卸会话，标签仍可原地重连（旧实现会整体释放标签）
        Assert.False(tab.IsDisposed);
        Assert.Equal(ConnectionState.Disconnected, tab.State);
    }
}

// 真实 TerminalControl：远端经 OSC 设置的标题到达标签，后台标签的响铃点亮活动标记
public class TerminalTabTitleHeadlessTests
{
    private static (Window Window, TerminalTabViewModel Tab) Host(bool follow)
    {
        var tab = new TerminalTabViewModel("session", "DejaVu Sans Mono", 14);
        tab.BindConfig(new ResolvedSessionConfig(
            Guid.NewGuid(), "session", "h", 22, "u", null, "xterm-256color", null, null,
            new Dictionary<string, string>(), FollowRemoteTitle: follow));
        var window = new Window { Width = 600, Height = 300, Content = tab.Terminal };
        window.Show();
        HeadlessAvalonia.Pump();
        return (window, tab);
    }

    [Fact]
    public Task Osc2FromRemote_BecomesTabTitle() => HeadlessAvalonia.RunAsync(() =>
    {
        var (window, tab) = Host(follow: true);

        tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\u001b]2;root@gzz - byobu\u0007"));
        HeadlessAvalonia.Pump();

        Assert.Equal("root@gzz - byobu", tab.Title);
        window.Close();
    });

    [Fact]
    public Task BellInBackgroundTab_MarksActivity_UntilSelected() => HeadlessAvalonia.RunAsync(() =>
    {
        var (window, tab) = Host(follow: true);
        tab.IsSelected = false;

        tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\u0007"));
        HeadlessAvalonia.Pump();
        Assert.True(tab.HasActivity);

        tab.IsSelected = true;
        Assert.False(tab.HasActivity);
        window.Close();
    });

    [Fact]
    public Task TerminalQuery_IsAnsweredBackToRemote() => HeadlessAvalonia.RunAsync(() =>
    {
        var (window, tab) = Host(follow: true);
        var session = new RecordingSession();
        tab.AttachSession(session);

        // 远端询问光标位置（DSR 6）：终端必须回 ESC [ 行 ; 列 R，否则依赖应答的程序会卡住等待
        tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\u001b[6n"));
        HeadlessAvalonia.Pump();

        Assert.Matches(@"^\u001b\[\d+;\d+R$", session.SentText);
        window.Close();
    });

    private sealed class RecordingSession : ITerminalSession
    {
        private readonly StringBuilder _sent = new();

        public string SentText
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToString();
                }
            }
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
#pragma warning disable CS0067
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            lock (_sent)
            {
                _sent.Append(Encoding.UTF8.GetString(data.Span));
            }

            return Task.CompletedTask;
        }

        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
