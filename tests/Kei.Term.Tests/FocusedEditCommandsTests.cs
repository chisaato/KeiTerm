namespace Kei.Term.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;

public class FocusedEditCommandsTests
{
    [Fact]
    public Task TerminalCommands_CopyActualScreenSelection_AndDisableDestructiveEdits() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        RoyalTerminal.Avalonia.Controls.TerminalControl terminal = TerminalControlFactory.Create(new VtCallbackHooks());
        TreeView tree = new();
        Window window = new() { Width = 800, Height = 480, Content = new StackPanel { Children = { terminal, tree } } };
        using FocusedEditCommands commands = new(window, tree, _ => null);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            terminal.WriteOutput(Encoding.UTF8.GetBytes("remote screen text"));
            HeadlessAvalonia.Pump();
            terminal.Focus();
            commands[EditAction.SelectAll].Execute(null);
            Assert.True(commands[EditAction.Copy].CanExecute(null));
            commands[EditAction.Copy].Execute(null);
            HeadlessAvalonia.Pump();
            Assert.Contains("remote screen text", window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
            Assert.False(commands[EditAction.Cut].CanExecute(null));
            Assert.False(commands[EditAction.Delete].CanExecute(null));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InputCommands_EditSelectionAndClipboard_WithoutTouchingTree() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TextBox input = new() { Text = "alpha beta" };
        TreeView tree = new();
        Window window = new() { Content = new StackPanel { Children = { input, tree } } };
        using FocusedEditCommands commands = new(window, tree, _ => null);
        try
        {
            window.Show();
            input.Focus();
            input.SelectionStart = 0;
            input.SelectionEnd = 5;
            commands[EditAction.Copy].Execute(null);
            HeadlessAvalonia.Pump();
            Assert.Equal("alpha", window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
            Assert.Equal("alpha beta", input.Text);
            commands[EditAction.Cut].Execute(null);
            HeadlessAvalonia.Pump();
            Assert.Equal(" beta", input.Text);
            commands[EditAction.Paste].Execute(null);
            HeadlessAvalonia.Pump();
            Assert.Equal("alpha beta", input.Text);
            commands[EditAction.SelectAll].Execute(null);
            Assert.Equal(input.Text, input.SelectedText);
            commands[EditAction.Delete].Execute(null);
            Assert.Equal(string.Empty, input.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MenuFocus_PreservesInputTarget_OtherControlsClearIt() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TextBox input = new() { Text = "menu target" };
        MenuItem menuItem = new() { Header = "编辑", Focusable = true };
        Menu menu = new();
        menu.Items.Add(menuItem);
        Button other = new() { Content = "其他操作" };
        TreeView tree = new();
        Window window = new() { Content = new StackPanel { Children = { menu, input, tree, other } } };
        using FocusedEditCommands commands = new(window, tree, _ => null);
        try
        {
            window.Show();
            window.UpdateLayout();
            input.Focus();
            input.SelectAll();
            Assert.True(menuItem.Focus());
            Assert.True(commands[EditAction.Copy].CanExecute(null));
            commands[EditAction.Copy].Execute(null);
            HeadlessAvalonia.Pump();
            Assert.Equal("menu target", window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
            other.Focus();
            Assert.False(commands[EditAction.Copy].CanExecute(null));
            Assert.False(commands[EditAction.Delete].CanExecute(null));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ProtectedInputs_DisableCutCopyOrPasteAsAppropriate() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TextBox input = new() { Text = "sensitive", PasswordChar = '•' };
        TreeView tree = new();
        Window window = new() { Content = new StackPanel { Children = { input, tree } } };
        using FocusedEditCommands commands = new(window, tree, _ => null);
        try
        {
            window.Show();
            input.Focus();
            input.SelectAll();
            Assert.False(commands[EditAction.Copy].CanExecute(null));
            Assert.False(commands[EditAction.Cut].CanExecute(null));
            input.PasswordChar = default;
            input.IsReadOnly = true;
            Assert.True(commands[EditAction.Copy].CanExecute(null));
            Assert.False(commands[EditAction.Cut].CanExecute(null));
            Assert.False(commands[EditAction.Paste].CanExecute(null));
            Assert.False(commands[EditAction.Delete].CanExecute(null));
            input.IsEnabled = false;
            Assert.False(commands[EditAction.Copy].CanExecute(null));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RealMainWindow_EditMenuUsesFilterFocus_InsteadOfSelectedSession() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_edit_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        try
        {
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "test.db")}");
            InternalVaultManager vault = new(database);
            MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database),
                vault, vault, new JsonSettingsService(Path.Combine(directory, "settings.json")), new SshSessionFactory());
            model.CurrentSettings.ConfirmBeforeClose = false;
            model.FilterText = "copy this filter";
            model.SelectedTreeNode = new Kei.Term.Core.Models.SessionNode { Name = "selected session" };
            window = new MainWindow { DataContext = model };
            window.Show();
            window.UpdateLayout();
            TextBox filter = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == model.FilterText);
            filter.Focus();
            filter.SelectAll();
            NativeMenu edit = ((NativeMenuItem)NativeMenu.GetMenu(window)!.Items[1]).Menu!;
            NativeMenuItem copy = edit.Items.OfType<NativeMenuItem>().Single(item => item.Header == Strings.Get("Menu.Edit.Copy"));
            Assert.True(copy.Command!.CanExecute(null));
            copy.Command.Execute(null);
            HeadlessAvalonia.Pump();
            Assert.Equal("copy this filter", window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
            Assert.False(model.PasteNodeCommand.CanExecute(null));
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });
}
