using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Settings;
using RoyalTerminal.Avalonia.Rendering;
using SkiaSharp;
using Xunit;

namespace Kei.Term.Tests;

public sealed class JetBrainsFontFactAttribute : FactAttribute
{
    public JetBrainsFontFactAttribute()
    {
        if (!SKFontManager.Default.FontFamilies.Contains("JetBrainsMono Nerd Font")) Skip = "未安装 JetBrainsMono Nerd Font";
    }
}

public class TerminalFontWeightTests
{
    [Theory]
    [InlineData(-1, 100)]
    [InlineData(500, 500)]
    [InlineData(950, 900)]
    public void SnapshotClampsWeight(int input, int expected) => Assert.Equal(expected, new TerminalFontSnapshot("Example", [], 11, false, input).FontWeight);

    [Fact]
    public async Task LegacySettingsRetainDefaultWeight()
    {
        string folder = CreateFolder();
        try
        {
            string path = Path.Combine(folder, "settings.json");
            await File.WriteAllTextAsync(path, "{\"FontSize\":11,\"FontFamily\":\"Example\"}");
            AppSettings loaded = await new JsonSettingsService(path).LoadSettingsAsync();
            Assert.Equal(400, loaded.TerminalFontWeight);
            Assert.Equal(11, loaded.FontSize);
        }
        finally { Directory.Delete(folder, true); }
    }

    [JetBrainsFontFact]
    public void ResolvesRealWeightsAndSharesBorrowedTypefaces()
    {
        TerminalFontSnapshot snapshot = new("JetBrainsMono Nerd Font", [], 11, false, 500);
        TerminalTypefaceCollection collection = Assert.IsType<TerminalTypefaceCollection>(TerminalTypefaceProvider.Create(snapshot));
        Assert.Equal(500, collection.GetPrimaryTypeface().FontWeight);
        Assert.Equal(800, collection.GetPrimaryTypeface(TerminalTypefaceStyle.Bold).FontWeight);
        using (SkiaTerminalRenderer renderer = SkiaTerminalRenderer.CreateWithTypefaces(collection, 11))
        {
            Assert.NotNull(renderer.GlyphCache);
            Assert.NotNull(TerminalFontMetricAdapter.TryCalculateTargetCellHeight(snapshot.FontFamily, 11, null, collection.GetPrimaryTypeface()));
        }
        TerminalTypefaceCollection second = TerminalTypefaceProvider.Create(snapshot)!;
        Assert.Same(collection.GetPrimaryTypeface(), second.GetPrimaryTypeface());
        using SKFont font = new(second.GetPrimaryTypeface(), 15);
        Assert.True(font.MeasureText("Medium") > 0);
    }

    [Fact]
    public void MissingFamilyUsesOriginalRenderingPath() => Assert.Null(TerminalTypefaceProvider.Create(new TerminalFontSnapshot("KeiTerm nonexistent font 4b3ca516", [], 11, false, 500)));

    [JetBrainsFontFact]
    public void FontSelectionChangesSnapshotAndSummary()
    {
        AppearanceSettingsPage page = new() { FontFamily = "JetBrainsMono Nerd Font", FontSize = 11 };
        page.SelectedFontWeight = page.AvailableFontWeights.Single(option => option.Weight == 500);
        Assert.Equal(500, page.DraftFontSnapshot.FontWeight);
        Assert.Equal("JetBrainsMono Nerd Font · Medium · 11pt", page.FontSummary);
        page.FontSize = 12;
        Assert.EndsWith("12pt", page.FontSummary);
        page.FontFamily = "KeiTerm nonexistent font 4b3ca516";
        Assert.Equal(page.SelectedFontWeight!.Weight, page.DraftFontSnapshot.FontWeight);
        Assert.Contains(page.SelectedFontWeight, page.AvailableFontWeights);
    }

    [JetBrainsFontFact]
    public void CanonicalizesLinuxLegacyFamilyAliases()
    {
        Assert.Equal(("JetBrainsMono Nerd Font", 500), SystemFontScanner.ResolveLegacyFamily("JetBrainsMono NF Medium"));
        Assert.DoesNotContain(SystemFontScanner.GetInstalledFonts(), option => option.Name == "JetBrainsMono NF Medium");
        Assert.Contains(500, SystemFontScanner.GetAvailableWeights("JetBrainsMono Nerd Font"));
    }

    [JetBrainsFontFact]
    public async Task ImportOnlyChangesDraftAndCancelRestoresWeight()
    {
        string folder = CreateFolder();
        try
        {
            JsonSettingsService service = new(Path.Combine(folder, "settings.json"));
            await service.LoadSettingsAsync();
            service.Current.FontFamily = "JetBrainsMono Nerd Font";
            service.Current.FontSize = 14;
            service.Current.TerminalFontWeight = 400;
            ProfileManagerService manager = new(service, folder);
            await manager.InitializeAsync();
            string palettesBefore = System.Text.Json.JsonSerializer.Serialize(manager.AllTerminalProfiles);
            string selectedBefore = manager.DefaultTerminalProfile.Id;
            using SettingsViewModel vm = new(service, folder, profileManager: manager);
            ScriptedInteraction interaction = new();
            vm.SetInteraction(interaction);
            string profilePath = Path.Combine(folder, "konsole.profile");
            await File.WriteAllTextAsync(profilePath, "[Appearance]\nFont=JetBrainsMono NF Medium,11,-1,5,57,0,0,0,0,0,Regular\nColorScheme=DefinitelyNotTheCurrentScheme");
            interaction.KonsoleProfilePaths.Enqueue(profilePath);
            await vm.ImportKonsoleFontCommand.ExecuteAsync(null);
            AppearanceSettingsPage page = vm.Categories.Select(category => category.Page).OfType<AppearanceSettingsPage>().Single();
            Assert.Equal(500, page.DraftFontSnapshot.FontWeight);
            Assert.Equal(11, page.FontSize);
            Assert.Equal("JetBrainsMono Nerd Font", page.FontFamily);
            Assert.Equal(400, service.Current.TerminalFontWeight);
            Assert.Equal(palettesBefore, System.Text.Json.JsonSerializer.Serialize(manager.AllTerminalProfiles));
            Assert.Equal(selectedBefore, manager.DefaultTerminalProfile.Id);
            Assert.Contains("Medium", Assert.Single(interaction.Notifications).Message);
            await vm.CancelCommand.ExecuteAsync(null);
            Assert.Equal(400, page.DraftFontSnapshot.FontWeight);
            Assert.Equal(14, page.FontSize);
            interaction.KonsoleProfilePaths.Enqueue(profilePath);
            await vm.ImportKonsoleFontCommand.ExecuteAsync(null);
            await vm.ApplyCommand.ExecuteAsync(null);
            AppSettings persisted = await new JsonSettingsService(Path.Combine(folder, "settings.json")).LoadSettingsAsync();
            Assert.Equal(500, persisted.TerminalFontWeight);
            Assert.Equal(11, persisted.FontSize);
            page.FontWeight = 400;
            await File.WriteAllTextAsync(profilePath, "[Appearance]\nFont=JetBrainsMono NF,11,-1,5,57,0,0,0,0,0,Regular");
            interaction.KonsoleProfilePaths.Enqueue(profilePath);
            await vm.ImportKonsoleFontCommand.ExecuteAsync(null);
            Assert.Equal(500, page.FontWeight);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string CreateFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "keiterm-font-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
