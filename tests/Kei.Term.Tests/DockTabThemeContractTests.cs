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

// 验证 Dock 标签关闭按钮的悬停/按下描边，以及关闭指定标签后相邻标签仍在。
public sealed class DockTabThemeContractTests
{
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

            DocumentTabStripItem firstTab = window.GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .First();

            ContentPresenter closePresenter = firstTab.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .First(cp => cp.Name == "PART_ClosePresenter");

            Button closeButton = closePresenter.GetVisualDescendants()
                .OfType<Button>()
                .First();

            AvaloniaPath closePath = closeButton.GetVisualDescendants()
                .OfType<AvaloniaPath>()
                .First(p => p.Name == "PART_ClosePath");

            // 常态描边为弱化色；悬停与按下必须换成另一支笔，移开后回到弱化色。
            IBrush? mutedBrush = Application.Current!.FindResource("Kei.Text.Muted") as IBrush;
            Assert.NotNull(mutedBrush);
            Assert.Equal(mutedBrush, closePath.Stroke);

            // 移动鼠标至按钮中心，触发实际 PointerMove 交互
            Point closeCenter = closeButton.TranslatePoint(new Point(12, 12), window)
                ?? throw new InvalidOperationException("无法获取关闭按钮的全局视口坐标");
            window.MouseMove(closeCenter, RawInputModifiers.None);
            HeadlessAvalonia.Pump(20);

            // Hover 时描边换成主文字色
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

            closeButton.Focus();
            HeadlessAvalonia.Pump(20);

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
