using Kei.Term.App.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Infrastructure.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class CommandPaletteTests
{
    [Fact]
    public Task RecorderWindow_UsesLetterKey_WhenOptionProducesASymbol() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        SettingsViewModel model = new(new FixedSettingsService());
        GeneralSettingsPage page = (GeneralSettingsPage)model.SelectedCategory.Page;
        SettingsWindow window = new(model);
        try
        {
            window.Show();
            window.UpdateLayout();
            Button recorder = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CommandPaletteShortcutRecorder");
            recorder.Command!.Execute(null);
            recorder.Focus();
            RawInputModifiers primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(OperatingSystem.IsMacOS() ? Key.A : Key.P, primary | RawInputModifiers.Alt, PhysicalKey.P, "π");
            Assert.Equal(Key.P, AppShortcuts.CommandPalette(page.CommandPaletteShortcut).Key);
            Assert.False(page.IsRecordingCommandPaletteShortcut);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SearchAndKeyboard_ChooseFilteredResult_AndDoNotExecuteEmptyResults() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        CommandPaletteViewModel vm = new([
            new("快速连接", "输入主机地址", "", PaletteAction.QuickConnect),
            new("LAN server", "局域网 / ops@192.168.1.42:22", "", PaletteAction.ConnectSession, Guid.NewGuid()),
            new("Other server", "云端 / ops@203.0.113.10:22", "", PaletteAction.ConnectSession, Guid.NewGuid())]);
        MainWindow window = new();
        try
        {
            window.Show();
            Task<CommandPaletteItem?> completion = window.ShowCommandPaletteAsync(vm);
            HeadlessAvalonia.Pump();
            CommandPaletteView view = (CommandPaletteView)window.FindControl<ContentControl>("CommandPaletteHost")!.Content!;
            TextBox query = view.FindControl<TextBox>("QueryBox")!;
            query.Text = "server";
            Assert.Equal(2, vm.Results.Count);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal("Other server", vm.SelectedResult!.Title);
            query.Text = "局域网 192.168.1.42";
            Assert.Equal("LAN server", Assert.Single(vm.Results).Title);
            query.Text = "missing";
            Assert.Empty(vm.Results);
            Assert.False(vm.ExecuteSelectedCommand.CanExecute(null));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.False(vm.IsConfirmed);
            Assert.True(window.IsVisible);
            query.Text = "192.168.1.42";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.True(vm.IsConfirmed);
            Assert.Equal("LAN server", vm.Result!.Title);
            Assert.True(window.IsVisible);
            Assert.False(window.FindControl<Grid>("CommandPaletteOverlay")!.IsVisible);
            Assert.True(completion.IsCompletedSuccessfully);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Escape_ClosesWithoutChoosingAnAction() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        CommandPaletteViewModel vm = new([new("快速连接", "", "", PaletteAction.QuickConnect)]);
        MainWindow window = new();
        window.Show();
        Task<CommandPaletteItem?> completion = window.ShowCommandPaletteAsync(vm);
        HeadlessAvalonia.Pump();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.False(vm.IsConfirmed);
        Assert.Null(vm.Result);
        Assert.True(window.IsVisible);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Null(completion.GetAwaiter().GetResult());
        Assert.False(window.FindControl<Grid>("CommandPaletteOverlay")!.IsVisible);
        window.Close();
    });

    [Fact]
    public Task ShortcutRecording_RejectsConflicts_AndCancelKeepsPreviousShortcut() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        GeneralSettingsPage page = new("");
        KeyModifiers primary = AppShortcuts.NewTab.KeyModifiers;
        page.StartRecordingCommandPaletteShortcutCommand.Execute(null);
        page.RecordCommandPaletteShortcut(Key.T, primary);
        Assert.True(page.IsRecordingCommandPaletteShortcut);
        Assert.NotNull(page.CommandPaletteShortcutError);
        page.RecordCommandPaletteShortcut(Key.P, primary | KeyModifiers.Alt);
        Assert.False(page.IsRecordingCommandPaletteShortcut);
        Assert.Null(page.CommandPaletteShortcutError);
        KeyGesture custom = AppShortcuts.CommandPalette(page.CommandPaletteShortcut);
        Assert.Equal(primary | KeyModifiers.Alt, custom.KeyModifiers);
        page.StartRecordingCommandPaletteShortcutCommand.Execute(null);
        page.RecordCommandPaletteShortcut(Key.Escape, KeyModifiers.None);
        Assert.Equal(custom, AppShortcuts.CommandPalette(page.CommandPaletteShortcut));
        Assert.Equal(AppShortcuts.CommandPalette(AppShortcuts.DefaultCommandPaletteShortcut), AppShortcuts.CommandPalette("broken binding"));
    });

    [Fact]
    public async Task SettingsApplyAndReload_PersistCustomShortcut_CancelDiscardsDraft()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_shortcuts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            JsonSettingsService settings = new(Path.Combine(directory, "settings.json"));
            SettingsViewModel model = new(settings, directory);
            GeneralSettingsPage page = (GeneralSettingsPage)model.Categories.First(category => category.Page is GeneralSettingsPage).Page;
            string shortcut = AppShortcuts.Serialize(new KeyGesture(Key.P, AppShortcuts.NewTab.KeyModifiers | KeyModifiers.Alt));
            page.SetCommandPaletteShortcut(shortcut);
            Assert.True(await model.ApplyChangesAsync());
            JsonSettingsService reopened = new(Path.Combine(directory, "settings.json"));
            await reopened.LoadSettingsAsync();
            Assert.Equal(shortcut, reopened.Current.CommandPaletteShortcut);
            page.ResetCommandPaletteShortcutCommand.Execute(null);
            await model.CancelCommand.ExecuteAsync(null);
            model.Reload();
            Assert.Equal(shortcut, page.CommandPaletteShortcut);
            Assert.Equal(shortcut, settings.Current.CommandPaletteShortcut);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
