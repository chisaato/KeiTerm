namespace Kei.Term.Tests;

using System;
using System.Collections.Generic;
using Kei.Term.App.Helpers;
using Xunit;

public class TypeAheadNavigatorTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }

    private sealed class TestItem(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void SingleCharacter_FindsFirstMatchingItem()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(timeProvider: time);
        List<TestItem> items = [new("alpha"), new("beta"), new("branch"), new("charlie")];

        int match = navigator.ProcessInput("b", items, -1, x => x.Name);

        Assert.Equal(1, match); // "beta"
        Assert.Equal("b", navigator.CurrentBuffer);
    }

    [Fact]
    public void MultiCharacterSequence_FindsLongerPrefix()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(timeProvider: time);
        List<TestItem> items = [new("server"), new("scratch"), new("system")];

        int first = navigator.ProcessInput("s", items, -1, x => x.Name);
        Assert.Equal(0, first); // "server"

        time.Advance(TimeSpan.FromMilliseconds(200));
        int second = navigator.ProcessInput("c", items, first, x => x.Name);
        Assert.Equal(1, second); // "scratch"
        Assert.Equal("sc", navigator.CurrentBuffer);

        time.Advance(TimeSpan.FromMilliseconds(200));
        int third = navigator.ProcessInput("r", items, second, x => x.Name);
        Assert.Equal(1, third); // "scratch"
        Assert.Equal("scr", navigator.CurrentBuffer);
    }

    [Fact]
    public void Timeout_ResetsPrefixBuffer()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(TimeSpan.FromMilliseconds(800), timeProvider: time);
        List<TestItem> items = [new("scratch"), new("system"), new("app")];

        int first = navigator.ProcessInput("s", items, -1, x => x.Name);
        Assert.Equal(0, first);

        // 超时 800ms
        time.Advance(TimeSpan.FromMilliseconds(850));

        int second = navigator.ProcessInput("a", items, first, x => x.Name);
        Assert.Equal(2, second); // "app"
        Assert.Equal("a", navigator.CurrentBuffer);
    }

    [Fact]
    public void RepeatedSameCharacter_CyclesThroughMatches()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(timeProvider: time);
        List<TestItem> items = [new("banana"), new("box"), new("branch"), new("busy")];

        int first = navigator.ProcessInput("b", items, -1, x => x.Name);
        Assert.Equal(0, first); // "banana"

        time.Advance(TimeSpan.FromMilliseconds(200));
        int second = navigator.ProcessInput("b", items, first, x => x.Name);
        Assert.Equal(1, second); // "box"

        time.Advance(TimeSpan.FromMilliseconds(200));
        int third = navigator.ProcessInput("b", items, second, x => x.Name);
        Assert.Equal(2, third); // "branch"

        time.Advance(TimeSpan.FromMilliseconds(200));
        int fourth = navigator.ProcessInput("b", items, third, x => x.Name);
        Assert.Equal(3, fourth); // "busy"

        time.Advance(TimeSpan.FromMilliseconds(200));
        int looped = navigator.ProcessInput("b", items, fourth, x => x.Name);
        Assert.Equal(0, looped); // 循环回到 "banana"
    }

    [Fact]
    public void Backspace_DeletesLastCharacterAndRefreshesMatch()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(timeProvider: time);
        List<TestItem> items = [new("cat"), new("car"), new("cool")];

        navigator.ProcessInput("c", items, -1, x => x.Name);
        time.Advance(TimeSpan.FromMilliseconds(100));
        navigator.ProcessInput("a", items, 0, x => x.Name);
        time.Advance(TimeSpan.FromMilliseconds(100));
        int matchCar = navigator.ProcessInput("r", items, 0, x => x.Name);
        Assert.Equal(1, matchCar); // "car"
        Assert.Equal("car", navigator.CurrentBuffer);

        int backMatch = navigator.HandleBackspace(items, matchCar, x => x.Name);
        Assert.Equal("ca", navigator.CurrentBuffer);
        Assert.Equal(1, backMatch); // 当前选中仍是 "car"，满足 "ca" 前缀
    }

    [Fact]
    public void SkipsParentDirectorySpecialEntry_UnlessExplicitDot()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(timeProvider: time);
        List<TestItem> items = [new(".."), new("data"), new("downloads")];

        int dMatch = navigator.ProcessInput("d", items, -1, x => x.Name);
        Assert.Equal(1, dMatch); // "data"，跳过 ".."

        navigator.Reset();
        int dotMatch = navigator.ProcessInput(".", items, -1, x => x.Name);
        Assert.Equal(0, dotMatch); // ".."，显式以点开头
    }

    [Fact]
    public void CaseInsensitive_MatchesBothUpperAndLower()
    {
        TestTimeProvider time = new();
        TypeAheadNavigator navigator = new(timeProvider: time);
        List<TestItem> items = [new("Document"), new("Desktop")];

        int matchLower = navigator.ProcessInput("d", items, -1, x => x.Name);
        Assert.Equal(0, matchLower);

        time.Advance(TimeSpan.FromMilliseconds(200));
        int matchUpper = navigator.ProcessInput("E", items, matchLower, x => x.Name);
        Assert.Equal(1, matchUpper); // "Desktop" 匹配 "de"
    }
}
