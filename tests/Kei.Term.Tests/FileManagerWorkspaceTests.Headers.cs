using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Workspaces;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Kei.Term.Tests;

public partial class FileManagerWorkspaceTests
{
    [MonospaceTheory]
    [InlineData("/", "/")]
    [InlineData("/var/log/", "log")]
    [InlineData("/var/备份 files", "备份 files")]
    [InlineData("/var/a\\b", "a\\b")]
    public Task FileHeader_TracksDirectoryAndConnection_WithFullPathInTooltip(string path, string directory) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel files = Promote(host);
        WorkspaceDocument document = host.Model.Workspace.FindDocument(files)!;
        host.Files.CurrentPath = path;
        host.Terminal.Rename("production");
        HeadlessAvalonia.Pump();

        Assert.Equal($"production · {directory}", document.Title);
        Assert.Contains(path, files.ToolTip);
        DocumentTabStripItem header = HeaderFor(host, document);
        Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == document.Title);
    });

    [MonospaceFact]
    public Task ConnectionHeaders_ShowDistinctIcons_AndShareLiveConnectionStatus() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.FollowRemoteTitle = true;
        host.Terminal.ApplyRemoteTitle("vim /etc/hosts");
        FileManagerTabViewModel files = Promote(host);
        DocumentTabStripItem terminalHeader = HeaderFor(host, host.Model.Workspace.FindDocument(host.Terminal)!);
        DocumentTabStripItem fileHeader = HeaderFor(host, host.Model.Workspace.FindDocument(files)!);

        Assert.Contains(terminalHeader.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "LAN · vim /etc/hosts");
        AvaloniaPath terminalIcon = VisibleKindIcon(terminalHeader);
        AvaloniaPath fileIcon = VisibleKindIcon(fileHeader);
        Assert.Same(host.Window.FindResource("Kei.Icon.Terminal"), terminalIcon.Data);
        Assert.Same(host.Window.FindResource("Kei.Icon.Folder"), fileIcon.Data);
        foreach (AvaloniaPath icon in new[] { terminalIcon, fileIcon })
        {
            Viewbox viewport = icon.GetVisualAncestors().OfType<Viewbox>().First();
            Assert.Equal(16, viewport.Bounds.Width);
            Assert.Equal(16, viewport.Bounds.Height);
        }
        Assert.Equal(ColorFor(host, "Kei.Text.Primary"), Assert.IsAssignableFrom<ISolidColorBrush>(fileIcon.Stroke).Color);

        foreach (ConnectionState state in new[] { ConnectionState.Connected, ConnectionState.Connecting, ConnectionState.Error })
        {
            host.Terminal.State = state;
            HeadlessAvalonia.Pump();
            foreach (DocumentTabStripItem header in new[] { terminalHeader, fileHeader })
            {
                Border indicator = header.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "ConnectionStateIndicator");
                Assert.Equal(3, indicator.Bounds.Width);
                Assert.Equal(14, indicator.Bounds.Height);
                Assert.Equal(ColorFor(host, $"Kei.Status.{state}"), Assert.IsAssignableFrom<ISolidColorBrush>(indicator.Background).Color);
            }
        }
    });

    [MonospaceFact]
    public Task FullscreenFileManager_ChangesHeaderIcon_AndRestoresTerminalIcon() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        DocumentTabStripItem header = HeaderFor(host, host.Model.Workspace.FindDocument(host.Terminal)!);
        host.Terminal.ShowFileManagerFullScreenCommand.Execute(null);
        HeadlessAvalonia.Pump();
        Assert.Same(host.Window.FindResource("Kei.Icon.Folder"), VisibleKindIcon(header).Data);
        Assert.Equal("LAN · log", host.Terminal.Title);
        host.Files.CurrentPath = "/etc";
        Assert.Equal("LAN · etc", host.Terminal.Title);
        host.Files.CurrentPath = "/other/etc";
        Assert.Contains("/other/etc", host.Terminal.ToolTip);

        host.Terminal.CloseFileManagerDocument();
        HeadlessAvalonia.Pump();
        Assert.Same(host.Window.FindResource("Kei.Icon.Terminal"), VisibleKindIcon(header).Data);
    });

    private static DocumentTabStripItem HeaderFor(Host host, WorkspaceDocument document)
        => host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().Single(header => ReferenceEquals(header.DataContext, document));

    private static AvaloniaPath VisibleKindIcon(DocumentTabStripItem header)
        => header.GetVisualDescendants().OfType<AvaloniaPath>().Single(icon => icon.Classes.Contains("workspaceTabKind") && icon.IsEffectivelyVisible);

    private static Color ColorFor(Host host, string resource)
        => Assert.IsAssignableFrom<ISolidColorBrush>(host.Window.FindResource(resource)).Color;
}
