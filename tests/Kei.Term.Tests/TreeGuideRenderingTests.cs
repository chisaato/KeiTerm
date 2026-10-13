using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Views.Controls;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Models.Profiles.BuiltIns;

namespace Kei.Term.Tests;

// 从真实模板的箭头和行位置取参照，再检查 Skia 像素；不能只验证模板里声明的偏移值。
public class TreeGuideRenderingTests
{
    [Theory]
    [InlineData(6, 18, 12)]
    [InlineData(12, 22, 14)]
    [InlineData(32, 40, 24)]
    public Task Guides_JoinParentChevronAndEveryDirectChild_AtEachDensity(double indent, double height, double iconSize) => HeadlessAvalonia.RunAsync(() =>
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
            // 末项自己已展开时，竖线要继续贯通到它子树的最末一行，而不是停在末项的行中点上。
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

    // 共享控件 SessionTreeView（模版驱动 + .sessionTree 样式作用域）必须画出同样的引导线，
    // 并用 profile 派生出来的 Kei.Tree.Line，而不是令牌里的兜底值。
    [Fact]
    public Task Guides_RenderInsideSharedSessionTreeView_WithProfileDerivedLine() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        UiDesignSystemService.ApplyTreeDensity(28, 12, 14, 16);
        var originalResources = Application.Current!.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);

        GuiProfile profile = BuiltInPresets.GetDefaultGuiProfile();
        FolderNode nested = new() { Name = "Nested" };
        nested.Children.Add(new SessionNode { Name = "Nested session" });
        FolderNode branch = new() { Name = "LAN" };
        branch.Children.Add(new SessionNode { Name = "First session" });
        branch.Children.Add(nested);
        VirtualRootNode root = new();
        root.Children.Add(branch);
        root.Children.Add(new SessionNode { Name = "Last session" });
        root.IsExpanded = true;
        branch.IsExpanded = true;
        nested.IsExpanded = true;

        SessionTreeView tree = new() { ItemsSource = new TreeNodeBase[] { root } };
        Window window = new()
        {
            Width = 440, Height = 380, Content = tree,
            Background = (IBrush)Application.Current.FindResource("Kei.Bg.Panel")!,
            FontFamily = (FontFamily)Application.Current.FindResource("Kei.Font.UI")!
        };
        try
        {
            ProfileManagerService.ApplyGuiProfile(profile);
            window.Show();
            window.MouseMove(new Point(420, 350), RawInputModifiers.None);
            HeadlessAvalonia.Pump(30);

            // 展开状态由 SessionTreeViewStyles 里的绑定负责：模版驱动的树必须真的展开。
            TreeViewItem rootContainer = tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, root));
            Assert.True(rootContainer.IsExpanded);
            Assert.Equal(
                Color.Parse(ProfileManagerService.CalculateTreeAndSubtleBorder(profile.PanelBackground, profile.BorderBrush).TreeLineHex),
                Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Tree.Line")).Color);

            AssertGuides(window, rootContainer);
            AssertGuides(window, ChildContainers(rootContainer).Single(item => ReferenceEquals(item.DataContext, branch)));
            AssertGuides(window, ChildContainers(rootContainer).SelectMany(ChildContainers)
                .Single(item => ReferenceEquals(item.DataContext, nested)));
        }
        finally
        {
            window.Close();
            UiDesignSystemService.ApplyTreeDensity(22, 12, 14, 12);
            foreach (object key in Application.Current!.Resources.Keys.ToArray())
            {
                Application.Current.Resources.Remove(key);
            }
            foreach (var resource in originalResources)
            {
                Application.Current.Resources[resource.Key] = resource.Value;
            }
        }
    });

    // 缩进下限必须覆盖真实模板的行几何（箭头槽 + 图标半径 + 字形内缩）；模板改了而常量没跟着改时这条会红。
    [Fact]
    public Task MinimumTreeIndent_CoversMeasuredRowGeometry() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        UiDesignSystemService.ApplyTreeDensity(22, 12, 14, 12);
        TreeViewItem folder = Item(new FolderNode { Name = "LAN" }, true);
        TreeViewItem root = Item(new VirtualRootNode { Name = "Sessions" }, true);
        root.Items.Add(folder);
        Window window = new() { Width = 320, Height = 200, Content = new TreeView { Items = { root } } };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Panel slot = Part<Panel>(folder, "PART_ExpandCollapseChevronContainer");
            Control glyph = Part<Avalonia.Controls.Primitives.ToggleButton>(folder, "PART_ExpandCollapseChevron");
            double slotWidth = slot.Bounds.Width + slot.Margin.Right;
            double glyphInset = glyph.TranslatePoint(default, window)!.Value.X - slot.TranslatePoint(default, window)!.Value.X;
            double iconSize = IconSize(window);
            double needed = slotWidth + iconSize / 2 + glyphInset;
            Assert.True(UiDesignSystemService.MinimumTreeIndent(iconSize) >= needed,
                $"最小缩进 {UiDesignSystemService.MinimumTreeIndent(iconSize):F1} 小于模板实测需求 {needed:F1}");

            // 用户缩进偏小时抬到最小值，够大时保留用户值。
            UiDesignSystemService.ApplyTreeDensity(22, 12, iconSize, 6);
            Assert.Equal(UiDesignSystemService.MinimumTreeIndent(iconSize), Indent(window), 2);
            UiDesignSystemService.ApplyTreeDensity(22, 12, iconSize, 48);
            Assert.Equal(48, Indent(window), 2);
        }
        finally
        {
            window.Close();
            UiDesignSystemService.ApplyTreeDensity(22, 12, 14, 12);
        }
    });

    // 直接压小 Kei.Tree.Indent（绕过最小值抬升）时，竖线退到子项箭头槽左侧，绝不压住子项箭头。
    [Fact]
    public Task SmallIndent_KeepsRailOffChildArrow() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        UiDesignSystemService.ApplyTreeDensity(22, 12, 14, 12);
        object? original = Application.Current!.Resources["Kei.Tree.Indent"];
        TreeViewItem nested = Item(new FolderNode { Name = "Nested" }, true);
        nested.Items.Add(Item(new SessionNode { Name = "Deep" }));
        TreeViewItem folder = Item(new FolderNode { Name = "LAN" }, true);
        folder.Items.Add(nested);
        folder.Items.Add(Item(new SessionNode { Name = "Session" }));
        TreeViewItem root = Item(new VirtualRootNode { Name = "Sessions" }, true);
        root.Items.Add(folder);
        Window window = new()
        {
            Width = 360, Height = 220, Content = new TreeView { Items = { root } },
            Background = (IBrush)Application.Current.FindResource("Kei.Bg.Panel")!
        };
        try
        {
            window.Show();
            Application.Current.Resources["Kei.Tree.Indent"] = 6.0;
            HeadlessAvalonia.Pump(30);

            using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
            using ILockedFramebuffer pixels = frame.Lock();
            Color line = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Tree.Line")).Color;
            // 用文件夹子项的可见箭头衡量（叶子行没有箭头字形）。
            double slotLeft = ArrowSlotLeft(window, nested);
            int row = (int)Math.Floor(RowCenter(window, nested).Y * window.RenderScaling);
            int rail = FirstLinePixel(pixels, line, row, (int)Math.Floor(slotLeft * window.RenderScaling));
            Assert.True(rail >= 0, "缩进变小时横线仍要连到竖线");
            Assert.True(rail < slotLeft, $"竖线（{rail}）越过子项箭头槽左缘（{slotLeft:F1}）");
            double glyphLeft = Part<Avalonia.Controls.Primitives.ToggleButton>(nested, "PART_ExpandCollapseChevron")
                .TranslatePoint(default, window)!.Value.X;
            Assert.True(rail <= glyphLeft - 4, $"竖线（{rail}）压住了子项箭头字形（{glyphLeft:F1}）");
        }
        finally
        {
            window.Close();
            Application.Current!.Resources["Kei.Tree.Indent"] = original;
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

    // 直接子容器按实际布局取，模版驱动（ItemsSource）与手工挂 Items 的树都能用同一套断言；叶子没有展开面板时返回空。
    private static TreeViewItem[] ChildContainers(TreeViewItem parent) => parent.GetVisualDescendants()
        .OfType<ItemsPresenter>().FirstOrDefault(presenter => ReferenceEquals(presenter.TemplatedParent, parent))?
        .Panel?.Children.OfType<TreeViewItem>()
        .Where(item => item.IsVisible && Header(item) is { Bounds.Height: > 0 })
        .ToArray() ?? [];

    private static Grid? Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Grid>()
        .FirstOrDefault(grid => grid.Name == "PART_Header" && ReferenceEquals(grid.TemplatedParent, item));

    private static void AssertGuides(Window window, TreeViewItem parent)
    {
        TreeViewItem[] children = ChildContainers(parent);
        Assert.NotEmpty(children);
        Border parentRow = Part<Border>(parent, "PART_LayoutRoot");
        Point parentBottom = parentRow.TranslatePoint(new Point(0, parentRow.Bounds.Height), window)!.Value;
        double contentLeft = StubEnd(window, children[0]);
        // 叶子行箭头隐藏但占位：横线必须穿过占位连到图标/文字，而不是停在 Header 左缘。
        foreach (TreeViewItem child in children.Where(child => !Part<Avalonia.Controls.Primitives.ToggleButton>(child, "PART_ExpandCollapseChevron").IsVisible))
        {
            Assert.True(StubEnd(window, child) > RowCenter(window, child).X + 8, "叶子行的横线应穿过隐藏箭头的占位");
        }

        // 缩进足够时竖线落在父行图标轴上（线从文件夹图标正下方引出）；缩进被手动压小时退到子项箭头槽左侧。
        double iconCenter = IconCenter(window, parent);
        double slotLeft = ArrowSlotLeft(window, children[0]);
        bool onIconAxis = Indent(window) >= UiDesignSystemService.MinimumTreeIndent(IconSize(window));
        double expectedRail = onIconAxis ? iconCenter : slotLeft - 2;
        // 子项箭头（可见字形的左缘）必须整个排在竖线右侧，任何缩进下都不重叠。
        foreach (TreeViewItem child in children)
        {
            Assert.True(ArrowSlotLeft(window, child) >= slotLeft, "同级子项的箭头槽必须对齐");
            Avalonia.Controls.Primitives.ToggleButton arrow = Part<Avalonia.Controls.Primitives.ToggleButton>(child, "PART_ExpandCollapseChevron");
            if (arrow.IsVisible)
            {
                double glyphLeft = arrow.TranslatePoint(default, window)!.Value.X;
                Assert.True(glyphLeft > expectedRail + 2, $"竖线（{expectedRail:F1}）不得压住子项箭头字形（{glyphLeft:F1}）");
            }
        }

        // 子树最末一行：末项已展开时继续下潜，直到再没有可见子项。
        TreeViewItem subtreeLast = LastRow(children[^1]);
        Point lastRow = RowCenter(window, subtreeLast);

        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
        using ILockedFramebuffer pixels = frame.Lock();
        Color line = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Tree.Line")).Color;
        int contentLeftPixel = (int)Math.Floor(contentLeft * window.RenderScaling);
        int firstRow = (int)Math.Floor(RowCenter(window, children[0]).Y * window.RenderScaling);
        // 竖线列由实绘像素反查：从子项内容左侧往左走完这条连续的横线，最左端即竖线所在列。
        int rail = FirstLinePixel(pixels, line, firstRow, contentLeftPixel);
        Assert.True(rail >= 0, "每个直接子项的行中点都应接到一条横向连接线");
        Assert.InRange(rail, (int)Math.Floor(expectedRail * window.RenderScaling) - 1, (int)Math.Floor(expectedRail * window.RenderScaling) + 1);
        Assert.NotEqual(line, ReadPixel(pixels, contentLeftPixel, firstRow));
        // 线必须比它旁边的背景亮得出来：与面板底几乎同色时（历史上令牌只有 1.13:1）肉眼等于没有线。
        Color beside = ReadPixel(pixels, rail - 2, firstRow);
        Assert.True(Contrast(line, beside) >= 1.5, $"引导线 {line} 与背景 {beside} 对比度仅 {Contrast(line, beside):F2}:1");

        foreach (TreeViewItem child in children)
        {
            Point row = RowCenter(window, child);
            int y = (int)Math.Floor(row.Y * window.RenderScaling);
            int endX = (int)Math.Floor(StubEnd(window, child) * window.RenderScaling);
            Assert.True(endX > rail, "子项内容必须排在竖线右侧");
            for (int branchX = rail; branchX < endX; branchX++)
            {
                Assert.Equal(line, ReadPixel(pixels, branchX, y));
            }
        }

        // 竖线从父行底部一路贯通到整棵子树的最末一行，末项因此收成 └ 形或继续向下延伸。
        int startY = (int)Math.Ceiling(parentBottom.Y * window.RenderScaling);
        int lastY = (int)Math.Floor(lastRow.Y * window.RenderScaling);
        Assert.True(lastY > startY);
        for (int y = startY; y <= lastY; y++)
        {
            Assert.Equal(line, ReadPixel(pixels, rail, y));
        }
        Assert.NotEqual(line, ReadPixel(pixels, rail, lastY + 2));
    }

    private static TreeViewItem LastRow(TreeViewItem item)
        => item.IsExpanded && ChildContainers(item).LastOrDefault() is { } last ? LastRow(last) : item;

    // 1px 细线是否“看得见”：用 WCAG 对比度衡量线条像素与其旁边背景像素。
    private static double Contrast(Color foreground, Color background)
    {
        double light = Math.Max(Luminance(foreground), Luminance(background));
        double dark = Math.Min(Luminance(foreground), Luminance(background));
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    // 横线终点：叶子行到内容（图标/文字）左缘，文件夹行到 Header 左缘（箭头前）。
    private static double StubEnd(Window window, TreeViewItem item)
    {
        bool leaf = !Part<Avalonia.Controls.Primitives.ToggleButton>(item, "PART_ExpandCollapseChevron").IsVisible;
        return leaf
            ? Part<ContentPresenter>(item, "PART_HeaderPresenter").TranslatePoint(new Point(0, 0), window)!.Value.X
            : RowCenter(window, item).X;
    }

    // 父行图标（SessionTreeItemView 里的 Viewbox）的水平中心：竖线要落在这里，而不是写死像素。
    private static double IconCenter(Window window, TreeViewItem item)
    {
        Viewbox icon = item.GetVisualDescendants().OfType<SessionTreeItemView>().First()
            .GetVisualDescendants().OfType<Viewbox>().First(box => box.Bounds.Width > 0);
        return icon.TranslatePoint(new Point(icon.Bounds.Width / 2, 0), window)!.Value.X;
    }

    // 子项箭头槽（叶子行箭头隐藏但槽仍占位）左缘。
    private static double ArrowSlotLeft(Window window, TreeViewItem item) =>
        Part<Panel>(item, "PART_ExpandCollapseChevronContainer").TranslatePoint(default, window)!.Value.X;

    private static double Indent(Window window) => (double)window.FindResource("Kei.Tree.Indent")!;

    private static double IconSize(Window window) => (double)window.FindResource("Kei.Tree.IconSize")!;

    private static Point RowCenter(Window window, TreeViewItem item)
    {
        Grid header = Header(item) ?? throw new InvalidOperationException("TreeViewItem has no PART_Header");
        return header.TranslatePoint(new Point(0, header.Bounds.Height / 2), window)!.Value;
    }

    private static int FirstLinePixel(ILockedFramebuffer pixels, Color line, int y, int contentLeftPixel)
    {
        int rail = contentLeftPixel - 1;
        if (rail < 0 || ReadPixel(pixels, rail, y) != line)
        {
            return -1;
        }
        while (rail > 0 && ReadPixel(pixels, rail - 1, y) == line)
        {
            rail--;
        }
        return rail;
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
