using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;

namespace Kei.Term.Tests;

public class TerminalThemeJsonImportTransactionTests
{
    [Fact]
    public async Task ConfirmedJsonImport_RemainsInDraft_AndCancelRestoresCommittedColorsAndFonts()
    {
        string directory = NewDirectory();
        try
        {
            FixedSettingsService settings = new(new AppSettings { FontFamily = "User Mono", FontSize = 19 });
            ProfileManagerService manager = new(settings, directory);
            await manager.InitializeAsync();
            string committedId = manager.DefaultTerminalProfile.Id;
            SettingsViewModel model = new(settings, directory, profileManager: manager);
            ScriptedInteraction interaction = new();
            model.SetInteraction(interaction);
            TerminalProfile imported = TerminalThemeJsonParser.Parse(TerminalThemeJsonParser.ExampleJson, "Imported JSON").Profile!;
            interaction.TerminalThemeJsonResults.Enqueue(imported);

            await model.ImportTerminalThemeJsonCommand.ExecuteAsync(null);
            Assert.Equal(imported.Id, manager.DefaultTerminalProfile.Id);
            Assert.Equal(imported.AnsiColors, manager.DefaultTerminalProfile.AnsiColors);
            imported.AnsiColors[0] = "#123456";
            Assert.Equal("#000000", manager.DefaultTerminalProfile.AnsiColors[0]);
            Assert.Equal("User Mono", settings.Current.FontFamily);
            Assert.Equal(19, settings.Current.FontSize);
            ProfileManagerService beforeApply = new(settings, directory);
            await beforeApply.InitializeAsync();
            Assert.DoesNotContain(beforeApply.AllTerminalProfiles, profile => profile.Id == imported.Id);

            await model.CancelCommand.ExecuteAsync(null);
            Assert.DoesNotContain(manager.AllTerminalProfiles, profile => profile.Id == imported.Id);
            Assert.Equal(committedId, manager.DefaultTerminalProfile.Id);
            Assert.Equal("User Mono", settings.Current.FontFamily);
            Assert.Equal(19, settings.Current.FontSize);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ApplyingJsonImport_PersistsSelectedPalette_WithoutPersistingLegacyThemeFonts()
    {
        string directory = NewDirectory();
        try
        {
            FixedSettingsService settings = new(new AppSettings { FontFamily = "Original Mono", FontSize = 18 });
            ProfileManagerService manager = new(settings, directory);
            await manager.InitializeAsync();
            SettingsViewModel model = new(settings, directory, profileManager: manager);
            AppearanceSettingsPage appearance = Appearance(model);
            appearance.FontFamily = "Draft Mono";
            appearance.FontSize = 22;
            ScriptedInteraction interaction = new();
            model.SetInteraction(interaction);
            TerminalProfile imported = TerminalThemeJsonParser.Parse(TerminalThemeJsonParser.ExampleJson, "Saved JSON").Profile!;
            interaction.TerminalThemeJsonResults.Enqueue(imported);

            await model.ImportTerminalThemeJsonCommand.ExecuteAsync(null);
            Assert.Equal("Draft Mono", appearance.FontFamily);
            Assert.Equal(22, appearance.FontSize);
            Assert.Equal(imported.Id, appearance.SelectedTerminalProfile!.Id);
            Assert.True(await model.ApplyChangesAsync());

            ProfileManagerService reopened = new(settings, directory);
            await reopened.InitializeAsync();
            TerminalProfile persisted = Assert.Single(reopened.AllTerminalProfiles, profile => profile.Id == imported.Id);
            Assert.Equal("Saved JSON", persisted.Name);
            Assert.Equal(imported.Background, persisted.Background);
            Assert.Equal(imported.SelectionBackground, persisted.SelectionBackground);
            Assert.Equal(imported.AnsiColors, persisted.AnsiColors);
            Assert.Equal(imported.Id, reopened.DefaultTerminalProfile.Id);
            Assert.Equal("Draft Mono", settings.Current.FontFamily);
            Assert.Equal(22, settings.Current.FontSize);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancelledJsonDialog_DoesNotChangeAnExistingDraftSelection()
    {
        string directory = NewDirectory();
        try
        {
            FixedSettingsService settings = new();
            ProfileManagerService manager = new(settings, directory);
            await manager.InitializeAsync();
            SettingsViewModel model = new(settings, directory, profileManager: manager);
            TerminalProfile existingDraft = TerminalThemeJsonParser.Parse(TerminalThemeJsonParser.ExampleJson, "Existing draft").Profile!;
            manager.AddOrUpdateCustomTerminalProfile(existingDraft);
            manager.NotifyDefaultProfileSelectionChanged(existingDraft.Id);
            model.SetInteraction(new ScriptedInteraction());

            await model.ImportTerminalThemeJsonCommand.ExecuteAsync(null);
            Assert.Equal(existingDraft.Id, manager.DefaultTerminalProfile.Id);
            Assert.Single(manager.AllTerminalProfiles, profile => !profile.IsBuiltIn);
            Assert.False(File.Exists(Path.Combine(directory, "profiles.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static AppearanceSettingsPage Appearance(SettingsViewModel model)
        => (AppearanceSettingsPage)model.Categories.First(category => category.Page is AppearanceSettingsPage).Page;

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeiTerm-JsonImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
