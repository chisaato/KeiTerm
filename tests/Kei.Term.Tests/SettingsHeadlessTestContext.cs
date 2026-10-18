using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;

namespace Kei.Term.Tests;

// 只管理测试窗口和临时数据库；布局、绑定、交互服务及仓储均使用生产实现。
internal sealed class SettingsHeadlessTestContext : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "keiterm_settings_interactions_" + Guid.NewGuid().ToString("N"));
    public string Connection => $"Data Source={Path.Combine(DirectoryPath, "settings.db")}";
    public SettingsViewModel Model { get; private set; } = null!;
    public SettingsWindow Window { get; private set; } = null!;
    public SqliteProxyRepository Proxies { get; private set; } = null!;

    public static async Task<SettingsHeadlessTestContext> CreateAsync(IVaultManager? vault = null)
    {
        SettingsHeadlessTestContext context = new();
        Directory.CreateDirectory(context.DirectoryPath);
        SqliteTreeRepository tree = new(context.Connection);
        await tree.InitializeAsync();
        context.Proxies = new SqliteProxyRepository(context.Connection);
        context.Model = new SettingsViewModel(new FixedSettingsService(), context.DirectoryPath, proxyRepo: context.Proxies, vault: vault);
        UiDesignSystemService.Apply();
        context.Window = new SettingsWindow(context.Model) { Width = 1280, Height = 900 };
        context.Window.Show();
        await context.Model.ProxyPage!.ReloadAsync();
        context.Layout();
        return context;
    }

    public void Layout()
    {
        Window.UpdateLayout();
        HeadlessAvalonia.Pump();
    }

    public static void AssertVisible(Control control)
    {
        control.BringIntoView();
        HeadlessAvalonia.Pump();
        (TopLevel.GetTopLevel(control) as Window)?.UpdateLayout();
        Assert.True(control.IsEffectivelyVisible, $"{control}: 不可见");
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{control}: 尺寸为 {control.Bounds}");
    }

    public static void Click(Window window, Button button)
    {
        AssertVisible(button);
        // 下钻行可能位于长页底部：将按钮滚至视口中央，避免中心落入底部操作栏的裁剪区域。
        foreach (ScrollViewer scroll in button.GetVisualAncestors().OfType<ScrollViewer>())
        {
            Point position = button.TranslatePoint(default, scroll)!.Value;
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + position.Y - scroll.Viewport.Height / 2);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
        }
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Assert.True(new Rect(window.ClientSize).Contains(center), $"按钮 {button} 中心 {center} 不在窗口 {window.ClientSize} 内");
        Assert.True(window.InputHitTest(center) is Visual hit && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button)),
            $"按钮 {button} 中心 {center} 命中 {window.InputHitTest(center)}");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        HeadlessAvalonia.Pump();
    }

    public T Dialog<T>() where T : Window
    {
        HeadlessAvalonia.Pump();
        T dialog = Assert.Single(Window.OwnedWindows.OfType<T>());
        dialog.UpdateLayout();
        HeadlessAvalonia.Pump();
        Assert.True(dialog.IsVisible);
        return dialog;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Window child in Window.OwnedWindows.ToArray()) child.Close();
        await Model.CancelCommand.ExecuteAsync(null);
        HeadlessAvalonia.Pump();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
