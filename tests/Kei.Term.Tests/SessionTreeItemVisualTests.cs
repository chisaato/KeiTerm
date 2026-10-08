using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Views.Controls;
using Kei.Term.Core.Models;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Kei.Term.Tests;

public class SessionTreeItemVisualTests
{
    [Fact]
    public Task NodeKindAndRename_ChangeActualIconsAndLabelVisibility() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        SessionTreeItemView view = new() { DataContext = new SessionNode { Name = "LAN", Host = "192.168.1.42" } };
        Window window = new() { Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Path folderIcon = view.GetVisualDescendants().OfType<Path>().Single(icon => icon.Classes.Contains("folderIcon"));
            Path sessionIcon = view.GetVisualDescendants().OfType<Path>().Single(icon => icon.Classes.Contains("sessionIcon"));
            TextBlock name = view.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.True(sessionIcon.IsVisible);
            Assert.False(folderIcon.IsVisible);
            view.HideName = true;
            Assert.False(name.IsVisible);
            view.HideName = false;
            Assert.True(name.IsVisible);
            view.DataContext = new FolderNode { Name = "Servers" };
            Assert.True(folderIcon.IsVisible);
            Assert.False(sessionIcon.IsVisible);
            view.DataContext = new VirtualRootNode { Name = "Sessions" };
            Assert.Equal(FontWeight.SemiBold, name.FontWeight);
        }
        finally { window.Close(); }
    });
}
