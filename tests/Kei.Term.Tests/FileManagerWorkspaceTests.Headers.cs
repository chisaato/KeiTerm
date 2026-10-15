using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Workspaces;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Kei.Term.Tests;

public partial class FileManagerWorkspaceTests
{
    [MonospaceFact]
    public Task SavedTemplate_UpdatesAlreadyOpenDocuments_AndClearingRestoresDefault() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel files = Promote(host);
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keiterm_title_live_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            SettingsViewModel settings = new(new FixedSettingsService(host.Model.CurrentSettings), directory);
            TerminalSettingsPage page = settings.Categories.Select(category => category.Page)
                .OfType<TabBarSettingsProxyPage>().Single().Terminal;
            page.TabTitleTemplate = "{local_name}: {remote_path}";
            Assert.True(await settings.ApplyChangesAsync());
            Assert.Equal("LAN: —", host.Terminal.Title);
            Assert.Equal("LAN: /var/log", files.Title);
            Assert.Equal(files.Title, host.Model.Workspace.FindDocument(files)!.Title);
            page.TabTitleTemplate = string.Empty;
            Assert.True(await settings.ApplyChangesAsync());
            Assert.Equal("LAN", host.Terminal.Title);
            Assert.Equal("LAN · log", files.Title);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    });

    [MonospaceFact]
    public Task RemoteIp_RequiresConnectedNumericTarget_AndNeverDisplaysDomainAsIp() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        Core.Models.ResolvedSessionConfig config = new(System.Guid.NewGuid(), "LAN", "203.0.113.5", 22, "ops", null,
            "xterm-256color", null, null, new System.Collections.Generic.Dictionary<string, string>(), TabTitleTemplate: "{remote_ip}");
        host.Terminal.BindConfig(config);
        Assert.Equal("203.0.113.5", host.Terminal.Title);
        host.Terminal.ApplyTitleTemplate("{local_host} | {remote_ip}");
        host.Terminal.BindConfig(config);
        Assert.Equal("203.0.113.5 | 203.0.113.5", host.Terminal.Title);
        host.Terminal.ApplyTitleTemplate("{remote_ip}");
        host.Terminal.State = ConnectionState.Connecting;
        Assert.Equal("—", host.Terminal.Title);
        host.Terminal.State = ConnectionState.Connected;
        host.Terminal.BindConfig(config with { Host = "example.com" });
        Assert.Equal("—", host.Terminal.Title);
    });

    [MonospaceFact]
    public Task Template_TracksRemoteReportsAndFileNavigation_AndManualRenameWins() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        FileManagerTabViewModel files = Promote(host);
        host.Terminal.ApplyTitleTemplate("{local_name} | {remote_hostname} | {remote_path}");
        Assert.Equal("LAN | — | —", host.Terminal.Title);
        Assert.Equal("LAN | — | /var/log", files.Title);
        host.Terminal.Terminal.WriteOutput(System.Text.Encoding.UTF8.GetBytes("\u001b]7;file://server/home/ops\u0007"));
        HeadlessAvalonia.Pump();
        Assert.Equal("LAN | server | /home/ops", host.Terminal.Title);
        Assert.Equal("LAN | server | /var/log", files.Title);
        host.Files.CurrentPath = "/etc";
        Assert.Equal("LAN | server | /etc", files.Title);
        host.Terminal.Rename("manual");
        host.Terminal.ApplyTitleTemplate("{remote_path}");
        Assert.Equal("manual", host.Terminal.Title);
        Assert.Equal("/etc", files.Title);
    });

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
