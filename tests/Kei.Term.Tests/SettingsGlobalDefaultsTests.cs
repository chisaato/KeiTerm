using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

public class SettingsGlobalDefaultsTests
{
    [Theory]
    [InlineData(true, CwdFollowMode.OnceOnOpen)]
    [InlineData(false, CwdFollowMode.Always)]
    public Task GlobalBehaviorControls_SaveAndReload_AndResolveSessionOverrides(bool followsRemote, CwdFollowMode followMode)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_global_defaults_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            await service.SaveSettingsAsync(new AppSettings { TabTitleFollowsRemote = !followsRemote });
            SettingsViewModel model = new(service, directory);
            window = new SettingsWindow(model);
            window.Show();

            // 从实际标签栏控件修改全局标题策略，防止只有 VM 字段而设置页缺入口。
            model.SelectedCategory = model.Categories.Single(c => c.Page is TabBarSettingsProxyPage);
            window.UpdateLayout();
            CheckBox title = Find<CheckBox>(window, "TabTitleFollowsRemoteCheckBox");
            Assert.True(title.IsVisible);
            Assert.True(title.Bounds.Width > 0);
            title.IsChecked = followsRemote;

            model.SelectedCategory = model.Categories.Single(c => c.Page is FileTransferSettingsPage);
            window.UpdateLayout();
            FileTransferSettingsPage page = (FileTransferSettingsPage)model.SelectedCategory.Page;
            ComboBox cwd = Find<ComboBox>(window, "DirectoryFollowModeComboBox");
            Assert.True(cwd.IsVisible);
            cwd.SelectedItem = page.Terminal.CwdFollowModes.Single(option => option.Mode == followMode);
            Assert.NotEqual(followsRemote, service.Current.TabTitleFollowsRemote);
            Assert.True(await model.ApplyChangesAsync());

            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            SettingsViewModel reopened = new(reader, directory);
            TabBarSettingsProxyPage tabs = reopened.Categories.Select(c => c.Page).OfType<TabBarSettingsProxyPage>().Single();
            FileTransferSettingsPage files = reopened.Categories.Select(c => c.Page).OfType<FileTransferSettingsPage>().Single();
            Assert.Equal(followsRemote, tabs.Terminal.TabTitleFollowsRemote);
            Assert.Equal(followMode, files.Terminal.SelectedCwdFollow?.Mode);

            SessionNode inherited = new() { Host = "host" };
            ResolvedSessionConfig resolved = SessionConfigBuilder.Build(inherited, reader.Current);
            Assert.Equal(followsRemote, resolved.FollowRemoteTitle);
            Assert.Equal(followMode, resolved.CwdFollow);
            SessionEditViewModel sessionEditor = new(inherited, null, [], reader.Current);
            Assert.Null(sessionEditor.SelectedTitleFollow?.Value);
            Assert.Null(sessionEditor.SelectedCwdFollow?.Value);
            inherited.Overrides = new SessionOverrides { FollowRemoteTitle = !followsRemote, CwdFollow = CwdFollowMode.Off };
            resolved = SessionConfigBuilder.Build(inherited, reader.Current);
            Assert.Equal(!followsRemote, resolved.FollowRemoteTitle);
            Assert.Equal(CwdFollowMode.Off, resolved.CwdFollow);
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task FileTransferControls_SavePreviouslyHiddenPreferences_AndReloadThem() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_file_preferences_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            SettingsViewModel model = new(service, directory);
            window = new SettingsWindow(model);
            window.Show();
            model.SelectedCategory = model.Categories.Single(c => c.Page is FileTransferSettingsPage);
            window.UpdateLayout();
            foreach (Expander advanced in window.GetVisualDescendants().OfType<Expander>().ToArray()) advanced.IsExpanded = true;
            window.UpdateLayout();
            NumericUpDown polling = Find<NumericUpDown>(window, "PollingIntervalSecondsInput");
            NumericUpDown debounce = Find<NumericUpDown>(window, "WriteDebounceMillisecondsInput");
            Button editPath = Find<Button>(window, "CustomEditorPathEditButton");
            CheckBox left = Find<CheckBox>(window, "FileManagerDockLeftCheckBox");
            polling.Value = 7;
            debounce.Value = 1200;
            Assert.Same(((FileTransferSettingsPage)model.SelectedCategory.Page).EditCustomEditorPathCommand, editPath.Command);
            editPath.Command!.Execute(null);
            HeadlessAvalonia.Pump();
            TextPromptWindow dialog = Assert.IsType<TextPromptWindow>(Assert.Single(window.OwnedWindows));
            TextBox editor = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            editor.Text = "  code  ";
            Assert.True(editor.Focus());
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            await ((FileTransferSettingsPage)model.SelectedCategory.Page).EditCustomEditorPathCommand.ExecutionTask!;
            left.IsChecked = true;
            Assert.True(await model.ApplyChangesAsync());

            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            SettingsViewModel reopened = new(reader, directory);
            FileTransferSettingsPage reloaded = reopened.Categories.Select(c => c.Page).OfType<FileTransferSettingsPage>().Single();
            Assert.Equal(7, reloaded.PollingIntervalSeconds);
            Assert.Equal(1200, reloaded.WriteDebounceMilliseconds);
            Assert.Equal("code", reloaded.CustomEditorPath);
            Assert.True(reloaded.IsFileManagerOnLeft);
            Assert.Equal(7, reader.Current.FileTransfer.PollingIntervalSeconds);
            Assert.Equal(1200, reader.Current.FileTransfer.WriteDebounceMilliseconds);
            Assert.Equal("code", reader.Current.FileTransfer.CustomEditorPath);
            Assert.True(reader.Current.FileTransfer.IsFileManagerOnLeft);
            System.Diagnostics.ProcessStartInfo launch = FileEditorLauncher.CreateEditorStartInfo("/tmp/example.txt", reader.Current.FileTransfer.CustomEditorPath);
            Assert.Equal("code", launch.FileName);
            Assert.Contains("--wait", launch.Arguments);
            Assert.Contains("/tmp/example.txt", launch.Arguments);
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FallbackEditorDialog_CancelKeepsDraft_AndEmptyConfirmRestoresSystemAssociation(bool cancel)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_fallback_editor_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            await service.SaveSettingsAsync(new AppSettings { FileTransfer = new FileTransferSettings { CustomEditorPath = "original-editor" } });
            SettingsViewModel model = new(service, directory);
            window = new SettingsWindow(model);
            window.Show();
            FileTransferSettingsPage page = model.Categories.Select(category => category.Page).OfType<FileTransferSettingsPage>().Single();
            Task editing = page.EditCustomEditorPathCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            TextPromptWindow dialog = Assert.IsType<TextPromptWindow>(Assert.Single(window.OwnedWindows));
            TextBox input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            input.Text = cancel ? "changed-but-cancelled" : string.Empty;
            Assert.True(input.Focus());
            dialog.KeyPress(cancel ? Key.Escape : Key.Enter, RawInputModifiers.None,
                cancel ? PhysicalKey.Escape : PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            await editing;
            Assert.Equal(cancel ? "original-editor" : string.Empty, page.CustomEditorPath);
            Assert.Equal("original-editor", service.Current.FileTransfer.CustomEditorPath);
            Assert.True(await model.ApplyChangesAsync());
            Assert.Equal(cancel ? "original-editor" : string.Empty, service.Current.FileTransfer.CustomEditorPath);
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });

    [Theory]
    [InlineData(-10, -100, 1, 200)]
    [InlineData(7200, 120000, 3600, 60000)]
    public async Task FileTransferPreferences_SaveClampsValuesToSupportedRanges(int polling, int debounce, int expectedPolling, int expectedDebounce)
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_file_ranges_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            SettingsViewModel model = new(service, directory);
            FileTransferSettingsPage page = model.Categories.Select(c => c.Page).OfType<FileTransferSettingsPage>().Single();
            page.PollingIntervalSeconds = polling;
            page.WriteDebounceMilliseconds = debounce;
            Assert.True(await model.ApplyChangesAsync());
            Assert.Equal(expectedPolling, service.Current.FileTransfer.PollingIntervalSeconds);
            Assert.Equal(expectedDebounce, service.Current.FileTransfer.WriteDebounceMilliseconds);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Find<T>(SettingsWindow window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
