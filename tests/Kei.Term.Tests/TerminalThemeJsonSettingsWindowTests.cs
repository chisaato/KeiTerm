using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Services;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

public class TerminalThemeJsonSettingsWindowTests
{
    [Fact]
    public Task SettingsImportButton_OpensOwnedEditor_AndConfirmationAddsOnlyADraft() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeiTerm-JsonWindow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsWindow? settingsWindow = null;
        try
        {
            UiDesignSystemService.Apply();
            FixedSettingsService settings = new();
            ProfileManagerService manager = new(settings, directory);
            await manager.InitializeAsync();
            SettingsViewModel model = new(settings, directory, profileManager: manager);
            settingsWindow = new SettingsWindow(model);
            model.SelectedCategory = model.Categories.Single(category => category.Page is TerminalAppearanceSettingsPage);
            settingsWindow.Show();
            HeadlessAvalonia.Pump();

            Button action = settingsWindow.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ImportTerminalThemeJsonButton");
            action.BringIntoView();
            HeadlessAvalonia.Pump();
            Assert.Same(model.ImportTerminalThemeJsonCommand, action.Command);
            Click(settingsWindow, action);

            TerminalProfileEditWindow palette = Assert.IsType<TerminalProfileEditWindow>(Assert.Single(settingsWindow.OwnedWindows));
            TerminalProfileEditViewModel paletteDraft = Assert.IsType<TerminalProfileEditViewModel>(palette.DataContext);
            TerminalThemeJsonImportWindow editor = Assert.IsType<TerminalThemeJsonImportWindow>(Assert.Single(palette.OwnedWindows));
            TerminalThemeJsonImportViewModel draft = Assert.IsType<TerminalThemeJsonImportViewModel>(editor.DataContext);
            draft.Name = "Imported from settings";
            draft.JsonText = TerminalThemeJsonParser.ExampleJson;
            Click(editor, editor.FindControl<Button>("ValidateButton")!);
            Assert.True(editor.FindControl<Button>("ImportButton")!.IsEnabled);
            Click(editor, editor.FindControl<Button>("ImportButton")!);
            HeadlessAvalonia.WaitUntil(() => paletteDraft.Name == "Imported from settings");
            Assert.True(palette.IsVisible);
            Assert.Empty(palette.OwnedWindows);
            Assert.DoesNotContain(manager.AllTerminalProfiles, profile => !profile.IsBuiltIn);
            Assert.False(model.ImportTerminalThemeJsonCommand.ExecutionTask!.IsCompleted);

            // JSON 回写后继续使用通用调色控件；只有确认父窗口才加入设置草稿。
            Button blue = palette.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.CommandParameter, "Ansi4"));
            Click(palette, blue);
            paletteDraft.ActiveHex = "#123456";
            Click(palette, palette.FindControl<Button>("ConfirmButton")!);
            await model.ImportTerminalThemeJsonCommand.ExecutionTask!;

            Assert.Empty(settingsWindow.OwnedWindows);
            Assert.Equal("Imported from settings", manager.DefaultTerminalProfile.Name);
            Assert.Equal("#123456", manager.DefaultTerminalProfile.AnsiColors[4]);
            Assert.Single(manager.AllTerminalProfiles, profile => !profile.IsBuiltIn);
            Assert.False(File.Exists(Path.Combine(directory, "profiles.json")));

            await model.CancelCommand.ExecuteAsync(null);
            Assert.DoesNotContain(manager.AllTerminalProfiles, profile => profile.Name == "Imported from settings");
            Assert.False(settingsWindow.IsVisible);
        }
        finally
        {
            settingsWindow?.Close();
            Directory.Delete(directory, recursive: true);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task PaletteJsonButton_EditsCurrentColors_AndCancelLeavesPaletteUntouched(bool confirmJson)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeiTerm-JsonEdit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsWindow? owner = null;
        try
        {
            UiDesignSystemService.Apply();
            SettingsViewModel settings = new(new FixedSettingsService(), directory);
            owner = new SettingsWindow(settings);
            owner.Show();
            TerminalProfile source = TerminalThemeJsonParser.Parse(TerminalThemeJsonParser.ExampleJson).Profile!;
            source.Name = "Existing palette";
            source.Background = "#112233";
            Task<TerminalProfile?> completion = settings.OpenTerminalProfileEditDialogAsync!(
                source, new TerminalFontSnapshot("Menlo", [], 12, false));
            HeadlessAvalonia.Pump();
            TerminalProfileEditWindow palette = Assert.IsType<TerminalProfileEditWindow>(Assert.Single(owner.OwnedWindows));
            TerminalProfileEditViewModel paletteDraft = Assert.IsType<TerminalProfileEditViewModel>(palette.DataContext);
            Click(palette, palette.FindControl<Button>("EditJsonButton")!);
            TerminalThemeJsonImportWindow json = Assert.IsType<TerminalThemeJsonImportWindow>(Assert.Single(palette.OwnedWindows));
            TerminalThemeJsonImportViewModel draft = Assert.IsType<TerminalThemeJsonImportViewModel>(json.DataContext);
            Assert.Equal(source.Name, draft.Name);
            Assert.Equal(source.Background, TerminalThemeJsonParser.Parse(draft.JsonText).Profile!.Background);
            draft.Name = "Edited palette";
            draft.JsonText = draft.JsonText.Replace("#112233", "#334455");
            Click(json, json.FindControl<Button>("ValidateButton")!);
            Click(json, json.FindControl<Button>(confirmJson ? "ImportButton" : "CancelButton")!);
            HeadlessAvalonia.WaitUntil(() => palette.OwnedWindows.Count == 0);
            Assert.Equal(confirmJson ? "#334455" : source.Background, paletteDraft.Background);
            Assert.Equal(confirmJson ? "Edited palette" : source.Name, paletteDraft.Name);
            Assert.Equal("#112233", source.Background);
            Assert.Equal("Existing palette", source.Name);

            Click(palette, palette.FindControl<Button>("ConfirmButton")!);
            TerminalProfile result = Assert.IsType<TerminalProfile>(await completion);
            Assert.Equal(source.Id, result.Id);
            Assert.Equal(confirmJson ? "#334455" : source.Background, result.Background);
        }
        finally
        {
            owner?.Close();
            Directory.Delete(directory, recursive: true);
        }
    });

    private static void Click(Window window, Button button)
    {
        Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }
}
