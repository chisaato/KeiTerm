namespace Kei.Term.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Infrastructure.Settings;

public class SettingsUiPreviewTests
{
    [Fact]
    public Task FontPreview_TracksDraftFontAndFallbackOrder_WithoutSaving() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_preview_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            string original = service.Current.UiFontFamily;
            SettingsViewModel model = new(service, directory);
            SettingsCategoryItem category = model.Categories.Single(c => c.Page is AppearanceSettingsPage);
            AppearanceSettingsPage page = (AppearanceSettingsPage)category.Page;
            model.SelectedCategory = category;
            window = new SettingsWindow(model);
            window.Show();
            window.UpdateLayout();
            TextBlock preview = window.GetVisualDescendants().OfType<TextBlock>().Single(c =>
                c.Text == Strings.Get("Settings.Appearance.UiFontPreviewSample"));
            Expander advanced = window.GetVisualDescendants().OfType<Expander>().Single();
            Assert.False(advanced.IsExpanded);
            ToggleButton header = advanced.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "ExpanderHeader");
            Assert.InRange(header.Bounds.Height, 28, 32);
            page.SelectedUiFont = new FontFamilyOption("DejaVu Sans", false);
            page.UiFallbackFonts.Clear();
            page.UiFallbackFonts.Add("Arial");
            page.UiFallbackFonts.Add("Georgia");
            Assert.Equal(new[] { "DejaVu Sans", "Arial", "Georgia", "sans-serif" }, preview.FontFamily.FamilyNames);
            page.UiFallbackFonts.Move(1, 0);
            Assert.Equal(new[] { "DejaVu Sans", "Georgia", "Arial", "sans-serif" }, preview.FontFamily.FamilyNames);
            page.UiFallbackFonts.Remove("Arial");
            Assert.DoesNotContain("Arial", preview.FontFamily.FamilyNames);
            Assert.Equal(original, service.Current.UiFontFamily);
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });
}
