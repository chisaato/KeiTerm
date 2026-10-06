using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.App.Workspaces;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using AvaloniaDock = Avalonia.Controls.Dock;
using TabPlacement = Kei.Term.Core.Models.Profiles.TabPlacement;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

// 真实协调器、停靠视图和终端标签。Headless 只证明布局、焦点、缓存和尺寸下发，不代替桌面手感。
public class WorkspaceHostTests
{
    [Fact]
    public Task SecondView_StealsTerminalWithoutKeepingTwoParents() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel tab = CreateTab("one");
        TerminalWorkspaceDocument document = new(tab);
        TerminalConnectionView first = new() { DataContext = document };
        TerminalConnectionView second = new() { DataContext = document };
        StackPanel panel = new();
        panel.Children.Add(first);
        panel.Children.Add(second);
        Window window = new()
        {
            Width = 900,
            Height = 700,
            Content = panel
        };
        window.Show();
        HeadlessAvalonia.Pump(20);

        Assert.Null(first.FindControl<ScrollViewer>("TerminalScroll")!.Content);
        Assert.Same(tab.Terminal, second.FindControl<ScrollViewer>("TerminalScroll")!.Content);
        Assert.Single(tab.Terminal.GetVisualAncestors().OfType<ScrollViewer>());
        window.Close();
    });

    [Fact]
    public Task TwoGroups_ShowSelectedContentSimultaneously() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        using Host host = Show(first, second);
        Split(host, second, DockOperation.Right);

        List<TerminalConnectionView> views = host.Window.GetVisualDescendants().OfType<TerminalConnectionView>().ToList();
        Assert.Equal(2, views.Count);
        Assert.All(views, view =>
        {
            Assert.True(view.IsVisible);
            Assert.True(view.Bounds.Width > 40, $"宽度 {view.Bounds.Width}");
            Assert.True(view.Bounds.Height > 40, $"高度 {view.Bounds.Height}");
        });
        Assert.Contains(views, view => view.DataContext is TerminalWorkspaceDocument document && document.Tab.IsSelected);
        Assert.Contains(views, view => view.DataContext is TerminalWorkspaceDocument document && !document.Tab.IsSelected);
    });

    [Fact]
    public Task SftpInteraction_SelectsComposeTarget() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        RecordingSession firstSession = new();
        RecordingSession secondSession = new();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        first.IsFileManagerVisible = true;
        second.IsFileManagerVisible = true;
        first.AttachSession(firstSession);
        second.AttachSession(secondSession);
        using Host host = Show(first, second);
        Split(host, second, DockOperation.Right);
        Assert.Same(second, host.ViewModel.SelectedTab);

        TerminalConnectionView firstView = host.Window.GetVisualDescendants()
            .OfType<TerminalConnectionView>()
            .Single(view => view.DataContext is TerminalWorkspaceDocument document && ReferenceEquals(document.Tab, first));
        Border sftp = firstView.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SftpHost");
        Assert.True(sftp.Focus());
        HeadlessAvalonia.Pump();

        Assert.Same(first, host.ViewModel.SelectedTab);
        Assert.Same(first, host.ViewModel.Workspace.ActiveTab);
        first.SendCommandAsync("echo sftp-target").GetAwaiter().GetResult();
        Assert.Contains("echo sftp-target", firstSession.SentText, StringComparison.Ordinal);
        Assert.DoesNotContain("echo sftp-target", secondSession.SentText, StringComparison.Ordinal);
    });

    [Fact]
    public Task ComposeFocus_PreservesLastTarget() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        MainViewModel viewModel = CreateViewModel();
        viewModel.Workspace.AddTab(first);
        viewModel.Workspace.AddTab(second);
        TerminalWorkspaceView workspace = new() { DataContext = viewModel };
        TextBox compose = new();
        Grid grid = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        Grid.SetRow(compose, 1);
        grid.Children.Add(workspace);
        grid.Children.Add(compose);
        Window window = new()
        {
            Width = 1200,
            Height = 800,
            Content = grid
        };
        window.Show();
        HeadlessAvalonia.Pump(30);

        TerminalTabViewModel? before = viewModel.Workspace.ActiveTab;
        Assert.NotNull(before);
        Assert.True(compose.Focus());
        HeadlessAvalonia.Pump();
        Assert.Same(before, viewModel.Workspace.ActiveTab);
        Assert.Same(before, viewModel.SelectedTab);
        window.Close();
    });

    [Fact]
    public Task BottomTabs_ApplyToNewSplitGroups() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        MainViewModel viewModel = CreateViewModel();
        viewModel.TabPlacement = TabPlacement.Bottom;
        viewModel.Workspace.AddTab(first);
        viewModel.Workspace.AddTab(second);
        TerminalWorkspaceView workspace = new() { DataContext = viewModel };
        Window window = new()
        {
            Width = 1200,
            Height = 800,
            Content = workspace
        };
        window.Show();
        HeadlessAvalonia.Pump(30);
        IDocumentDock group = RequireGroup(viewModel.Workspace, first);
        viewModel.Workspace.SplitTab(second, group, DockOperation.Bottom);
        HeadlessAvalonia.Pump(40);

        List<DocumentTabStrip> strips = window.GetVisualDescendants().OfType<DocumentTabStrip>().ToList();
        Assert.True(strips.Count >= 2, $"标签条数量 {strips.Count}");
        Assert.All(strips, strip => Assert.Equal(AvaloniaDock.Bottom, DockPanel.GetDock(strip)));
        window.Close();
    });

    [Fact]
    public Task CancelledDrag_PreservesMembership() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        using Host host = Show(first, second);
        TerminalWorkspaceDocument document = RequireDocument(host.ViewModel.Workspace.Layout, second);
        IDock? owner = document.Owner as IDock;
        int groups = CountGroups(host.ViewModel.Workspace.Layout);
        DocumentTabStripItem item = host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>()
            .First(candidate => ReferenceEquals(candidate.DataContext, document));
        Point origin = item.TranslatePoint(new Point(8, 8), host.Window) ?? new Point(30, 12);

        host.Window.MouseDown(origin, MouseButton.Left);
        host.Window.MouseMove(origin + new Vector(28, 0));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "Escape");
        host.Window.MouseUp(new Point(-40, -40), MouseButton.Left);
        HeadlessAvalonia.Pump();

        Assert.Same(owner, document.Owner);
        Assert.Equal(groups, CountGroups(host.ViewModel.Workspace.Layout));
        Assert.Contains(second, host.ViewModel.Tabs);
    });

    [Fact]
    public Task Close_RemovesOnlyClosedDocumentViewCache() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        using Host host = Show(first, second);
        Split(host, second, DockOperation.Right);
        TerminalWorkspaceDocument firstDocument = RequireDocument(host.ViewModel.Workspace.Layout, first);
        TerminalWorkspaceDocument secondDocument = RequireDocument(host.ViewModel.Workspace.Layout, second);
        Assert.True(host.Workspace.IsCached(firstDocument));
        Assert.True(host.Workspace.IsCached(secondDocument));

        host.ViewModel.CloseTabCommand.ExecuteAsync(second).GetAwaiter().GetResult();
        HeadlessAvalonia.Pump();

        Assert.True(host.Workspace.IsCached(firstDocument));
        Assert.False(host.Workspace.IsCached(secondDocument));
        Assert.Contains(first, host.ViewModel.Tabs);
        Assert.DoesNotContain(second, host.ViewModel.Tabs);
    });

    [Fact]
    public Task Close_DisposesSessionExactlyOnce_WhileMoveDoesNotDispose() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        RecordingSession firstSession = new();
        RecordingSession secondSession = new();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        first.AttachSession(firstSession);
        second.AttachSession(secondSession);
        using Host host = Show(first, second);
        Split(host, second, DockOperation.Right);
        Assert.Equal(0, firstSession.DisposeCount);
        Assert.Equal(0, secondSession.DisposeCount);

        host.ViewModel.CloseTabCommand.ExecuteAsync(second).GetAwaiter().GetResult();
        DateTime until = DateTime.UtcNow.AddSeconds(3);
        while (secondSession.DisposeCount == 0 && DateTime.UtcNow < until)
        {
            HeadlessAvalonia.Pump(2);
        }

        Assert.Equal(0, firstSession.DisposeCount);
        Assert.Equal(1, secondSession.DisposeCount);
        Thread.Sleep(80);
        Assert.Equal(1, secondSession.DisposeCount);
    });

    [Fact]
    public Task Split_PropagatesChangedTerminalSizeToEndpoint() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        RecordingSession session = new();
        TerminalTabViewModel first = CreateTab("one");
        TerminalTabViewModel second = CreateTab("two");
        second.AttachSession(session);
        using Host host = Show(first, second);
        HeadlessAvalonia.Pump(20);
        Resize before = session.Resizes.LastOrDefault(resize => resize.Width > 80);
        Assert.True(before.Width > 80, "分屏前没有拿到终端宽度");

        Split(host, second, DockOperation.Right);
        HeadlessAvalonia.Pump(30);
        Resize after = session.Resizes.LastOrDefault(resize => resize.Width > 40);

        Assert.True(after.Width < before.Width - 40, $"分屏前 {before.Width}px，分屏后 {after.Width}px");
        Assert.True(after.Columns > 0);
        Assert.True(after.Rows > 0);
    });

    private static void Split(Host host, TerminalTabViewModel tab, DockOperation operation)
    {
        IDocumentDock group = RequireGroup(host.ViewModel.Workspace, host.ViewModel.Tabs[0]);
        host.ViewModel.Workspace.SplitTab(tab, group, operation);
        HeadlessAvalonia.Pump(40);
    }

    private static IDocumentDock RequireGroup(WorkspaceCoordinator workspace, TerminalTabViewModel tab)
    {
        TerminalWorkspaceDocument document = RequireDocument(workspace.Layout, tab);
        return Assert.IsAssignableFrom<IDocumentDock>(document.Owner);
    }

    private static TerminalWorkspaceDocument RequireDocument(IDockable node, TerminalTabViewModel tab)
    {
        TerminalWorkspaceDocument? found = FindDocument(node, tab);
        Assert.NotNull(found);
        return found;
    }

    private static TerminalWorkspaceDocument? FindDocument(IDockable node, TerminalTabViewModel tab)
    {
        if (node is TerminalWorkspaceDocument document && ReferenceEquals(document.Tab, tab))
        {
            return document;
        }

        if (node is not IDock dock || dock.VisibleDockables == null)
        {
            return null;
        }

        foreach (IDockable child in dock.VisibleDockables)
        {
            TerminalWorkspaceDocument? found = FindDocument(child, tab);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static int CountGroups(IDockable node)
    {
        int count = node is IDocumentDock ? 1 : 0;
        if (node is not IDock dock || dock.VisibleDockables == null)
        {
            return count;
        }

        foreach (IDockable child in dock.VisibleDockables)
        {
            count += CountGroups(child);
        }

        return count;
    }

    private static Host Show(params TerminalTabViewModel[] tabs)
    {
        MainViewModel viewModel = CreateViewModel();
        foreach (TerminalTabViewModel tab in tabs)
        {
            viewModel.Workspace.AddTab(tab);
        }

        TerminalWorkspaceView workspace = new() { DataContext = viewModel };
        Window window = new()
        {
            Width = 1200,
            Height = 800,
            Content = workspace
        };
        window.Show();
        HeadlessAvalonia.Pump(30);
        return new Host(viewModel, workspace, window);
    }

    private static TerminalTabViewModel CreateTab(string title)
        => new(title, "DejaVu Sans Mono", 14);

    private static MainViewModel CreateViewModel()
    {
        string path = Path.Combine(Path.GetTempPath(), $"keiterm-dock-{Guid.NewGuid():N}.json");
        return new MainViewModel(
            new EmptyTree(),
            new EmptyIdentities(),
            new PlainVault(),
            new PlainVault(),
            new JsonSettingsService(path),
            new UnusedFactory());
    }

    private sealed class Host : IDisposable
    {
        public Host(MainViewModel viewModel, TerminalWorkspaceView workspace, Window window)
        {
            ViewModel = viewModel;
            Workspace = workspace;
            Window = window;
        }

        public MainViewModel ViewModel { get; }

        public TerminalWorkspaceView Workspace { get; }

        public Window Window { get; }

        public void Dispose() => Window.Close();
    }

    private readonly record struct Resize(int Columns, int Rows, int Width, int Height);

    private sealed class RecordingSession : ITerminalSession
    {
        private readonly StringBuilder _sent = new();

        public List<Resize> Resizes { get; } = [];

        public int DisposeCount;

        public string SentText
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToString();
                }
            }
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public bool IsConnected => true;

#pragma warning disable CS0067
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            lock (_sent)
            {
                _sent.Append(Encoding.UTF8.GetString(data.Span));
            }

            return Task.CompletedTask;
        }

        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
        {
            Resizes.Add(new Resize(columns, rows, widthPx, heightPx));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyTree : ITreeRepository
    {
        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TreeNodeBase>>([]);

        public Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<TreeNodeBase?>(null);

        public Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteNodeAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyIdentities : IIdentityRepository
    {
        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Identity>>([]);

        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Identity?>(null);

        public Task SaveAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PlainVault : IVaultManager, IVaultSecretStore
    {
        public bool IsUnlocked => true;

        public bool IsPlainMode => true;

        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;

        public void Lock()
        {
        }

        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, SecretPayload>());

        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnusedFactory : ISshSessionFactory
    {
        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
