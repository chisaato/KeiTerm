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
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

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
            model.SelectedCategory = model.Categories.Single(category => category.Page is TabBarSettingsProxyPage);
            window = new(model);
            window.Show();
            HeadlessAvalonia.Pump();
            TextBox input = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "TabTitleTemplateInput");
            input.Text = "{remote_hostname} | {remote_path} ({remote_ip})";
            HeadlessAvalonia.Pump();
            Assert.True(input.IsEffectivelyVisible);
            Expander reference = window.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "TabTitleVariableReference");
            Assert.True(reference.IsExpanded);
            SelectableTextBlock variable = Assert.Single(reference.GetVisualDescendants().OfType<SelectableTextBlock>(), control => control.Text == "{remote_path}");
            Assert.True(variable.IsEffectivelyVisible);
            reference.IsExpanded = false;
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
