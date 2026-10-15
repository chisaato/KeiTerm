using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class TerminalDirectoryTrackerTests
{
    [Theory]
    [InlineData("host:/var/www", "/var/www")]
    [InlineData("ops@host: /var/有 空格:目录;test", "/var/有 空格:目录;test")]
    [InlineData("[2001:db8::1]:/srv", "/srv")]
    public void BuiltInTitles_PreserveAbsolutePaths(string title, string expected)
    {
        TerminalDirectoryTracker tracker = new();
        Assert.True(tracker.AcceptTitle(title, TerminalMultiplexerMode.Auto));
        Assert.Equal(expected, tracker.Directory);
    }

    [Theory]
    [InlineData(TerminalMultiplexerMode.Auto, "kei;tmux;host;bash;/srv;release", TerminalMultiplexerMode.Tmux, "/srv;release")]
    [InlineData(TerminalMultiplexerMode.Tmux, "kei;tmux;host;zsh;/srv", TerminalMultiplexerMode.Tmux, "/srv")]
    [InlineData(TerminalMultiplexerMode.Auto, "kei;screen;host;/etc", TerminalMultiplexerMode.Screen, "/etc")]
    [InlineData(TerminalMultiplexerMode.Screen, "kei;screen;host;/etc", TerminalMultiplexerMode.Screen, "/etc")]
    public void MultiplexerTitles_AutoDetectOrSelectBackend(TerminalMultiplexerMode mode, string title,
        TerminalMultiplexerMode detected, string expected)
    {
        TerminalDirectoryTracker tracker = new();
        Assert.True(tracker.AcceptTitle(title, mode));
        Assert.Equal(detected, tracker.DetectedMultiplexer);
        Assert.Equal(expected, tracker.Directory);
    }

    [Theory]
    [InlineData(TerminalMultiplexerMode.Off, "host:/srv")]
    [InlineData(TerminalMultiplexerMode.Tmux, "kei;screen;host;/srv")]
    [InlineData(TerminalMultiplexerMode.Screen, "kei;tmux;host;bash;/srv")]
    [InlineData(TerminalMultiplexerMode.Auto, "host: ~/srv")]
    [InlineData(TerminalMultiplexerMode.Auto, "vim /srv")]
    [InlineData(TerminalMultiplexerMode.Auto, "host:/srv\u001b[0m")]
    [InlineData(TerminalMultiplexerMode.Auto, "kei;tmux;host;bash;relative")]
    public void DisabledWrongBackendAndMalformedTitles_DoNotNavigate(TerminalMultiplexerMode mode, string title)
    {
        TerminalDirectoryTracker tracker = new();
        tracker.AcceptOsc7("/original", "host");
        Assert.False(tracker.AcceptTitle(title, mode));
        Assert.Equal("/original", tracker.Directory);
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("mosh")]
    [InlineData("mosh-client")]
    [InlineData("telnet")]
    public void NestedRemoteCommand_InvalidatesOuterPaneDirectory(string command)
    {
        TerminalDirectoryTracker tracker = new();
        tracker.AcceptOsc7("/previous", "host");
        Assert.True(tracker.AcceptTitle($"kei;tmux;host;{command};/wrong", TerminalMultiplexerMode.Auto));
        Assert.Null(tracker.Directory);
        Assert.True(tracker.AcceptTitle("kei;tmux;host;bash;/back", TerminalMultiplexerMode.Auto));
        Assert.Equal("/back", tracker.Directory);
    }

    [Fact]
    public void HostChange_PausesUntilOriginalHostReturns()
    {
        TerminalDirectoryTracker tracker = new();
        Assert.True(tracker.AcceptOsc7("/home/ops", "login.example.com"));
        Assert.True(tracker.AcceptTitle("login:/etc", TerminalMultiplexerMode.Tmux));
        Assert.Equal("/etc", tracker.Directory);
        Assert.True(tracker.AcceptOsc7("/etc", "other.example.com"));
        Assert.Null(tracker.Directory);
        Assert.Equal("other.example.com", tracker.RejectedHost);
        Assert.True(tracker.AcceptOsc7("/srv", "LOGIN.EXAMPLE.COM."));
        Assert.Equal("/srv", tracker.Directory);
        Assert.Null(tracker.RejectedHost);
    }

    [Theory]
    [InlineData("relative", "host")]
    [InlineData("/bad\npath", "host")]
    [InlineData("/valid", "host: /bogus")]
    [InlineData("/valid", "")]
    public void InvalidSignals_CannotReplaceKnownDirectory(string path, string host)
    {
        TerminalDirectoryTracker tracker = new();
        tracker.AcceptOsc7("/known", "host");
        Assert.False(tracker.AcceptOsc7(path, host));
        Assert.Equal("/known", tracker.Directory);
    }
}
