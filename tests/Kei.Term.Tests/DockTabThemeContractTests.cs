using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Xunit;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Kei.Term.Tests;

// 验证 Dock 标签栏现代美化样式、关闭按钮几何及无障碍/交互契约
public sealed class DockTabThemeContractTests
{
    [Fact]
    public Task TabCloseButton_ContractAndResources_MatchTokens() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Application app = Application.Current!;

        // 验证 KeiTokens.axaml 中新增的 Dock Tab 尺寸与几何令牌
        Assert.True(app.TryGetResource("Kei.Tab.Height", null, out object? tabHeight));
        Assert.Equal(30.0, Convert.ToDouble(tabHeight));

        Assert.True(app.TryGetResource("Kei.Tab.CloseHitSize", null, out object? hitSize));
        Assert.Equal(24.0, Convert.ToDouble(hitSize));

        Assert.True(app.TryGetResource("Kei.Tab.CloseIconSize", null, out object? iconSize));
        Assert.Equal(10.0, Convert.ToDouble(iconSize));

        Assert.True(app.TryGetResource("Kei.Tab.CloseRadius", null, out object? radiusObj));
        Assert.Equal(new CornerRadius(3), Assert.IsType<CornerRadius>(radiusObj));
    });

    [Fact]
    public Task TabControl_RendersModernCloseButton_With24HitBoxAndCharacterEllipsis() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model, Width = 1000, Height = 700 };

        try
        {
            window.Show();
            ((IConnectionHost)model).OpenTab(CreateConfig("prod-web-01-long-domain-name.internal.vpc.net"));
            ((IConnectionHost)model).OpenTab(CreateConfig("prod-web-02-secondary"));
            HeadlessAvalonia.Pump(20);

            DocumentTabStripItem[] tabItems = window.GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .ToArray();

            Assert.NotEmpty(tabItems);
            DocumentTabStripItem firstTab = tabItems[0];
            // 验证标题 TextBlock 配置了 CharacterEllipsis 尾省略及最大宽度约束
            TextBlock titleBlock = firstTab.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(tb => tb.Text?.Contains("prod-web-01") == true);

            Assert.Equal(TextTrimming.CharacterEllipsis, titleBlock.TextTrimming);
            Assert.Equal(200.0, titleBlock.MaxWidth);

            // 验证状态条高度为 14px
            Border statusBorder = firstTab.GetVisualDescendants()
                .OfType<Border>()
                .First(b => b.Width == 3.0);
            Assert.Equal(14.0, statusBorder.Height);

            // 验证关闭按钮（位于 PART_ClosePresenter 内部）
            ContentPresenter closePresenter = firstTab.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .First(cp => cp.Name == "PART_ClosePresenter");

            Button closeButton = closePresenter.GetVisualDescendants()
                .OfType<Button>()
                .First();

            Assert.Equal(24.0, closeButton.Width);
            Assert.Equal(24.0, closeButton.Height);
            Assert.NotNull(ToolTip.GetTip(closeButton));
            Assert.Equal(Strings.Get("Main.Tab.CloseTip"), ToolTip.GetTip(closeButton)?.ToString());

            Viewbox viewbox = closeButton.GetVisualDescendants()
                .OfType<Viewbox>()
                .First();
            Assert.Equal(10.0, viewbox.Width);
            Assert.Equal(10.0, viewbox.Height);

            AvaloniaPath closePath = closeButton.GetVisualDescendants()
                .OfType<AvaloniaPath>()
                .First(p => p.Name == "PART_ClosePath");

            Assert.Equal(24.0, closePath.Width);
            Assert.Equal(24.0, closePath.Height);
            // 验证常态下的描边与填充
            Assert.Null(closePath.Fill);
            Assert.Same(Application.Current!.FindResource("Kei.Icon.Close"), closePath.Data);
            Assert.Equal(Stretch.None, closePath.Stretch);

            // 验证常态 Stroke 为 Kei.Text.Muted
            IBrush? mutedBrush = Application.Current!.FindResource("Kei.Text.Muted") as IBrush;
            Assert.NotNull(mutedBrush);
            Assert.Equal(mutedBrush, closePath.Stroke);

            // 移动鼠标至按钮中心，触发实际 PointerMove 交互
            Point closeCenter = closeButton.TranslatePoint(new Point(12, 12), window)
                ?? throw new InvalidOperationException("无法获取关闭按钮的全局视口坐标");
            window.MouseMove(closeCenter, RawInputModifiers.None);
            HeadlessAvalonia.Pump(20);

            // 验证 Hover 状态下的 Stroke 与 Background
            IBrush? primaryBrush = Application.Current!.FindResource("Kei.Text.Primary") as IBrush;
            Assert.NotNull(primaryBrush);
            Assert.Equal(primaryBrush, closePath.Stroke);

            // 触发实际 MouseDown 按下
            window.MouseDown(closeCenter, MouseButton.Left, RawInputModifiers.None);
            HeadlessAvalonia.Pump(20);

            // 验证 Pressed 状态下的 Stroke 为 Error 色
            IBrush? errorBrush = Application.Current!.FindResource("Kei.Status.Error") as IBrush;
            Assert.NotNull(errorBrush);
            Assert.Equal(errorBrush, closePath.Stroke);

            // 释放鼠标按键
            window.MouseUp(closeCenter, MouseButton.Left, RawInputModifiers.None);
            HeadlessAvalonia.Pump(20);

            // 移开鼠标
            window.MouseMove(new Point(0, 0), RawInputModifiers.None);
            HeadlessAvalonia.Pump(20);
            Assert.True(closePath.Stroke == null || closePath.Stroke.Equals(mutedBrush));
        }
        finally
        {
            foreach (TerminalTabViewModel tab in model.Tabs.ToArray())
            {
                await model.CloseTabCommand.ExecuteAsync(tab);
            }
            window.Close();
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task CloseButton_FocusAndCloseTargetTab_PreservesAdjacentTab() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model, Width = 1000, Height = 700 };

        try
        {
            window.Show();
            ((IConnectionHost)model).OpenTab(CreateConfig("server-alpha"));
            ((IConnectionHost)model).OpenTab(CreateConfig("server-beta"));
            HeadlessAvalonia.Pump(20);

            Assert.Equal(2, model.Tabs.Count);
            TerminalTabViewModel tabToClose = model.Tabs[0];
            TerminalTabViewModel tabToKeep = model.Tabs[1];

            DocumentTabStripItem[] tabItems = window.GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .ToArray();

            Assert.Equal(2, tabItems.Length);
            DocumentTabStripItem firstTab = tabItems[0];

            ContentPresenter closePresenter = firstTab.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .First(cp => cp.Name == "PART_ClosePresenter");

            Button closeButton = closePresenter.GetVisualDescendants()
                .OfType<Button>()
                .First();

            // 获得焦点并验证键盘/可聚焦性
            closeButton.Focus();
            HeadlessAvalonia.Pump(20);
            Assert.True(closeButton.Focusable);

            // 通过真实的鼠标点击（MouseMove + MouseDown + MouseUp）触发关闭，而非直接调用 Command.Execute
            Point closeCenter = closeButton.TranslatePoint(new Point(12, 12), window)
                ?? throw new InvalidOperationException("无法获取关闭按钮的全局视口坐标");
            window.MouseMove(closeCenter, RawInputModifiers.None);
            window.MouseDown(closeCenter, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(closeCenter, MouseButton.Left, RawInputModifiers.None);
            HeadlessAvalonia.Pump(20);

            // 验证指定目标关闭，相邻的 server-beta 依然存活
            Assert.DoesNotContain(tabToClose, model.Tabs);
            Assert.Contains(tabToKeep, model.Tabs);
            Assert.Single(model.Tabs);
        }
        finally
        {
            foreach (TerminalTabViewModel tab in model.Tabs.ToArray())
            {
                await model.CloseTabCommand.ExecuteAsync(tab);
            }
            window.Close();
            await model.DisposeAsync();
        }
    });

    private static ResolvedSessionConfig CreateConfig(string name) => new(
        Guid.NewGuid(),
        name,
        name + ".example",
        22,
        "ops",
        null,
        "xterm-256color",
        null,
        null,
        new Dictionary<string, string>());

    private static MainViewModel CreateModel()
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = new(database);
        MainViewModel model = new(
            new SqliteTreeRepository(database),
            new SqliteIdentityRepository(database),
            vault,
            vault,
            new FixedSettingsService(),
            new SshSessionFactory());
        model.CurrentSettings.ConfirmBeforeClose = false;
        return model;
    }
}
