using Kei.Term.Core.Services;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Settings;
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

public class TabTitleTemplateSettingsTests
{
    [Fact]
    public Task SettingsControl_PreviewsValidatesPersistsAndResolvesTemplate() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_title_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            SettingsViewModel model = new(service, directory);
            window = new(model);
            window.Show();
            model.SelectedCategory = model.Categories.Single(category => category.Page is TabBarSettingsProxyPage);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            TextBox input = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "TabTitleTemplateInput");
            input.Text = "{remote_hostname} | {remote_path} ({remote_ip})";
            HeadlessAvalonia.Pump();
            Assert.True(input.IsEffectivelyVisible);
            TabBarSettingsProxyPage tabBarProxy = (TabBarSettingsProxyPage)model.SelectedCategory.Page;
            tabBarProxy.DrillDownVariablesCommand.Execute(null);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            SelectableTextBlock variable = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(), control => control.Text == "{remote_path}");
            Assert.True(variable.IsEffectivelyVisible);
            tabBarProxy.NavigateBackCommand.Execute(null);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            Assert.Equal("server | /home/ops (203.0.113.10)", window.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "TabTitlePreview").Text);
            Assert.True(await model.ApplyChangesAsync());

            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            Assert.Equal(input.Text, reader.Current.TabTitleTemplate);
            Assert.Equal(input.Text, SessionConfigBuilder.Build(new SessionNode(), reader.Current).TabTitleTemplate);
            SettingsViewModel reopened = new(reader, directory);
            Assert.Equal(input.Text, reopened.Categories.Select(category => category.Page).OfType<TabBarSettingsProxyPage>().Single().Terminal.TabTitleTemplate);

            input.Text = "{remote_typo}";
            HeadlessAvalonia.Pump();
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "TabTitleTemplateError").IsEffectivelyVisible);
            Assert.False(await model.ApplyChangesAsync());
            Assert.Equal(reader.Current.TabTitleTemplate, service.Current.TabTitleTemplate);
        }
        finally { window?.Close(); Directory.Delete(directory, true); }
    });
}
