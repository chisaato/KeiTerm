using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class TerminalDirectoryTrackerTests
{
    [Fact]
    public void BuiltInTitles_PreserveAbsolutePaths()
    {
        (string Title, string Expected)[] cases =
        [
            ("host:/var/www", "/var/www"),
            ("ops@host: /var/有 空格:目录;test", "/var/有 空格:目录;test"),
            ("[2001:db8::1]:/srv", "/srv"),
        ];
        foreach ((string title, string expected) in cases)
        {
            TerminalDirectoryTracker tracker = new();
            Assert.True(tracker.AcceptTitle(title, TerminalMultiplexerMode.Auto));
            Assert.Equal(expected, tracker.Directory);
        }
    }

    [Fact]
    public void MultiplexerTitles_AutoDetectOrSelectBackend()
    {
        (TerminalMultiplexerMode Mode, string Title, TerminalMultiplexerMode Detected, string Expected)[] cases =
        [
            (TerminalMultiplexerMode.Auto, "kei;tmux;host;bash;/srv;release", TerminalMultiplexerMode.Tmux, "/srv;release"),
            (TerminalMultiplexerMode.Tmux, "kei;tmux;host;zsh;/srv", TerminalMultiplexerMode.Tmux, "/srv"),
            (TerminalMultiplexerMode.Auto, "kei;screen;host;/etc", TerminalMultiplexerMode.Screen, "/etc"),
            (TerminalMultiplexerMode.Screen, "kei;screen;host;/etc", TerminalMultiplexerMode.Screen, "/etc"),
        ];
        foreach ((TerminalMultiplexerMode mode, string title, TerminalMultiplexerMode detected, string expected) in cases)
        {
            TerminalDirectoryTracker tracker = new();
            Assert.True(tracker.AcceptTitle(title, mode));
            Assert.Equal(detected, tracker.DetectedMultiplexer);
            Assert.Equal(expected, tracker.Directory);
        }
    }

    [Fact]
    public void DisabledWrongBackendAndMalformedTitles_DoNotNavigate()
    {
        (TerminalMultiplexerMode Mode, string Title)[] cases =
        [
            (TerminalMultiplexerMode.Off, "host:/srv"),
            (TerminalMultiplexerMode.Tmux, "kei;screen;host;/srv"),
            (TerminalMultiplexerMode.Screen, "kei;tmux;host;bash;/srv"),
            (TerminalMultiplexerMode.Auto, "host: ~/srv"),
            (TerminalMultiplexerMode.Auto, "vim /srv"),
            (TerminalMultiplexerMode.Auto, "host:/srv\u001b[0m"),
            (TerminalMultiplexerMode.Auto, "kei;tmux;host;bash;relative"),
        ];
        foreach ((TerminalMultiplexerMode mode, string title) in cases)
        {
            TerminalDirectoryTracker tracker = new();
            tracker.AcceptOsc7("/original", "host");
            Assert.False(tracker.AcceptTitle(title, mode));
            Assert.Equal("/original", tracker.Directory);
        }
    }

    [Fact]
    public void NestedRemoteCommand_InvalidatesOuterPaneDirectory()
    {
        foreach (string command in new[] { "ssh", "mosh", "mosh-client", "telnet" })
        {
            TerminalDirectoryTracker tracker = new();
            tracker.AcceptOsc7("/previous", "host");
            Assert.True(tracker.AcceptTitle($"kei;tmux;host;{command};/wrong", TerminalMultiplexerMode.Auto));
            Assert.Null(tracker.Directory);
            Assert.True(tracker.AcceptTitle("kei;tmux;host;bash;/back", TerminalMultiplexerMode.Auto));
            Assert.Equal("/back", tracker.Directory);
        }
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

    [Fact]
    public void InvalidSignals_CannotReplaceKnownDirectory()
    {
        (string Path, string Host)[] cases =
        [
            ("relative", "host"),
            ("/bad\npath", "host"),
            ("/valid", "host: /bogus"),
            ("/valid", ""),
        ];
        foreach ((string path, string host) in cases)
        {
            TerminalDirectoryTracker tracker = new();
            tracker.AcceptOsc7("/known", "host");
            Assert.False(tracker.AcceptOsc7(path, host));
            Assert.Equal("/known", tracker.Directory);
        }
    }
}
