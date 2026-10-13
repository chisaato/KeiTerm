using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles.BuiltIns.Gui;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class RemoteFileManagerLayoutTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(450)]
    public Task NarrowPanel_KeepsFilesVerticalAndTransferActionsReachable(int width) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        model.IsActivityPanelOpen = true;
        model.SelectedActivityTab = 1;
        model.TransferStatusMessage = "后台监视中 (16 个文件)";
        for (int index = 0; index < 16; index++)
            model.ActiveTrackedFiles.Add($"/etc/site-{index}/server-configuration-with-a-long-name.conf");
        for (int index = 0; index < 6; index++)
            model.TransferTasks.Add(new FileTransferTaskItemViewModel($"AstreaOratio-测试文件-{index}.apk", "/tmp/file.apk", "/remote/file.apk", FileTransferDirection.Upload)
            {
                State = index == 1 ? FileTransferState.Failed : FileTransferState.Transferring,
                StatusText = index == 1 ? "失败: permission denied while uploading /remote/file.apk" : "36.2% (3.0 MB/s)",
                Progress = 36.2
            });
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = width, Height = 720, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            Border panel = view.FindControl<Border>("FileActivityPanel")!;
            Assert.True(panel.IsEffectivelyVisible);
            Assert.True(panel.Bounds.Height <= view.Bounds.Height * 0.5 + 0.1);
            AssertHeaderColors(view.FindControl<TabItem>("TransferActivityTab")!);
            AssertHeaderColors(view.FindControl<TabItem>("TrackedActivityTab")!);
            AssertInsideHorizontally(view.FindControl<Button>("CollapseFileActivityButton")!, window);
            ItemsControl tracked = view.FindControl<ItemsControl>("TrackedFileList")!;
            Border[] rows = tracked.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("trackedFile")).ToArray();
            Assert.Equal(16, rows.Length);
            for (int index = 0; index < rows.Length; index++)
            {
                AssertInsideHorizontally(rows[index], window);
                if (index > 0)
                {
                    Point previous = rows[index - 1].TranslatePoint(default, window)!.Value;
                    Point current = rows[index].TranslatePoint(default, window)!.Value;
                    Assert.True(current.Y >= previous.Y + rows[index - 1].Bounds.Height, "监视文件必须纵向排列且互不重叠");
                    Assert.Equal(previous.X, current.X);
                }
                AssertInsideHorizontally(rows[index].GetVisualDescendants().OfType<Button>().Single(), window);
            }

            // 很多文件时，最后一个文件的停止按钮仍可通过列表滚动完整到达。
            ScrollViewer monitorScroll = tracked.FindAncestorOfType<ScrollViewer>()!;
            Assert.True(monitorScroll.Extent.Height > monitorScroll.Viewport.Height);
            monitorScroll.Offset = new Vector(0, double.MaxValue);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            Button lastStop = rows[^1].GetVisualDescendants().OfType<Button>().Single();
            Point stopOrigin = lastStop.TranslatePoint(default, monitorScroll)!.Value;
            Assert.True(stopOrigin.Y >= 0 && stopOrigin.Y + lastStop.Bounds.Height <= monitorScroll.Bounds.Height + 0.1);
            Assert.Same(model.StopTrackingFileCommand, lastStop.Command);
            Assert.Equal(model.ActiveTrackedFiles[^1], lastStop.CommandParameter);

            monitorScroll.Offset = default;
            window.UpdateLayout();
            await CaptureAsync(window, width, "tracked");

            Click(window, view.FindControl<TabItem>("TransferActivityTab")!);
            Assert.Equal(0, model.SelectedActivityTab);
            Assert.DoesNotContain(tracked, window.GetVisualDescendants());
            Assert.True(panel.Bounds.Height <= view.Bounds.Height * 0.5 + 0.1);
            ItemsControl transfers = view.FindControl<ItemsControl>("TransferTaskList")!;
            Border[] tasks = transfers.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("transferTask")).ToArray();
            Assert.Equal(6, tasks.Length);
            foreach (Border task in tasks)
            {
                AssertInsideHorizontally(task, window);
                TextBlock name = task.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == ((FileTransferTaskItemViewModel)task.DataContext!).FileName);
                TextBlock status = task.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("transferStatus"));
                Point nameOrigin = name.TranslatePoint(default, window)!.Value;
                Point statusOrigin = status.TranslatePoint(default, window)!.Value;
                Assert.True(statusOrigin.Y >= nameOrigin.Y + name.Bounds.Height, "任务状态必须另起一行以保留长文件名的宽度");
                Assert.True(name.Bounds.Width > 100, "窄面板也应留出可辨识文件名的宽度");
                AssertInsideHorizontally(task.GetVisualDescendants().OfType<Button>().Single(), window);
            }
            AssertInsideHorizontally(view.FindControl<Button>("ClearTransferTasksButton")!, window);
            Button clear = view.FindControl<Button>("ClearTransferTasksButton")!;
            Point clearOrigin = clear.TranslatePoint(default, panel)!.Value;
            Assert.True(clearOrigin.Y < 40, "清理操作应位于标题栏");
            TabItem monitoredTab = view.FindControl<TabItem>("TrackedActivityTab")!;
            Point tabOrigin = monitoredTab.TranslatePoint(default, window)!.Value;
            Point clearWindowOrigin = clear.TranslatePoint(default, window)!.Value;
            Assert.True(tabOrigin.X + monitoredTab.Bounds.Width <= clearWindowOrigin.X, "窄面板的标题操作不能覆盖页签");
            Assert.NotEqual(monitoredTab.Background, view.FindControl<TabItem>("TransferActivityTab")!.Background);
            Border[] badges = view.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("activityCountBadge")).ToArray();
            Assert.Equal(3, badges.Length);
            Assert.All(badges, badge => Assert.NotNull(badge.Background));
            Assert.Equal("22", view.FindControl<TextBlock>("FileActivityTotalCount")!.Text);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == model.TransferStatusMessage);
            Button toggle = view.FindControl<Button>("FileActivityToggleButton")!;
            Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == "收起面板");
            await CaptureAsync(window, width, "transfers");

            Click(window, view.FindControl<Button>("CollapseFileActivityButton")!);
            Assert.False(panel.IsVisible);
            Assert.Equal(22, model.ActivityCount);
            Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == "展开面板");
            Click(window, toggle);
            Assert.True(panel.IsVisible);
            Assert.Equal(0, model.SelectedActivityTab);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClearQueue_PreservesTrackedPanelThenHidesWhenAllActivitiesRemoved() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        model.TransferTasks.Add(new("finished", "/tmp/local", "/remote/file", FileTransferDirection.Upload) { State = FileTransferState.Completed });
        model.ActiveTrackedFiles.Add("/etc/site.conf");
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = 300, Height = 600, Content = view };
        window.Show();
        try
        {
            HeadlessAvalonia.Pump();
            Click(window, view.FindControl<Button>("ClearTransferTasksButton")!);
            Assert.Empty(model.TransferTasks);
            Assert.Equal(1, model.ActivityCount);
            Assert.Equal("1", view.FindControl<TextBlock>("FileActivityTotalCount")!.Text);
            Assert.True(model.IsActivityPanelOpen);
            Assert.Equal(1, model.SelectedActivityTab);
            Assert.True(view.FindControl<ItemsControl>("TrackedFileList")!.IsEffectivelyVisible);
            Click(window, view.FindControl<TabItem>("TransferActivityTab")!);
            Assert.True(view.FindControl<TextBlock>("NoTransfersMessage")!.IsEffectivelyVisible);
            model.ActiveTrackedFiles.Clear();
            HeadlessAvalonia.Pump();
            Assert.False(view.FindControl<Border>("FileActivityPanel")!.IsVisible);
            Assert.False(view.FindControl<Button>("FileActivityToggleButton")!.IsVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Activity_FadesTwiceThenKeepsAccentWithoutChangingSelectedTab(bool trackedActivity) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        FileTransferTaskItemViewModel transfer = new("upload", "/tmp/local", "/remote/file", FileTransferDirection.Upload);
        if (trackedActivity) model.TransferTasks.Add(transfer);
        else model.ActiveTrackedFiles.Add("/etc/site.conf");
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = 450, Height = 600, Content = view };
        window.Show();
        try
        {
            HeadlessAvalonia.Pump();
            StackPanel header = view.FindControl<StackPanel>(trackedActivity ? "TrackedActivityHeader" : "TransferActivityHeader")!;
            int dimPhases = 0;
            bool dimmed = false;
            bool intermediateOpacity = false;
            header.PropertyChanged += (_, change) =>
            {
                if (change.Property != Visual.OpacityProperty) return;
                if (header.Opacity is > 0.55 and < 0.9) intermediateOpacity = true;
                if (header.Opacity < 0.55 && !dimmed) { dimPhases++; dimmed = true; }
                else if (header.Opacity > 0.9) dimmed = false;
            };
            if (trackedActivity)
            {
                model.ActiveTrackedFiles.Add("/etc/site.conf");
                // 回写事件发出的通知驱动视图动画；回写行为另由文件编辑测试覆盖。
                model.TrackedActivityRevision++;
            }
            else model.TransferTasks.Add(transfer);
            long revision = model.TransferActivityRevision;
            transfer.Progress = 42;
            transfer.SpeedMBs = 3;
            Assert.Equal(revision, model.TransferActivityRevision);
            transfer.State = FileTransferState.Transferring;
            transfer.State = FileTransferState.Completed;
            await AdvanceAnimationAsync(1200);
            Assert.Equal(2, dimPhases);
            Assert.True(intermediateOpacity);
            Assert.Equal(1, header.Opacity);
            Assert.Equal(trackedActivity ? 0 : 1, model.SelectedActivityTab);
            Assert.True(model.HasTransferActivity);
            Assert.Equal(trackedActivity, model.HasTrackedActivity);
            TabItem tab = view.FindControl<TabItem>(trackedActivity ? "TrackedActivityTab" : "TransferActivityTab")!;
            Assert.Contains("activity", tab.Classes);
            AssertHeaderColors(tab);
            Assert.NotEqual(Brushes.Transparent, tab.Foreground);
            Click(window, view.FindControl<TabItem>("TransferActivityTab")!);
            Click(window, view.FindControl<TabItem>("TrackedActivityTab")!);
            Assert.Contains("activity", tab.Classes);
            Assert.Equal(1, header.Opacity);
            await model.DisposeAsync();
            revision = model.TransferActivityRevision;
            transfer.State = FileTransferState.Failed;
            Assert.Equal(revision, model.TransferActivityRevision);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MonitoringWithoutFileChanges_KeepsNeutralColorsAndDoesNotPulse() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        model.ActiveTrackedFiles.Add("/etc/site.conf");
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = 450, Height = 300, Content = view };
        window.Show();
        try
        {
            HeadlessAvalonia.Pump();
            TabItem monitor = view.FindControl<TabItem>("TrackedActivityTab")!;
            StackPanel header = view.FindControl<StackPanel>("TrackedActivityHeader")!;
            bool pulsed = false;
            header.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.OpacityProperty && header.Opacity < 1) pulsed = true;
            };
            Color neutral = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Text.Secondary")).Color;
            Color selected = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Text.Primary")).Color;
            Assert.Equal(selected, Assert.IsAssignableFrom<ISolidColorBrush>(monitor.Foreground).Color);
            Click(window, view.FindControl<TabItem>("TransferActivityTab")!);
            Assert.Equal(neutral, Assert.IsAssignableFrom<ISolidColorBrush>(monitor.Foreground).Color);
            model.ActiveTrackedFiles.Add("/etc/second.conf");
            model.ActiveTrackedFiles.Remove("/etc/second.conf");
            await AdvanceAnimationAsync(1200);
            Assert.False(model.HasTrackedActivity);
            Assert.DoesNotContain("activity", monitor.Classes);
            Assert.False(pulsed);
            AssertHeaderColors(monitor);
            Button toggle = view.FindControl<Button>("FileActivityToggleButton")!;
            Assert.Equal(selected, Assert.IsAssignableFrom<ISolidColorBrush>(toggle.Foreground).Color);
            await CaptureAsync(window, 450, "monitor-idle");
            Click(window, monitor);
            Assert.Equal(selected, Assert.IsAssignableFrom<ISolidColorBrush>(monitor.Foreground).Color);
            Assert.False(model.HasTrackedActivity);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ActivityColors_FollowThemeTokensWhenProfileChanges() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        var originalResources = Application.Current!.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        model.TransferTasks.Add(new("upload", "/tmp/local", "/remote/file", FileTransferDirection.Upload));
        model.ActiveTrackedFiles.Add("/etc/site.conf");
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = 450, Height = 300, Content = view };
        window.Show();
        try
        {
            TabItem transfer = view.FindControl<TabItem>("TransferActivityTab")!;
            TabItem monitor = view.FindControl<TabItem>("TrackedActivityTab")!;
            ProfileManagerService.ApplyGuiProfile(NordGuiPreset.Instance);
            HeadlessAvalonia.Pump();
            Assert.Equal(Color.Parse(NordGuiPreset.Instance.AccentColor), Assert.IsAssignableFrom<ISolidColorBrush>(transfer.Foreground).Color);
            Assert.Equal(Color.Parse(NordGuiPreset.Instance.SecondaryText), Assert.IsAssignableFrom<ISolidColorBrush>(monitor.Foreground).Color);
            model.TrackedActivityRevision++;
            HeadlessAvalonia.Pump();
            Assert.Equal(Color.Parse(NordGuiPreset.Instance.AccentColor), Assert.IsAssignableFrom<ISolidColorBrush>(monitor.Foreground).Color);

            // 通过生产主题切换入口替换 Token，已亮起的提醒也必须立即跟随新主题。
            ProfileManagerService.ApplyGuiProfile(VeritasKotamaGuiPreset.Instance);
            HeadlessAvalonia.Pump();
            Color accent = Color.Parse(VeritasKotamaGuiPreset.Instance.AccentColor);
            Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(transfer.Foreground).Color);
            Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(monitor.Foreground).Color);
            AssertHeaderColors(transfer);
            AssertHeaderColors(monitor);
        }
        finally
        {
            window.Close();
            foreach (object key in Application.Current!.Resources.Keys.ToArray()) Application.Current.Resources.Remove(key);
            foreach (var resource in originalResources) Application.Current.Resources[resource.Key] = resource.Value;
        }
    });

    [Fact]
    public Task ActivityHeight_AutomaticallyCapsHalfAndAllowsDraggingLargerThenResizing() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        for (int index = 0; index < 30; index++) model.ActiveTrackedFiles.Add($"/etc/file-{index}.conf");
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = 450, Height = 600, Content = view };
        window.Show();
        try
        {
            HeadlessAvalonia.Pump();
            Border panel = view.FindControl<Border>("FileActivityPanel")!;
            Grid layout = view.FindControl<Grid>("FileManagerLayout")!;
            GridSplitter splitter = view.FindControl<GridSplitter>("FileActivitySplitter")!;
            Assert.Equal(view.Bounds.Height * 0.5, panel.Bounds.Height, precision: 1);
            double initialHeight = panel.Bounds.Height;
            Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            Point end = start + new Vector(0, -80);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            HeadlessAvalonia.Pump();
            window.MouseUp(end, MouseButton.Left);
            HeadlessAvalonia.Pump();
            Assert.True(panel.Bounds.Height > initialHeight + 40, "手动上拖应能扩大活动栏");
            Assert.True(layout.RowDefinitions[2].ActualHeight >= 96);
            double manualHeight = panel.Bounds.Height;
            Click(window, view.FindControl<TabItem>("TransferActivityTab")!);
            Assert.Equal(manualHeight, panel.Bounds.Height, precision: 1);
            Click(window, view.FindControl<TabItem>("TrackedActivityTab")!);
            Assert.Equal(manualHeight, panel.Bounds.Height, precision: 1);
            window.Height = 450;
            HeadlessAvalonia.Pump();
            Assert.True(panel.Bounds.Height < manualHeight);
            Assert.True(layout.RowDefinitions[2].ActualHeight >= 96);
            Click(window, view.FindControl<Button>("CollapseFileActivityButton")!);
            Assert.Equal(0, layout.RowDefinitions[4].ActualHeight);
            Assert.False(splitter.IsVisible);
            Click(window, view.FindControl<Button>("FileActivityToggleButton")!);
            window.Height = 900;
            HeadlessAvalonia.Pump();
            Assert.Equal(manualHeight, panel.Bounds.Height, precision: 1);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task SwitchingActivityTabs_KeepsSharedHeightForEmptyAndPopulatedLists(int initialTab) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        LayoutFileServices services = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), services, services);
        model.ActiveTrackedFiles.Add("/root/original-ks.cfg");
        model.ActiveTrackedFiles.Add("/root/resume-compose.sh");
        model.SelectedActivityTab = initialTab;
        RemoteFileManagerView view = new() { DataContext = model };
        Window window = new() { Width = 450, Height = 600, Content = view };
        window.Show();
        try
        {
            HeadlessAvalonia.Pump();
            Border panel = view.FindControl<Border>("FileActivityPanel")!;
            double height = panel.Bounds.Height;
            double top = panel.TranslatePoint(default, window)!.Value.Y;
            for (int index = 0; index < 4; index++)
            {
                Click(window, view.FindControl<TabItem>(index % 2 == 0 ? "TransferActivityTab" : "TrackedActivityTab")!);
                Assert.Equal(height, panel.Bounds.Height, precision: 1);
                Assert.Equal(top, panel.TranslatePoint(default, window)!.Value.Y, precision: 1);
                if (model.SelectedActivityTab == 0)
                {
                    TextBlock message = view.FindControl<TextBlock>("NoTransfersMessage")!;
                    Point messageOrigin = message.TranslatePoint(default, panel)!.Value;
                    Assert.True(messageOrigin.Y >= 36 && messageOrigin.Y + message.Bounds.Height <= panel.Bounds.Height);
                }
            }
            // 在后台新增任务后，切换至长列表同样使用公共高度，内容通过滚动到达。
            for (int index = 0; index < 12; index++)
                model.TransferTasks.Add(new($"file-{index}", "/tmp/local", "/remote/file", FileTransferDirection.Upload));
            HeadlessAvalonia.Pump();
            Click(window, view.FindControl<TabItem>("TransferActivityTab")!);
            Assert.Equal(height, panel.Bounds.Height, precision: 1);
            ItemsControl tasks = view.FindControl<ItemsControl>("TransferTaskList")!;
            ScrollViewer scroll = tasks.FindAncestorOfType<ScrollViewer>()!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        }
        finally { window.Close(); }
    });

    private static void AssertHeaderColors(TabItem tab)
    {
        Color color = Assert.IsAssignableFrom<ISolidColorBrush>(tab.Foreground).Color;
        StackPanel header = Assert.IsType<StackPanel>(tab.Header);
        foreach (TextBlock text in header.GetVisualDescendants().OfType<TextBlock>())
            Assert.Equal(color, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
        foreach (Avalonia.Controls.Shapes.Path icon in header.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
            Assert.Equal(color, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);
    }

    private static void Click(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        HeadlessAvalonia.Pump();
    }

    private static async Task AdvanceAnimationAsync(int milliseconds)
    {
        for (int elapsed = 0; elapsed < milliseconds; elapsed += 16)
        {
            await Task.Delay(16);
            HeadlessAvalonia.Pump();
        }
    }

    private static async Task CaptureAsync(Window window, int width, string page)
    {
        string? directory = Environment.GetEnvironmentVariable("KEITERM_FILES_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await AdvanceAnimationAsync(1040);
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered file manager frame");
        frame.Save(Path.Combine(directory, $"file-manager-{width}-{page}.png"), PngBitmapEncoderOptions.Default);
    }

    private static void AssertInsideHorizontally(Control control, Window window)
    {
        Point origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0 && origin.X >= -0.1 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 0.1,
            $"{control.GetType().Name}: {origin.X} + {control.Bounds.Width} outside {window.ClientSize.Width}");
    }

    // 仅提供页面构造所需的服务；布局验证不触发文件传输或本地监视。
    private sealed class LayoutFileServices : IRemoteFileSystem, ILocalFileTracker
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetLocalCachePath(Guid sessionId, string remotePath) => throw new NotSupportedException();
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
