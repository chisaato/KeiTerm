using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class TabTitleTemplateTests
{
    [Theory]
    [InlineData("{local_name}: {remote_hostname} | {remote_path}", "prod: server | /var/log")]
    [InlineData("{local_host}:{local_port} ({local_user}) {remote_ip}", "alias:2222 (ops) 203.0.113.5")]
    [InlineData("{{{remote_dirname}}} {remote_title}", "{log} vim {local_name}")]
    public void Render_ReplacesOnlyWhitelistedTokens_WithoutParsingRemoteText(string template, string expected)
    {
        TabTitleContext context = new("prod", "alias", "2222", "ops", "server", "/var/log", "203.0.113.5", "vim {local_name}");
        Assert.True(TabTitleTemplate.TryRender(template, context, out string title, out _));
        Assert.Equal(expected, title);
    }

    [Theory]
    [InlineData("{hostname}")]
    [InlineData("{remote_unknown}")]
    [InlineData("{remote_path")]
    [InlineData("remote_path}")]
    public void Render_RejectsUnknownOrMalformedVariables(string template)
    {
        Assert.False(TabTitleTemplate.TryRender(template, new("prod"), out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Render_HandlesMissingValuesRootPathControlCharactersAndLengthLimits()
    {
        Assert.True(TabTitleTemplate.TryRender("{remote_ip} | {remote_dirname}", new("prod", RemotePath: "/"), out string title, out _));
        Assert.Equal("— | /", title);
        Assert.True(TabTitleTemplate.TryRender("{remote_title}", new("prod", RemoteTitle: "a\nb\u001bc"), out title, out _));
        Assert.Equal("abc", title);
        Assert.False(TabTitleTemplate.TryRender(new string('x', 513), new("prod"), out _, out _));
        Assert.True(TabTitleTemplate.TryRender("{remote_path}", new("prod", RemotePath: new string('x', 8192)), out title, out _));
        Assert.Equal(2048, title.Length);
    }
}
