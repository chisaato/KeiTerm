using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;

namespace Kei.Term.Tests;

public class SettingsLayoutTests
{
    [Theory]
    [InlineData(880)]
    [InlineData(980)]
    public Task SettingsPages_AtMinimumAndDefaultWidth_KeepInputsInsideContentAndActionsReachable(int width)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_settings_layout_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            string connection = $"Data Source={Path.Combine(directory, "layout.db")}";
            SqliteTreeRepository tree = new(connection);
            await tree.InitializeAsync();
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            SettingsViewModel model = new(service, directory, proxyRepo: new SqliteProxyRepository(connection));
            window = new SettingsWindow(model) { Width = width, Height = 660 };
            window.Show();
            foreach (SettingsCategoryItem category in model.Categories)
            {
                model.SelectedCategory = category;
                window.UpdateLayout();
                HeadlessAvalonia.Pump();
                // 以真实测量后的控件边界捕获窄窗口横向截断，覆盖全部分类模板。
                foreach (Control input in window.GetVisualDescendants().OfType<Control>().Where(control =>
                    (control is ComboBox or AutoCompleteBox or NumericUpDown or TextBox) && control.TemplatedParent == null && control.Bounds.Width > 0))
                {
                    Point origin = input.TranslatePoint(default, window)!.Value;
                    Assert.True(origin.X >= 189 && origin.X + input.Bounds.Width <= window.ClientSize.Width + 0.1,
                        $"{category.Page.GetType().Name}/{input.GetType().Name}/{input.Name}: {origin.X} + {input.Bounds.Width} outside {window.ClientSize.Width}");
                    Assert.True(input.Bounds.Height >= 27, $"{category.Page.GetType().Name}/{input.Name}: input height {input.Bounds.Height}");
                }
                foreach (Button action in window.GetVisualDescendants().OfType<Button>().Where(button =>
                    button.Command == model.SaveCommand || button.Command == model.ApplyCommand || button.Command == model.CancelCommand))
                {
                    Point origin = action.TranslatePoint(default, window)!.Value;
                    Assert.True(origin.Y + action.Bounds.Height <= window.ClientSize.Height + 0.1);
                    Assert.True(action.IsEffectivelyVisible);
                }
                // 只在人工视觉审查时导出真实 Skia 渲染，不改变断言或生产行为。
                string? screenshotDirectory = Environment.GetEnvironmentVariable("KEITERM_SETTINGS_SCREENSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(screenshotDirectory))
                {
                    Directory.CreateDirectory(screenshotDirectory);
                    await Task.Delay(160);
                    HeadlessAvalonia.Pump();
                    using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered settings frame");
                    frame.Save(Path.Combine(screenshotDirectory, $"{width}-{category.Page.GetType().Name}.png"), PngBitmapEncoderOptions.Default);
                    if (category.Page is FileTransferSettingsPage or SshSettingsPage)
                    {
                        foreach (Expander advanced in window.GetVisualDescendants().OfType<Expander>().ToArray()) advanced.IsExpanded = true;
                        ScrollViewer content = window.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is ContentControl);
                        window.UpdateLayout();
                        content.Offset = new Vector(0, double.MaxValue);
                        await Task.Delay(160);
                        HeadlessAvalonia.Pump();
                        using WriteableBitmap bottom = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered settings frame");
                        bottom.Save(Path.Combine(screenshotDirectory, $"{width}-{category.Page.GetType().Name}-bottom.png"), PngBitmapEncoderOptions.Default);
                        content.Offset = default;
                    }
                }
            }
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });
}
