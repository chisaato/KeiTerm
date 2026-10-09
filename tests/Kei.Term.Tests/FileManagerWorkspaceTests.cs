using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Services;
using Kei.Term.App.Services.ContextMenus;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.App.Workspaces;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public class FileManagerWorkspaceTests
{
    [MonospaceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShowShellButton_RestoresClosedShellWithSameConnectionAndPanelWidth(bool onLeft) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.IsFileManagerOnLeft = onLeft;
        HeadlessAvalonia.Pump();
        TerminalConnectionView connection = host.Window.GetVisualDescendants().OfType<TerminalConnectionView>().Single(view => view.IsEffectivelyVisible);
        double originalWidth = connection.FindControl<Border>("SftpHost")!.Bounds.Width;
        host.Terminal.Terminal.Focus();
        await CloseShortcutAsync(host);
        Assert.False(host.Terminal.IsShellVisible);

        Click(host.Window, VisibleFiles(host).FindControl<Button>("FileManagerShowShellButton")!);
        Assert.True(host.Terminal.IsShellVisible);
        Assert.True(host.Terminal.IsFileManagerVisible);
        Assert.Same(host.Terminal, Assert.Single(host.Model.WorkspaceTabs));
        Assert.Same(host.Files, host.Terminal.FileManager);
        Assert.Equal("/var/log", host.Files.CurrentPath);
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());
        Assert.True(connection.FindControl<Border>("ShellHost")!.IsEffectivelyVisible);
        Assert.True(connection.FindControl<GridSplitter>("FileManagerSplitter")!.IsVisible);
        Assert.Equal(originalWidth, connection.FindControl<Border>("SftpHost")!.Bounds.Width, precision: 1);
        Assert.True(host.Model.OpenTerminalFindCommand.CanExecute(null));
        Assert.Equal(0, host.Session.DisposeCount);
        Assert.Equal(0, host.FileSystem.DisposeCount);

        await CloseShortcutAsync(host);
        Assert.False(host.Terminal.IsShellVisible);
        Assert.True(host.Terminal.IsFileManagerVisible);
        Assert.Single(host.Model.WorkspaceTabs);
    });

    [MonospaceFact]
    public Task ShowShellButton_InFileWorkspaceTabRestoresAndActivatesItsOwner() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.Terminal.Focus();
        await CloseShortcutAsync(host);
        FileManagerTabViewModel filesTab = Promote(host);
        Assert.False(host.Terminal.IsShellVisible);

        Click(host.Window, VisibleFiles(host).FindControl<Button>("FileManagerShowShellButton")!);
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);
        Assert.True(host.Terminal.IsShellVisible);
        Assert.False(host.Terminal.IsFileManagerVisible);
        Assert.Contains(filesTab, host.Model.WorkspaceTabs);
        Assert.Equal(2, host.Model.WorkspaceTabs.Count);
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(0, host.Session.DisposeCount);
        Assert.Equal(0, host.FileSystem.DisposeCount);
    });

    [MonospaceFact]
    public Task ShowShellButton_WhenAlreadyVisibleFocusesShellWithoutOpeningAnotherTab() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput")!.Focus();
        Click(host.Window, VisibleFiles(host).FindControl<Button>("FileManagerShowShellButton")!);
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(ConnectionDocumentKind.Shell, host.Terminal.ActiveDocument);
        Assert.Same(host.Terminal, Assert.Single(host.Model.WorkspaceTabs));
        Assert.Equal(0, host.Session.DisposeCount);
    });

    [MonospaceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CloseShortcut_FocusedFilesPreservesShellThenClosesEmptyTab(bool onLeft) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.IsFileManagerOnLeft = onLeft;
        HeadlessAvalonia.Pump();
        VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput")!.Focus();
        Assert.Equal(ConnectionDocumentKind.FileManager, host.Terminal.ActiveDocument);

        await CloseShortcutAsync(host);
        await Task.Delay(190);
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal, Assert.Single(host.Model.WorkspaceTabs));
        Assert.True(host.Terminal.IsShellVisible);
        Assert.False(host.Terminal.IsFileManagerVisible);
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(0, host.Session.DisposeCount);
        Assert.Equal(0, host.FileSystem.DisposeCount);

        await CloseShortcutAsync(host);
        Assert.Empty(host.Model.WorkspaceTabs);
        Assert.Equal(1, host.Session.DisposeCount);
        Assert.Equal(1, host.FileSystem.DisposeCount);
    });

    [MonospaceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CloseShortcut_FocusedShellPreservesFilesAndUsesWholePanel(bool onLeft) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.IsFileManagerOnLeft = onLeft;
        HeadlessAvalonia.Pump();
        host.Terminal.Terminal.Focus();
        Assert.Equal(ConnectionDocumentKind.Shell, host.Terminal.ActiveDocument);

        await CloseShortcutAsync(host);
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal, Assert.Single(host.Model.WorkspaceTabs));
        Assert.False(host.Terminal.IsShellVisible);
        Assert.True(host.Terminal.IsFileManagerVisible);
        Assert.False(host.Model.IsTerminalWorkspaceActive);
        Assert.False(host.Model.OpenTerminalFindCommand.CanExecute(null));
        TerminalConnectionView connection = host.Window.GetVisualDescendants().OfType<TerminalConnectionView>().Single(view => view.IsEffectivelyVisible);
        Assert.False(connection.FindControl<Border>("ShellHost")!.IsEffectivelyVisible);
        Assert.False(connection.FindControl<GridSplitter>("FileManagerSplitter")!.IsVisible);
        Assert.Equal(connection.Bounds.Width, connection.FindControl<Border>("SftpHost")!.Bounds.Width, precision: 1);
        Assert.Same(VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput"), host.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(0, host.Session.DisposeCount);
        Assert.Equal(0, host.FileSystem.DisposeCount);

        await CloseShortcutAsync(host);
        Assert.Empty(host.Model.WorkspaceTabs);
        Assert.Equal(1, host.Session.DisposeCount);
        Assert.Equal(1, host.FileSystem.DisposeCount);
    });

    [MonospaceFact]
    public Task ShellCloseButton_ClosesShellEvenWhenFilesHaveFocus() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput")!.Focus();
        TerminalConnectionView connection = host.Window.GetVisualDescendants().OfType<TerminalConnectionView>().Single(view => view.IsEffectivelyVisible);
        Click(host.Window, connection.FindControl<Button>("ShellCloseButton")!);
        Assert.False(host.Terminal.IsShellVisible);
        Assert.True(host.Terminal.IsFileManagerVisible);
        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Equal(0, host.Session.DisposeCount);

        Click(host.Window, VisibleFiles(host).FindControl<Button>("FileManagerCloseButton")!);
        HeadlessAvalonia.WaitUntil(() => host.Terminal.IsDisposed && host.FileSystem.DisposeCount == 1);
        Assert.Empty(host.Model.WorkspaceTabs);
    });

    [MonospaceFact]
    public Task CloseShortcut_AfterClickingSidebarInOtherSplitKeepsBothConnections() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        TerminalTabViewModel other = new("other", InstalledMonospace.Require(), 14);
        host.Model.Workspace.AddTab(other);
        IDocumentDock original = (IDocumentDock)host.Model.Workspace.FindDocument(host.Terminal)!.Owner!;
        host.Model.Workspace.SplitTab(other, original, DockOperation.Right);
        HeadlessAvalonia.Pump();

        VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput")!.Focus();
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);
        await CloseShortcutAsync(host);
        Assert.False(host.Terminal.IsFileManagerVisible);
        Assert.True(host.Terminal.IsShellVisible);
        Assert.Contains(other, host.Model.WorkspaceTabs);
        Assert.Contains(host.Terminal, host.Model.WorkspaceTabs);
        Assert.False(other.IsDisposed);
        Assert.Equal(0, host.Session.DisposeCount);
    });

    private static async Task CloseShortcutAsync(Host host)
    {
        if (OperatingSystem.IsMacOS())
        {
            NativeMenuItem close = Menus(NativeMenu.GetMenu(host.Window)!)
                .Single(item => ReferenceEquals(item.Command, host.Model.CloseCurrentWorkspaceTabCommand));
            Assert.Equal(AppShortcuts.CloseTab, close.Gesture);
        }
        RawInputModifiers modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        host.Window.KeyPress(AppShortcuts.CloseTab.Key, modifier, PhysicalKey.W, "w");
        HeadlessAvalonia.Pump();
        if (host.Model.CloseCurrentWorkspaceTabCommand.ExecutionTask is { } closing) await closing;
        Assert.Equal(0, host.Session.SendCount);
        HeadlessAvalonia.Pump();
    }

    [MonospaceTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task CopyRemotePath_MenuCopiesFileAndDirectoryPaths(bool nativeInvocation, bool multiple)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        RemoteFileManagerView view = VisibleFiles(host);
        RemoteFileItem file = new("日志 file.txt", "/var/log/日志 file.txt", false, 42, DateTimeOffset.UtcNow, "-rw-------");
        RemoteFileItem directory = new("备份", "/var/log/备份", true, 0, DateTimeOffset.UtcNow, "drwx------");
        host.Files.Items.Clear();
        host.Files.Items.Add(file);
        host.Files.Items.Add(directory);
        host.Files.SelectedItem = multiple ? file : directory;
        HeadlessAvalonia.Pump();
        ListBox list = view.FindControl<ListBox>("FileListBox")!;
        if (multiple) list.SelectedItems!.Add(directory);
        ContextMenu menu = list.ContextMenu!;
        try
        {
            menu.Open(list);
            HeadlessAvalonia.Pump();
            if (nativeInvocation)
            {
                ContextMenuSnapshot snapshot = ContextMenuSnapshot.Create(menu)!;
                NativeContextMenuEntry entry = snapshot.Entries.Single(entry => entry.Label == Strings.Get("FileManager.CopyRemotePath"));
                menu.Close();
                snapshot.Invoke(entry.Id);
            }
            else
            {
                MenuItem copy = menu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, Strings.Get("FileManager.CopyRemotePath")));
                copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            HeadlessAvalonia.Pump();

            Assert.Equal(multiple ? file.FullPath + "\n" + directory.FullPath : directory.FullPath,
                await host.Window.Clipboard!.TryGetTextAsync());
            Assert.Equal(0, host.Session.SendCount);
        }
        finally { menu.Close(); }
    });

    [MonospaceFact]
    public Task ToolbarButton_CreatesAndSelectsAFileDocument_WithTheExistingManager() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        Button promote = host.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.Command, host.Files.PromoteToTabCommand) && button.IsEffectivelyVisible);
        Click(host.Window, promote);

        Assert.Equal(2, host.Model.WorkspaceTabs.Count);
        Assert.NotSame(host.Terminal, host.Model.ActiveWorkspaceTab);
        Assert.Same(host.Terminal, host.Model.SelectedTab);
        Assert.Same(host.Terminal, host.Model.Workspace.ActiveTab);
        Assert.Single(host.Model.Tabs);
        WorkspaceDocument fileDocument = host.Model.Workspace.FindDocument(host.Model.ActiveWorkspaceTab!)!;
        DocumentTabStripItem[] headers = host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().ToArray();
        Assert.Equal(2, headers.Length);
        DocumentTabStripItem fileHeader = headers.Single(header => ReferenceEquals(header.DataContext, fileDocument));
        Assert.True(fileHeader.IsEffectivelyVisible && fileHeader.Bounds.Width > 0 && fileHeader.Bounds.Height > 0,
            $"File header {fileHeader.Bounds}, visible={fileHeader.IsEffectivelyVisible}");
        Point fileHeaderOrigin = fileHeader.TranslatePoint(default, host.Window)!.Value;
        Assert.True(fileHeaderOrigin.X >= 0 && fileHeaderOrigin.X + fileHeader.Bounds.Width <= host.Window.Bounds.Width,
            $"File header {fileHeader.Bounds} at {fileHeaderOrigin}, window={host.Window.Bounds}");
        Assert.Contains(fileHeader.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == fileDocument.Title);
        RemoteFileManagerView opened = host.Window.GetVisualDescendants().OfType<RemoteFileManagerView>()
            .Single(view => view.IsEffectivelyVisible && ReferenceEquals(view.DataContext, host.Files));
        Assert.False(opened.GetVisualAncestors().OfType<TerminalConnectionView>().Any());
        Assert.Equal("/var/log", host.Files.CurrentPath);
        Assert.Same(host.Listed, host.Files.Items.Single());
        Assert.False(host.Terminal.IsFileManagerVisible);
        Assert.Equal(0, host.FileSystem.DisposeCount);
        Assert.Equal(0, host.Session.DisposeCount);
    });

    [MonospaceFact]
    public Task RepeatedPromotion_SelectsExistingDocument_AndTracksItsTitleAndPath() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel tab = Promote(host);
        WorkspaceDocument document = host.Model.Workspace.FindDocument(tab)!;
        host.Model.Workspace.Activate(host.Terminal);
        HeadlessAvalonia.Pump();

        host.Files.PromoteToTabCommand.Execute(null);
        HeadlessAvalonia.Pump();
        Assert.Equal(2, host.Model.WorkspaceTabs.Count);
        Assert.Same(tab, host.Model.ActiveWorkspaceTab);
        Assert.Same(document, host.Model.Workspace.FindDocument(tab));
        host.Terminal.TabName = "Renamed connection";
        host.Files.CurrentPath = "/etc";
        Assert.Contains("Renamed connection", document.Title, StringComparison.Ordinal);
        Assert.Contains("/etc", tab.ToolTip, StringComparison.Ordinal);
        Assert.Equal(0, host.FileSystem.DisposeCount);
    });

    [MonospaceFact]
    public Task FileToolbarClose_ReleasesOnlyItsDocument_AndSidebarCanPromoteAgain() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel first = Promote(host);
        WorkspaceDocument document = host.Model.Workspace.FindDocument(first)!;
        TerminalWorkspaceView workspace = host.Window.FindControl<TerminalWorkspaceView>("WorkspaceHost")!;
        RemoteFileManagerView opened = VisibleFiles(host);
        Assert.Same(opened, host.Files.PickExecutableFileDialogAsync?.Target);
        Assert.False(opened.FindControl<Button>("FileManagerOpenInTabButton")!.IsEffectivelyVisible);
        Assert.False(opened.FindControl<Button>("FileManagerDockButton")!.IsEffectivelyVisible);
        Assert.True(workspace.IsCached(document));

        Click(host.Window, opened.FindControl<Button>("FileManagerCloseButton")!);

        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);
        Assert.Null(host.Model.Workspace.FindDocument(first));
        Assert.False(workspace.IsCached(document));
        Assert.Null(TopLevel.GetTopLevel(opened));
        Assert.NotSame(opened, host.Files.PickExecutableFileDialogAsync?.Target);
        Assert.False(host.Files.IsWorkspaceTab);
        Assert.Same(host.Files, host.Terminal.FileManager);
        Assert.Equal(0, host.FileSystem.DisposeCount);
        Assert.Equal(0, host.Session.DisposeCount);

        host.Model.ToggleFileManagerCommand.Execute(null);
        HeadlessAvalonia.Pump();
        RemoteFileManagerView sidebar = VisibleFiles(host);
        Assert.True(sidebar.GetVisualAncestors().OfType<TerminalConnectionView>().Any());
        Assert.Same(sidebar, host.Files.PickExecutableFileDialogAsync?.Target);
        await Task.Delay(190);
        HeadlessAvalonia.Pump();
        FileManagerTabViewModel second = Promote(host);
        RemoteFileManagerView reopened = VisibleFiles(host);
        Assert.NotSame(first, second);
        Assert.NotSame(opened, reopened);
        Assert.Same(reopened, host.Files.PickExecutableFileDialogAsync?.Target);
        Assert.False(workspace.IsCached(document));
        Assert.Equal("/var/log", host.Files.CurrentPath);
        Assert.Same(host.Listed, host.Files.Items.Single());
    });

    [MonospaceFact]
    public Task SwitchingBetweenFilesAndTheirTerminal_RefreshesCommandsAndKeepsFileFocus() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Model.IsComposeBarVisible = true;
        CommandPaletteItem[] terminalActions = host.Model.CreateCommandPalette().Results.ToArray();
        Assert.Contains(terminalActions, item => item.Action == PaletteAction.FindTerminal);
        Assert.True(host.Model.OpenTerminalFindCommand.CanExecute(null));
        Assert.True(host.Model.SendComposeCommand.CanExecute(null));
        int changes = 0;
        host.Model.OpenTerminalFindCommand.CanExecuteChanged += (_, _) => changes++;
        FileManagerTabViewModel files = Promote(host);
        Assert.False(host.Model.IsTerminalWorkspaceActive);
        Assert.Same(host.Terminal, host.Model.SelectedTab);
        Assert.False(host.Model.OpenTerminalFindCommand.CanExecute(null));
        Assert.False(host.Model.ToggleComposeBarCommand.CanExecute(null));
        Assert.False(host.Model.SendComposeCommand.CanExecute(null));
        CommandPaletteItem[] fileActions = host.Model.CreateCommandPalette().Results.ToArray();
        Assert.DoesNotContain(fileActions, item => item.Action is PaletteAction.FindTerminal or PaletteAction.ToggleComposeBar or PaletteAction.ToggleFileManager);
        Assert.Contains(fileActions, item => item.Action == PaletteAction.Disconnect);
        Button send = host.Window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, host.Model.SendComposeCommand));
        Assert.False(send.IsEffectivelyVisible);
        Assert.Same(VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput"), host.Window.FocusManager!.GetFocusedElement());

        host.Model.ComposeText = "echo must-not-send";
        await host.Model.SendComposeCommand.ExecuteAsync(null);
        foreach (CommandPaletteItem item in terminalActions.Where(item => item.Action is PaletteAction.FindTerminal or PaletteAction.ToggleComposeBar or PaletteAction.ToggleFileManager))
            await host.Model.ExecutePaletteItemAsync(item);
        Assert.Equal("echo must-not-send", host.Model.ComposeText);
        Assert.Equal(0, host.Session.SendCount);
        Assert.False(host.Terminal.IsFindBarOpen);
        Assert.True(host.Model.IsComposeBarVisible);
        Assert.False(host.Terminal.IsFileManagerVisible);

        host.Model.Workspace.Activate(host.Terminal);
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal, host.Model.SelectedTab);
        Assert.True(host.Model.IsTerminalWorkspaceActive);
        Assert.True(host.Model.OpenTerminalFindCommand.CanExecute(null));
        Assert.True(host.Model.ToggleComposeBarCommand.CanExecute(null));
        Assert.True(host.Model.SendComposeCommand.CanExecute(null));
        Assert.True(send.IsEffectivelyVisible);
        Assert.NotNull(TopLevel.GetTopLevel(host.Terminal.Terminal));
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());
        host.Model.Workspace.Activate(files);
        HeadlessAvalonia.Pump();
        Assert.False(host.Model.OpenTerminalFindCommand.CanExecute(null));
        Assert.True(changes >= 3);
        Assert.Same(files, host.Model.ActiveWorkspaceTab);
        Assert.Same(VisibleFiles(host).FindControl<TextBox>("FileManagerPathInput"), host.Window.FocusManager.GetFocusedElement());
    });

    [MonospaceFact]
    public Task PromotionUsesOwnerGroup_AndClosingOwnerRemovesSplitFileDocument() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        TerminalTabViewModel other = new("other", "monospace", 14);
        host.Model.Workspace.AddTab(other);
        IDocumentDock original = (IDocumentDock)host.Model.Workspace.FindDocument(host.Terminal)!.Owner!;
        host.Model.Workspace.SplitTab(other, original, DockOperation.Right);
        IDocumentDock otherGroup = (IDocumentDock)host.Model.Workspace.FindDocument(other)!.Owner!;
        HeadlessAvalonia.Pump();
        Assert.Same(other, host.Model.ActiveWorkspaceTab);

        // 即使请求发出时另一分组活动，文件页仍落在所属连接的分组。
        host.Files.PromoteToTabCommand.Execute(null);
        HeadlessAvalonia.Pump();
        FileManagerTabViewModel files = Assert.Single(host.Model.WorkspaceTabs.OfType<FileManagerTabViewModel>());
        WorkspaceDocument fileDocument = host.Model.Workspace.FindDocument(files)!;
        Assert.Same(original, fileDocument.Owner);
        Assert.Same(files, host.Model.ActiveWorkspaceTab);
        Assert.Same(host.Terminal, host.Model.SelectedTab);
        host.Model.Workspace.MoveTab(files, otherGroup, 1);
        HeadlessAvalonia.Pump();
        Assert.Same(otherGroup, fileDocument.Owner);
        Assert.Same(host.Files, VisibleFiles(host).DataContext);
        Assert.Equal(0, host.FileSystem.DisposeCount);

        await host.Model.CloseTabCommand.ExecuteAsync(host.Terminal);
        HeadlessAvalonia.Pump();
        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Same(other, host.Model.WorkspaceTabs.Single());
        Assert.Null(host.Model.Workspace.FindDocument(files));
        Assert.False(host.Window.FindControl<TerminalWorkspaceView>("WorkspaceHost")!.IsCached(fileDocument));
        Assert.Equal(1, host.FileSystem.DisposeCount);
        Assert.Equal(1, host.Session.DisposeCount);
    });

    [MonospaceFact]
    public Task DisconnectFromFileDocument_RemovesFilesButPreservesTerminalDocument() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel files = Promote(host);
        WorkspaceDocument fileDocument = host.Model.Workspace.FindDocument(files)!;
        CommandPaletteItem disconnect = host.Model.CreateCommandPalette().Results.Single(item => item.Action == PaletteAction.Disconnect);

        await host.Model.ExecutePaletteItemAsync(disconnect);
        HeadlessAvalonia.Pump();

        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);
        Assert.Equal(ConnectionState.Disconnected, host.Terminal.State);
        Assert.Null(host.Terminal.FileManager);
        Assert.False(host.Window.FindControl<TerminalWorkspaceView>("WorkspaceHost")!.IsCached(fileDocument));
        Assert.Equal(1, host.FileSystem.DisposeCount);
        Assert.Equal(1, host.Session.DisposeCount);
        Assert.False(host.Terminal.IsDisposed);
    });

    [MonospaceFact]
    public Task DockClose_RemovesOnlyFileDocument_AndLaterRequestsDoNotResurrectClosedOwner() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel files = Promote(host);
        WorkspaceDocument fileDocument = host.Model.Workspace.FindDocument(files)!;
        host.Model.Workspace.Factory.CloseDockable(fileDocument);
        HeadlessAvalonia.Pump();
        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);
        Assert.Equal(0, host.FileSystem.DisposeCount);

        // 平台关闭手势只关闭文件页，保留其连接；Dock 关闭与键路由共用清理链路。
        host.Files.PromoteToTabCommand.Execute(null);
        HeadlessAvalonia.Pump();
        Assert.IsType<FileManagerTabViewModel>(host.Model.ActiveWorkspaceTab);
        if (OperatingSystem.IsMacOS())
        {
            // macOS 快捷键由原生菜单导出；Headless 不提供 NSMenu 的键盘路由。
            NativeMenuItem close = Menus(NativeMenu.GetMenu(host.Window)!)
                .Single(item => ReferenceEquals(item.Command, host.Model.CloseCurrentWorkspaceTabCommand));
            Assert.Equal(AppShortcuts.CloseTab, close.Gesture);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(close.Command).ExecuteAsync(close.CommandParameter);
        }
        else host.Window.KeyPress(AppShortcuts.CloseTab.Key, RawInputModifiers.Control, PhysicalKey.W, "w");
        HeadlessAvalonia.Pump();
        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);
        Assert.Equal(0, host.Session.DisposeCount);
        Assert.Equal(0, host.FileSystem.DisposeCount);

        await host.Model.CloseTabCommand.ExecuteAsync(host.Terminal);
        host.Files.PromoteToTabCommand.Execute(null);
        host.Files.CloseCommand.Execute(null);
        host.Files.ShowShellCommand.Execute(null);
        HeadlessAvalonia.Pump();
        Assert.Empty(host.Model.WorkspaceTabs);
        Assert.Null(host.Model.ActiveWorkspaceTab);
        Assert.Equal(1, host.FileSystem.DisposeCount);
        Assert.Equal(1, host.Session.DisposeCount);
    });

    private static FileManagerTabViewModel Promote(Host host)
    {
        Click(host.Window, VisibleFiles(host).FindControl<Button>("FileManagerOpenInTabButton")!);
        return Assert.IsType<FileManagerTabViewModel>(host.Model.ActiveWorkspaceTab);
    }

    private static RemoteFileManagerView VisibleFiles(Host host) => host.Window.GetVisualDescendants().OfType<RemoteFileManagerView>()
        .Single(view => view.IsEffectivelyVisible && ReferenceEquals(view.DataContext, host.Files));

    private static IEnumerable<NativeMenuItem> Menus(NativeMenu menu)
    {
        foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu != null)
                foreach (NativeMenuItem child in Menus(item.Menu)) yield return child;
        }
    }

    private static void Click(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        HeadlessAvalonia.Pump();
    }

    private sealed class Host : IAsyncDisposable
    {
        private Host(MainViewModel model, TerminalTabViewModel terminal, RecordingSession session, RecordingFileSystem fileSystem)
        {
            Model = model;
            Terminal = terminal;
            Session = session;
            FileSystem = fileSystem;
            Files = terminal.FileManager!;
            Files.CurrentPath = "/var/log";
            Files.Items.Clear();
            Listed = new RemoteFileItem("syslog", "/var/log/syslog", false, 512, DateTimeOffset.UnixEpoch, "-rw-r--r--");
            Files.Items.Add(Listed);
            Window = new MainWindow { DataContext = model };
            Window.Show();
            HeadlessAvalonia.Pump();
            InstalledMonospace.AssertUsableCellHeight(terminal.Terminal, terminal.Terminal.FontFamilyName);
        }

        public MainViewModel Model { get; }
        public TerminalTabViewModel Terminal { get; }
        public RemoteFileManagerViewModel Files { get; }
        public RecordingSession Session { get; }
        public RecordingFileSystem FileSystem { get; }
        public RemoteFileItem Listed { get; }
        public MainWindow Window { get; }

        public static async Task<Host> CreateAsync()
        {
            UiDesignSystemService.Apply();
            SqliteConnectionFactory database = new("Data Source=:memory:");
            InternalVaultManager vault = new(database);
            MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database), vault, vault,
                new FixedSettingsService(new AppSettings { ConfirmBeforeClose = false, SessionManagerPinned = false }), new SshSessionFactory());
            model.ApplySessionManagerSettings();
            TerminalTabViewModel terminal = new("LAN", InstalledMonospace.Require(), 14);
            RecordingSession session = new();
            RecordingFileSystem fileSystem = new();
            terminal.AttachSession(session);
            terminal.MarkConnected();
            await terminal.InitializeFileManagerAsync(fileSystem, new IdleFileTracker());
            terminal.IsFileManagerVisible = true;
            model.Workspace.AddTab(terminal);
            return new Host(model, terminal, session, fileSystem);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            foreach (TerminalTabViewModel tab in Model.Tabs.ToArray()) await tab.DisposeAsync();
            await Model.DisposeAsync();
        }
    }

    private sealed class RecordingSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public int DisposeCount;
        public int SendCount;
#pragma warning disable CS0067
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) { Interlocked.Increment(ref SendCount); return Task.CompletedTask; }
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public int DisposeCount;
        public bool IsConnected => DisposeCount == 0;
        public string WorkingDirectory => "/home/kei";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }

    private sealed class IdleFileTracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
