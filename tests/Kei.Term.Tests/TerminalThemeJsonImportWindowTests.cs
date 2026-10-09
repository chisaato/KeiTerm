using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public sealed class TerminalThemeJsonImportWindowTests
{
    [Fact]
    public Task EditorAndButtons_ValidateCurrentDraft_AndImportReturnedProfile() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalThemeJsonImportViewModel draft = new();
        Window owner = new();
        TerminalThemeJsonImportWindow dialog = new(draft);
        try
        {
            owner.Show();
            Task<TerminalProfile?> completion = dialog.ShowDialog<TerminalProfile?>(owner);
            HeadlessAvalonia.Pump();
            Assert.False(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            Click(dialog, "LoadExampleButton");
            Assert.True(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            dialog.FindControl<TextBox>("ThemeNameTextBox")!.Text = "Headless import";
            HeadlessAvalonia.Pump();
            Assert.False(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            Click(dialog, "ValidateButton");
            Assert.True(dialog.FindControl<Button>("ImportButton")!.IsEnabled);

            TextBox editor = dialog.FindControl<TextBox>("JsonEditor")!;
            editor.Text = "{\"schemaVersion\":1}";
            HeadlessAvalonia.Pump();
            Assert.False(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            Click(dialog, "ValidateButton");
            Assert.True(draft.HasErrors);
            Assert.False(completion.IsCompleted);
            // 即使请求确认，当前输入不合法也必须保留对话框。
            dialog.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(completion.IsCompleted);
            editor.Text = TerminalThemeJsonParser.ExampleJson;
            HeadlessAvalonia.Pump();
            Click(dialog, "FormatButton");
            Assert.True(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            Click(dialog, "ImportButton");

            TerminalProfile imported = Assert.IsType<TerminalProfile>(await completion);
            Assert.Equal("Headless import", imported.Name);
            Assert.Equal(draft.PreviewProfile!.AnsiColors, imported.AnsiColors);
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task CopyPromptButton_WritesSchemaAndNameToClipboard_WithoutClosing() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalThemeJsonImportViewModel draft = new() { Name = "Prompt theme" };
        Window owner = new();
        TerminalThemeJsonImportWindow dialog = new(draft);
        try
        {
            owner.Show();
            Task<TerminalProfile?> completion = dialog.ShowDialog<TerminalProfile?>(owner);
            HeadlessAvalonia.Pump();
            Click(dialog, "CopyPromptButton");
            HeadlessAvalonia.WaitUntil(() => draft.HasClipboardFeedback);

            string? copied = await dialog.Clipboard!.TryGetTextAsync();
            Assert.Contains("Prompt theme", copied);
            Assert.Contains("schemaVersion", copied);
            Assert.Contains("selectionBackground", copied);
            Assert.Contains("ansi", copied);
            Assert.True(dialog.FindControl<TextBlock>("ClipboardFeedbackText")!.IsVisible);
            Assert.False(completion.IsCompleted);
            Click(dialog, "CancelButton");
            Assert.Null(await completion);
            Assert.Null(draft.Result);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task EscapeOrWindowClose_ReturnsNull(bool escape) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalThemeJsonImportViewModel draft = new();
        draft.LoadExample();
        Window owner = new();
        TerminalThemeJsonImportWindow dialog = new(draft);
        try
        {
            owner.Show();
            Task<TerminalProfile?> completion = dialog.ShowDialog<TerminalProfile?>(owner);
            HeadlessAvalonia.Pump();
            if (escape) dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            else dialog.Close();

            Assert.Null(await completion);
            Assert.Null(draft.Result);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task FocusedInputAndDisabledImport_PreserveReadableThemeForeground() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        ProfileManagerService.ApplyGuiProfile(BuiltInPresets.GetDefaultGuiProfile());
        TerminalThemeJsonImportViewModel draft = new();
        Window owner = new();
        TerminalThemeJsonImportWindow dialog = new(draft);
        try
        {
            owner.Show();
            _ = dialog.ShowDialog<TerminalProfile?>(owner);
            HeadlessAvalonia.Pump();
            TextBox name = dialog.FindControl<TextBox>("ThemeNameTextBox")!;
            Assert.True(name.Focus());
            HeadlessAvalonia.Pump();
            TextBlock[] placeholders = name.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Text == name.PlaceholderText && block.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(placeholders);
            Border nameFrame = name.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement");
            // Fluent 同时保留普通与浮动占位标签；检查所有可见标签，而非依赖模板只有一个。
            foreach (TextBlock placeholder in placeholders)
                AssertReadableForeground(dialog, placeholder.Foreground, nameFrame.Background, "Kei.Text.Muted", 3);

            name.Text = "Readable theme";
            HeadlessAvalonia.Pump();
            TextPresenter namePresenter = name.GetVisualDescendants().OfType<TextPresenter>().Single();
            AssertReadableForeground(dialog, namePresenter.Foreground, nameFrame.Background, "Kei.Text.Primary", 4.5);

            draft.LoadExample();
            TextBox editor = dialog.FindControl<TextBox>("JsonEditor")!;
            Assert.True(editor.Focus());
            HeadlessAvalonia.Pump();
            TextPresenter editorPresenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
            Border editorFrame = editor.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement");
            AssertReadableForeground(dialog, editorPresenter.Foreground, editorFrame.Background, "Kei.Text.Primary", 4.5);

            // 改动后导入进入 disabled 状态，真实模板仍须显示可读文字。
            editor.Text = "{invalid}";
            HeadlessAvalonia.Pump();
            Button import = dialog.FindControl<Button>("ImportButton")!;
            Assert.False(import.IsEnabled);
            ContentPresenter importPresenter = import.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(presenter => ReferenceEquals(presenter.TemplatedParent, import));
            AssertReadableForeground(dialog, importPresenter.Foreground, importPresenter.Background, "Kei.Text.Muted", 3);

            Button validate = dialog.FindControl<Button>("ValidateButton")!;
            Point center = validate.TranslatePoint(new Point(validate.Bounds.Width / 2, validate.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(center, RawInputModifiers.None);
            HeadlessAvalonia.Pump();
            ContentPresenter validatePresenter = validate.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(presenter => ReferenceEquals(presenter.TemplatedParent, validate));
            AssertReadableForeground(dialog, validatePresenter.Foreground, validatePresenter.Background, "Kei.Text.Primary", 4.5);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    private static void AssertReadableForeground(Window dialog, IBrush? foreground, IBrush? background, string key, double minimumContrast)
    {
        Color actual = Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color;
        Color surface = Assert.IsAssignableFrom<ISolidColorBrush>(background).Color;
        Color expected = Assert.IsAssignableFrom<ISolidColorBrush>(dialog.FindResource(key)).Color;
        Assert.Equal(expected, actual);
        double contrast = (Math.Max(Luminance(actual), Luminance(surface)) + 0.05) /
                          (Math.Min(Luminance(actual), Luminance(surface)) + 0.05);
        Assert.True(contrast >= minimumContrast, $"Text contrast {contrast:F2} is below {minimumContrast} on {surface}");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double component = channel / 255d;
            return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(820, 640)]
    public Task ValidAndInvalidDrafts_KeepEditorAndActionsReachable_AtMinimumAndDefaultSize(int width, int height)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalThemeJsonImportViewModel draft = new();
        Window owner = new();
        TerminalThemeJsonImportWindow dialog = new(draft) { Width = width, Height = height };
        try
        {
            owner.Show();
            _ = dialog.ShowDialog<TerminalProfile?>(owner);
            draft.LoadExample();
            HeadlessAvalonia.Pump();
            AssertImportLayout(dialog);
            await CapturePreviewAsync(dialog, $"{width}x{height}-valid.png");

            dialog.FindControl<TextBox>("JsonEditor")!.Text = "{\"schemaVersion\":1,\"name\":\"Broken\",\"colors\":{\"background\":\"red\",\"extra\":true}}";
            HeadlessAvalonia.Pump();
            Click(dialog, "ValidateButton");
            Assert.True(dialog.FindControl<ItemsControl>("ValidationIssues")!.IsEffectivelyVisible);
            Assert.False(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            AssertImportLayout(dialog);
            await CapturePreviewAsync(dialog, $"{width}x{height}-invalid.png");
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    private static void AssertImportLayout(TerminalThemeJsonImportWindow dialog)
    {
        dialog.UpdateLayout();
        foreach (string name in new[] { "ThemeNameTextBox", "JsonEditor", "CopyPromptButton", "LoadExampleButton", "FormatButton", "ValidateButton", "CancelButton", "ImportButton", "ValidationSummaryText" })
        {
            Control control = dialog.FindControl<Control>(name)!;
            Assert.True(control.IsEffectivelyVisible, $"{name} must remain visible");
            Point origin = control.TranslatePoint(default, dialog)!.Value;
            Assert.True(origin.X >= -0.1 && origin.Y >= -0.1 &&
                        origin.X + control.Bounds.Width <= dialog.ClientSize.Width + 0.1 &&
                        origin.Y + control.Bounds.Height <= dialog.ClientSize.Height + 0.1,
                $"{name} falls outside the {dialog.ClientSize} client area: {origin} / {control.Bounds}");
        }

        // 名称与操作在最小窗口内都可见，编辑区不与校验结果及底栏重叠。
        Grid toolbar = dialog.FindControl<Grid>("JsonToolbar")!;
        foreach (string name in new[] { "LoadExampleButton", "FormatButton", "ValidateButton" })
        {
            Button action = dialog.FindControl<Button>(name)!;
            Point origin = action.TranslatePoint(default, toolbar)!.Value;
            Assert.True(origin.X >= -0.1 && origin.X + action.Bounds.Width <= toolbar.Bounds.Width + 0.1,
                $"{name} falls outside its toolbar");
        }

        TextBox editor = dialog.FindControl<TextBox>("JsonEditor")!;
        Point editorOrigin = editor.TranslatePoint(default, dialog)!.Value;
        TextBlock summary = dialog.FindControl<TextBlock>("ValidationSummaryText")!;
        Point summaryOrigin = summary.TranslatePoint(default, dialog)!.Value;
        Assert.True(editorOrigin.Y + editor.Bounds.Height < summaryOrigin.Y, "Editor and validation must not overlap");
        Assert.True(editor.Bounds.Width >= dialog.ClientSize.Width - 34, "JSON should use the dialog width");
        Assert.True(editor.Bounds.Height >= 160, "Validation errors must leave room to edit multiline JSON");
    }

    private static async Task CapturePreviewAsync(Window dialog, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("KEITERM_JSON_IMPORT_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(160);
        HeadlessAvalonia.Pump();
        using WriteableBitmap frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered JSON import frame");
        frame.Save(Path.Combine(directory, fileName), PngBitmapEncoderOptions.Default);
    }

    private static void Click(Window window, string name)
    {
        Button button = window.FindControl<Button>(name)!;
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center, RawInputModifiers.None);
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }
}
