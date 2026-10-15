using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services.ContextMenus;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;

namespace Kei.Term.Tests;

public partial class FileManagerWorkspaceTests
{
    [MonospaceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DirectoryMenu_OpensIndependentTabsFromSidebarAndFileTab(bool nativeInvocation) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        RemoteFileItem directory = DirectoryItem("/var/log/备份 files");
        host.Files.Items.Add(directory);
        host.Files.SelectedItem = directory;
        HeadlessAvalonia.Pump();

        OpenDirectoryMenu(VisibleFiles(host), nativeInvocation);

        FileManagerTabViewModel first = Assert.IsType<FileManagerTabViewModel>(host.Model.ActiveWorkspaceTab);
        Assert.NotSame(host.Files, first.FileManager);
        Assert.Equal(directory.FullPath, first.FileManager.CurrentPath);
        Assert.Equal("/var/log", host.Files.CurrentPath);
        Assert.Same(directory, host.Files.SelectedItem);
        Assert.True(host.Terminal.IsFileManagerVisible);
        Assert.Equal(2, host.Model.WorkspaceTabs.Count);
        AssertFileHeadersAreVisible(host);

        // 从独立文件页再打开目录仍须新增，不能只激活第一个文件页或改掉它的路径。
        RemoteFileItem nested = DirectoryItem("/var/log/备份 files/nested");
        first.FileManager.Items.Add(nested);
        first.FileManager.SelectedItem = nested;
        HeadlessAvalonia.Pump();
        RemoteFileManagerView view = host.Window.GetVisualDescendants().OfType<RemoteFileManagerView>()
            .Single(control => control.IsEffectivelyVisible && ReferenceEquals(control.DataContext, first.FileManager));
        OpenDirectoryMenu(view, nativeInvocation);

        FileManagerTabViewModel second = Assert.IsType<FileManagerTabViewModel>(host.Model.ActiveWorkspaceTab);
        Assert.NotSame(first, second);
        Assert.NotSame(first.FileManager, second.FileManager);
        Assert.Equal(nested.FullPath, second.FileManager.CurrentPath);
        Assert.Equal(directory.FullPath, first.FileManager.CurrentPath);
        Assert.Equal(3, host.Model.WorkspaceTabs.Count);
        AssertFileHeadersAreVisible(host);
        Assert.Equal("LAN · 备份 files", first.Title);
        Assert.Equal("LAN · nested", second.Title);
        Assert.Contains(directory.FullPath, first.ToolTip);
        Assert.Contains(nested.FullPath, second.ToolTip);

        await host.Model.CloseWorkspaceTabCommand.ExecuteAsync(second);
        HeadlessAvalonia.Pump();
        Assert.Equal(2, host.Model.WorkspaceTabs.Count);
        Assert.Equal(0, host.FileSystem.DisposeCount);
        Assert.Equal(0, host.Session.DisposeCount);
        await first.FileManager.NavigateToTerminalDirectoryAsync("/etc");
        Assert.Equal("/etc", first.FileManager.CurrentPath);
        Assert.Equal("/var/log", host.Files.CurrentPath);
    });

    [MonospaceFact]
    public Task DirectoryMenu_IsUnavailableForFiles() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Files.SelectedItem = host.Listed;
        HeadlessAvalonia.Pump();
        ContextMenu menu = VisibleFiles(host).FindControl<ListBox>("FileListBox")!.ContextMenu!;
        menu.Open(VisibleFiles(host).FindControl<ListBox>("FileListBox")!);
        try
        {
            HeadlessAvalonia.Pump();
            MenuItem entry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Strings.Get("FileManager.OpenInTab")));
            Assert.False(entry.IsVisible);
            Assert.False(entry.Command!.CanExecute(entry.CommandParameter));
            host.Files.OpenDirectoryInTabCommand.Execute(host.Listed);
            Assert.Same(host.Terminal, Assert.Single(host.Model.WorkspaceTabs));
        }
        finally { menu.Close(); }
    });

    [MonospaceFact]
    public Task ClosingConnection_DrainsDirectoryTabInitializationBeforeDisposingSharedChannel() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        TaskCompletionSource<IReadOnlyList<RemoteFileItem>> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.FileSystem.ListDirectory = (_, ct) =>
        {
            ct.Register(() => cancelled.TrySetResult());
            // 模拟不能立即取消的远程请求；必须等它实际结束后才能释放通道。
            return pending.Task;
        };
        host.Files.OpenDirectoryInTabCommand.Execute(DirectoryItem("/pending"));
        FileManagerTabViewModel directory = Assert.IsType<FileManagerTabViewModel>(host.Model.ActiveWorkspaceTab);
        Assert.True(directory.FileManager.IsLoading);

        Task closing = host.Model.CloseTabCommand.ExecuteAsync(host.Terminal);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(closing.IsCompleted);
            Assert.Equal(0, host.FileSystem.DisposeCount);
        }
        finally { pending.TrySetResult([]); }
        await closing;
        HeadlessAvalonia.Pump();
        Assert.Empty(host.Model.WorkspaceTabs);
        Assert.Equal(1, host.FileSystem.DisposeCount);
        Assert.Equal(1, host.Session.DisposeCount);
        Assert.False(directory.FileManager.OpenDirectoryInTabCommand.CanExecute(DirectoryItem("/late")));
    });

    private static RemoteFileItem DirectoryItem(string path)
        => new(path[(path.LastIndexOf('/') + 1)..], path, true, 0, DateTimeOffset.UnixEpoch, "drwx------");

    private static void OpenDirectoryMenu(RemoteFileManagerView view, bool nativeInvocation)
    {
        ListBox list = view.FindControl<ListBox>("FileListBox")!;
        ContextMenu menu = list.ContextMenu!;
        try
        {
            menu.Open(list);
            HeadlessAvalonia.Pump();
            if (nativeInvocation)
            {
                ContextMenuSnapshot snapshot = ContextMenuSnapshot.Create(menu)!;
                NativeContextMenuEntry entry = snapshot.Entries.Single(entry => entry.Label == Strings.Get("FileManager.OpenInTab"));
                menu.Close();
                snapshot.Invoke(entry.Id);
            }
            else
            {
                MenuItem entry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Strings.Get("FileManager.OpenInTab")));
                Assert.True(entry.IsVisible);
                Assert.True(entry.Command!.CanExecute(entry.CommandParameter));
                entry.Command.Execute(entry.CommandParameter);
            }
        }
        finally { menu.Close(); }
        HeadlessAvalonia.Pump();
    }

    private static void AssertFileHeadersAreVisible(Host host)
    {
        DocumentTabStripItem[] headers = host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().ToArray();
        Assert.Equal(host.Model.WorkspaceTabs.Count, headers.Length);
        double previousRight = 0;
        foreach (DocumentTabStripItem header in headers)
        {
            Assert.True(header.IsEffectivelyVisible);
            Assert.True(header.Bounds.Width > 0 && header.Bounds.Height > 0);
            Point origin = header.TranslatePoint(default, host.Window)!.Value;
            Assert.True(origin.X >= previousRight && origin.X + header.Bounds.Width <= host.Window.Bounds.Width,
                $"标签在屏幕外或重叠: {origin}, {header.Bounds}; 前一标签右边缘={previousRight}");
            previousRight = origin.X + header.Bounds.Width;
        }
    }
}
