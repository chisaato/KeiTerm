using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Views.Controls;
using Kei.Term.Core.Models;

namespace Kei.Term.Tests;

// 从真实模板的箭头和行位置取参照，再检查 Skia 像素；不能只验证模板里声明的偏移值。
public class TreeGuideRenderingTests
{
    [Theory]
    [InlineData(10, 18, 12)]
    [InlineData(32, 40, 24)]
    public Task Guides_JoinParentChevronAndEveryDirectChild_AtMinAndMaxDensity(double indent, double height, double iconSize) => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        UiDesignSystemService.ApplyTreeDensity(height, 12, iconSize, indent);
        TreeViewItem nested = Item(new FolderNode { Name = "Nested" }, true);
        nested.Items.Add(Item(new SessionNode { Name = "Nested session" }));
        TreeViewItem branch = Item(new FolderNode { Name = "LAN" }, true);
        branch.Items.Add(Item(new SessionNode { Name = "First session" }));
        branch.Items.Add(nested);
        TreeViewItem root = Item(new VirtualRootNode { Name = "Sessions" }, true);
        root.Items.Add(branch);
        root.Items.Add(Item(new SessionNode { Name = "Last session" }));
        TreeView tree = new() { Items = { root } };
        Window window = new()
        {
            Width = 440, Height = 380, Content = tree,
            Background = (IBrush)Application.Current!.FindResource("Kei.Bg.Panel")!,
            FontFamily = (FontFamily)Application.Current!.FindResource("Kei.Font.UI")!
        };
        try
        {
            window.Show();
            // 排除悬停高亮覆盖连接线的情况。
            window.MouseMove(new Point(420, 350), RawInputModifiers.None);
            HeadlessAvalonia.Pump();
            AssertGuides(window, root);
            AssertGuides(window, branch);
            AssertGuides(window, nested);
            branch.IsExpanded = false;
            HeadlessAvalonia.Pump();
            AssertGuides(window, root);
            branch.IsExpanded = true;
            root.Items.RemoveAt(1);
            HeadlessAvalonia.Pump();
            // 最后一个直接子节点展开时，竖线仍停在它的行中点，不伸到孙节点下方。
            AssertGuides(window, root);
            AssertGuides(window, branch);
            window.Width = 360;
            UiDesignSystemService.ApplyTreeDensity(height + 4, 13, iconSize, indent + 2);
            HeadlessAvalonia.Pump();
            AssertGuides(window, root);
            AssertGuides(window, branch);
        }
        finally
        {
            window.Close();
            UiDesignSystemService.ApplyTreeDensity(22, 12, 14, 12);
        }
    });

    [Fact]
    public Task NodeSlots_PlaceIconBetweenChevronAndName() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        UiDesignSystemService.ApplyTreeDensity(22, 12, 16, 16);
        TreeViewItem folder = Item(new FolderNode { Name = "LAN" }, true);
        TreeViewItem session = Item(new SessionNode { Name = "root@192.168.1.42" });
        folder.Items.Add(session);
        TreeViewItem root = Item(new VirtualRootNode { Name = "Sessions" }, true);
        root.Items.Add(folder);
        Window window = new() { Width = 400, Height = 250, Content = new TreeView { Items = { root } } };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            foreach (TreeViewItem item in new[] { root, folder, session })
            {
                Control chevron = Part<Panel>(item, "PART_ExpandCollapseChevronContainer");
                SessionTreeItemView view = Assert.IsType<SessionTreeItemView>(item.Header);
                Viewbox icon = Assert.Single(view.GetVisualDescendants().OfType<Viewbox>());
                TextBlock name = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("name"));
                Point arrowStart = chevron.TranslatePoint(default, window)!.Value;
                Point iconStart = icon.TranslatePoint(default, window)!.Value;
                Point nameStart = name.TranslatePoint(default, window)!.Value;
                // 只验证顺序：图标在箭头与名称之间，不锁定模板像素
                Assert.True(arrowStart.X < iconStart.X);
                Assert.True(arrowStart.X + chevron.Bounds.Width <= iconStart.X + 1);
                Assert.True(iconStart.X + icon.Bounds.Width <= nameStart.X + 1);
            }
        }
        finally
        {
            window.Close();
            UiDesignSystemService.ApplyTreeDensity(22, 12, 14, 12);
        }
    });

    private static TreeViewItem Item(TreeNodeBase node, bool expanded = false) => new()
    {
        Header = new SessionTreeItemView { DataContext = node },
        IsExpanded = expanded
    };

    private static T Part<T>(TreeViewItem item, string name) where T : Control => item.GetVisualDescendants()
        .OfType<T>().Single(control => control.Name == name && ReferenceEquals(control.TemplatedParent, item));

    private static void AssertGuides(Window window, TreeViewItem parent)
    {
        Panel chevron = Part<Panel>(parent, "PART_ExpandCollapseChevronContainer");
        Point axis = chevron.TranslatePoint(new Point(chevron.Bounds.Width / 2, 0), window)!.Value;
        TreeViewItem[] children = parent.Items.Cast<TreeViewItem>().Where(item => item.IsVisible).ToArray();
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
        using ILockedFramebuffer pixels = frame.Lock();
        Color line = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Tree.Line")).Color;
        int x = (int)Math.Floor(axis.X * window.RenderScaling);
        foreach (TreeViewItem child in children)
        {
            Grid header = Part<Grid>(child, "PART_Header");
            Point row = header.TranslatePoint(new Point(0, header.Bounds.Height / 2), window)!.Value;
            int endX = (int)Math.Floor(row.X * window.RenderScaling);
            int y = (int)Math.Floor(row.Y * window.RenderScaling);
            Assert.True(endX > x, "Child header must follow the parent chevron axis");
            for (int branchX = x; branchX < endX; branchX++)
                Assert.Equal(line, ReadPixel(pixels, branchX, y));
        }
        Grid lastHeader = Part<Grid>(children[^1], "PART_Header");
        Point lastRow = lastHeader.TranslatePoint(new Point(0, lastHeader.Bounds.Height / 2), window)!.Value;
        Border parentRow = Part<Border>(parent, "PART_LayoutRoot");
        Point start = parentRow.TranslatePoint(new Point(0, parentRow.Bounds.Height), window)!.Value;
        int startY = (int)Math.Ceiling(start.Y * window.RenderScaling);
        int lastY = (int)Math.Floor(lastRow.Y * window.RenderScaling);
        for (int y = startY; y <= lastY; y++)
            Assert.Equal(line, ReadPixel(pixels, x, y));
        Assert.NotEqual(line, ReadPixel(pixels, x, lastY + 2));
    }

    private static Color ReadPixel(ILockedFramebuffer pixels, int x, int y)
    {
        int offset = y * pixels.RowBytes + x * 4;
        byte first = Marshal.ReadByte(pixels.Address, offset);
        byte green = Marshal.ReadByte(pixels.Address, offset + 1);
        byte third = Marshal.ReadByte(pixels.Address, offset + 2);
        byte alpha = Marshal.ReadByte(pixels.Address, offset + 3);
        Assert.True(pixels.Format == PixelFormats.Bgra8888 || pixels.Format == PixelFormats.Rgba8888);
        return pixels.Format == PixelFormats.Bgra8888 ? Color.FromArgb(alpha, third, green, first) : Color.FromArgb(alpha, first, green, third);
    }
}
