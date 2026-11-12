using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views.Settings.Controls;
using Kei.Term.Core.Models;

namespace Kei.Term.Tests;

public class SettingsImportAndEditorLayoutTests
{
    [Theory]
    [InlineData(880)]
    [InlineData(980)]
    [InlineData(1280)]
    public Task TerminalImport_AlignsWithPageTitleAndStaysReachableWhileScrolling(int width)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
            context.Window.Width = width;
            context.Window.Height = 660;
            context.Model.SelectedCategory = context.Model.Categories.Single(category => category.Page is TerminalAppearanceSettingsPage);
            context.Layout();

            SettingsPageHeader header = context.Window.GetVisualDescendants().OfType<SettingsPageHeader>().Single();
            TextBlock title = header.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == header.Title && text.FontSize == 18);
            DropDownButton import = context.Window.GetVisualDescendants().OfType<DropDownButton>()
                .Single(button => button.Name == "TerminalAppearanceImportButton");
            Point titleCenter = title.TranslatePoint(new Point(0, title.Bounds.Height / 2), context.Window)!.Value;
            Point importCenter = import.TranslatePoint(new Point(0, import.Bounds.Height / 2), context.Window)!.Value;
            Assert.InRange(Math.Abs(titleCenter.Y - importCenter.Y), 0, 1);
            AssertInsideWindow(import, context.Window);

            AdaptiveColumnsGrid columns = context.Window.GetVisualDescendants().OfType<AdaptiveColumnsGrid>().Single();
            ScrollViewer settings = columns.Children.OfType<ScrollViewer>().Single();
            settings.Offset = new Vector(0, double.MaxValue);
            context.Layout();
            Assert.Equal(importCenter, import.TranslatePoint(new Point(0, import.Bounds.Height / 2), context.Window)!.Value);
            Assert.True(import.IsEffectivelyVisible);
            await CaptureAsync(context.Window, $"{width}-terminal-appearance");

            TerminalAppearanceSettingsPage page = (TerminalAppearanceSettingsPage)context.Model.SelectedCategory.Page;
            page.Appearance.DrillDownTerminalFallbackFontsCommand.Execute(null);
            context.Layout();
            Assert.False(import.IsEffectivelyVisible);
        });

    [Theory]
    [InlineData(880, "zh-CN", "添加", "编辑")]
    [InlineData(980, "zh-CN", "添加", "编辑")]
    [InlineData(1280, "en-US", "Add", "Edit")]
    public Task EditorLists_LocalizeActionsAndConstrainLongPaths(int width, string culture, string add, string edit)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            try
            {
                await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
                context.Window.Width = width;
                context.Window.Height = 660;
                var category = context.Model.Categories.Single(category => category.Page is FileManagerSettingsPage);
                FileManagerSettingsPage page = (FileManagerSettingsPage)category.Page;
                string longPath = "/Applications/" + string.Concat(Enumerable.Repeat("Long Editor Installation Path/", 12)) + "code";
                ExternalEditor editor = new() { Name = "VS Code", IsDefault = true };
                editor.Paths.Add(new ExternalEditorPath(Guid.NewGuid(), editor.Id, "any", longPath));
                page.Editors.Add(new ExternalEditorItemViewModel(editor));
                page.SelectedEditor = page.Editors[0];
                FileAssociationRule rule = new() { Pattern = "*." + new string('x', 160), EditorId = editor.Id };
                page.Associations.Add(new FileAssociationItemViewModel(rule, new string('y', 160)));
                page.SelectedAssociation = page.Associations[0];
                context.Model.SelectedCategory = category;
                context.Layout();

                foreach ((string name, string expected) in new[]
                {
                    ("AddExternalEditorButton", add), ("EditExternalEditorButton", edit),
                    ("AddFileAssociationButton", add), ("EditFileAssociationButton", edit)
                })
                {
                    Button action = context.Window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
                    Assert.Equal(expected, action.Content);
                }

                ListBox[] lists = context.Window.GetVisualDescendants().OfType<ListBox>()
                    .Where(list => list.Name is "ExternalEditorList" or "FileAssociationList").ToArray();
                Assert.Equal(2, lists.Length);
                foreach (ListBox list in lists)
                {
                    list.BringIntoView();
                    context.Layout();
                    AssertInsideWindow(list, context.Window);
                    ScrollViewer viewport = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
                    Assert.True(viewport.Extent.Width <= viewport.Viewport.Width + 1,
                        $"{list.Name}: content width {viewport.Extent.Width} exceeds viewport {viewport.Viewport.Width}");
                    ListBoxItem item = Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>());
                    AssertInsideWindow(item, context.Window);
                    foreach (TextBlock text in item.GetVisualDescendants().OfType<TextBlock>())
                        AssertInsideWindow(text, context.Window);
                    await CaptureAsync(context.Window, $"{width}-{culture}-{list.Name}");
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = previousCulture;
            }
        });

    private static void AssertInsideWindow(Control control, Window window)
    {
        Point origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0, $"{control.Name}: empty width");
        Assert.True(origin.X >= 0 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1,
            $"{control.Name}: {origin.X} + {control.Bounds.Width} exceeds window width {window.ClientSize.Width}");
    }

    private static async Task CaptureAsync(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KEITERM_SETTINGS_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(160);
        HeadlessAvalonia.Pump();
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No settings frame");
        frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
