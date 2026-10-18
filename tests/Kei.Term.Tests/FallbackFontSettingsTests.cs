using System.Text.Json;
using Avalonia.Media;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using RoyalTerminal.Avalonia.Rendering;
using SkiaSharp;

namespace Kei.Term.Tests;

public sealed class FallbackFontsFactAttribute : FactAttribute
{
    public FallbackFontsFactAttribute()
    {
        string[] required = ["JetBrainsMono Nerd Font", "Noto Sans CJK SC"];
        if (required.Any(family => !SKFontManager.Default.FontFamilies.Contains(family)))
            Skip = "需要安装 JetBrainsMono Nerd Font 与 Noto Sans CJK SC";
    }
}

public class FallbackFontSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"TerminalFallbackFonts\":[]")]
    public async Task LegacyStringMigratesAndSavingWritesOnlyNewList(string listJson)
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"FontSize\":17,\"TerminalFallbackFontFamily\":\" First , Second, First \"" + listJson + "}");
            JsonSettingsService service = new(path);
            AppSettings settings = await service.LoadSettingsAsync();
            Assert.Equal(new[] { "First", "Second" }, settings.TerminalFallbackFonts.Select(font => font.Family));
            Assert.All(settings.TerminalFallbackFonts, font => Assert.Null(font.Weight));
            Assert.Equal(17, settings.FontSize);
            await service.SaveSettingsAsync(settings);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(json.RootElement.TryGetProperty("TerminalFallbackFontFamily", out _));
            Assert.Equal(2, json.RootElement.GetProperty("TerminalFallbackFonts").GetArrayLength());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewListTakesPrecedenceAndEmptyListSurvivesRoundTrip()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""
            {"TerminalFallbackFontFamily":"Old","TerminalFallbackFonts":[{"Family":"New","Weight":500}]}
            """)!;
        Assert.Equal(new FallbackFontSetting { Family = "New", Weight = 500 }, Assert.Single(settings.TerminalFallbackFonts));
        settings.TerminalFallbackFonts.Clear();
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Empty(restored.TerminalFallbackFonts);
    }

    [FallbackFontsFact]
    public void EntryCommandsWeightsAndSummaryUpdateDraftSnapshot()
    {
        AppearanceSettingsPage page = new() { FontFamily = "JetBrainsMono Nerd Font", FontWeight = 400 };
        int notifications = 0;
        page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(page.DraftFontSnapshot)) notifications++; };
        page.TerminalFallbackFontCandidate = page.AvailableFonts.Single(font => font.Name == "Noto Sans CJK SC");
        page.AddTerminalFallbackFont();
        FallbackFontEntry first = Assert.Single(page.TerminalFallbackFonts);
        Assert.Equal(0, first.AvailableWeights[0].Weight);
        Assert.False(first.IsMissing);
        Assert.False(first.MoveUpCommand.CanExecute(null));
        Assert.False(first.MoveDownCommand.CanExecute(null));
        Assert.Equal("Noto Sans CJK SC", page.TerminalFallbackFontsSummary);
        Assert.Null(Assert.Single(page.DraftFontSnapshot.FallbackFonts).Weight);
        first.SelectedWeight = first.AvailableWeights.Single(option => option.Weight == 500);
        Assert.Equal(500, Assert.Single(page.DraftFontSnapshot.FallbackFonts).Weight);
        Assert.Equal("Noto Sans CJK SC · Medium", page.TerminalFallbackFontsSummary);
        Assert.Equal((FontWeight)500, first.PreviewFontWeight);

        page.TerminalFallbackFontCandidate = page.AvailableFonts.Single(font => font.Name == "JetBrainsMono Nerd Font");
        page.AddTerminalFallbackFont();
        FallbackFontEntry second = page.TerminalFallbackFonts[1];
        Assert.True(first.MoveDownCommand.CanExecute(null));
        second.MoveUpCommand.Execute(null);
        Assert.Same(second, page.TerminalFallbackFonts[0]);
        Assert.Equal("JetBrainsMono Nerd Font › Noto Sans CJK SC · Medium", page.TerminalFallbackFontsSummary);
        Assert.False(second.MoveUpCommand.CanExecute(null));
        Assert.False(first.MoveDownCommand.CanExecute(null));
        Assert.Equal("JetBrainsMono Nerd Font", page.DraftFontSnapshot.FallbackFonts[0].Family);
        int previewNotifications = 0;
        second.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(second.PreviewFontWeight)) previewNotifications++; };
        page.FontWeight = 700;
        Assert.True(previewNotifications > 0);
        Assert.Equal((FontWeight)700, second.PreviewFontWeight);
        Assert.Equal((FontWeight)500, first.PreviewFontWeight);
        first.SelectedWeight = first.AvailableWeights[0];
        Assert.Null(page.DraftFontSnapshot.FallbackFonts[1].Weight);
        Assert.Equal((FontWeight)700, first.PreviewFontWeight);
        first.RemoveCommand.Execute(null);
        Assert.Single(page.DraftFontSnapshot.FallbackFonts);
        Assert.Equal("JetBrainsMono Nerd Font", page.TerminalFallbackFontsSummary);
        Assert.False(second.MoveDownCommand.CanExecute(null));
        Assert.True(notifications >= 7);
    }

    [Fact]
    public void MissingFamilyPreservesConfiguredWeight()
    {
        AppearanceSettingsPage page = new();
        page.SetTerminalFallbackFonts([new() { Family = "KeiTerm missing font 7c213c", Weight = 600 }]);
        FallbackFontEntry entry = Assert.Single(page.TerminalFallbackFonts);
        Assert.True(entry.IsMissing);
        Assert.Equal(600, entry.SelectedWeight!.Weight);
        Assert.Equal(600, Assert.Single(page.DraftFontSnapshot.FallbackFonts).Weight);
    }

    [FallbackFontsFact]
    public void FallbackTypefaceUsesOwnWeightAndBoldRule()
    {
        using TerminalFontResolver resolver = new();
        TerminalTypefaceCollection Make(int? weight, int primaryWeight = 400) => TerminalTypefaceProvider.Create(
            new TerminalFontSnapshot("JetBrainsMono Nerd Font", [new() { Family = "Noto Sans CJK SC", Weight = weight }], 14, false, primaryWeight))!;
        TerminalTypefaceCollection explicitWeight = Make(500);
        TerminalTypefaceCollection followPrimary = Make(null);
        SKTypeface medium = resolver.ResolveTypeface(explicitWeight, TerminalTypefaceStyle.Regular, '中').Typeface;
        SKTypeface regular = resolver.ResolveTypeface(followPrimary, TerminalTypefaceStyle.Regular, '中').Typeface;
        Assert.Equal("Noto Sans CJK SC", medium.FamilyName);
        Assert.Equal(500, medium.FontWeight);
        Assert.Equal(400, regular.FontWeight);
        Assert.NotSame(medium, regular);
        Assert.Same(TerminalTypefaceProvider.Match("Noto Sans CJK SC", 800),
            resolver.ResolveTypeface(explicitWeight, TerminalTypefaceStyle.Bold, '中').Typeface);
        foreach (int weight in new[] { 100, 500, 900 })
        {
            int bold = Math.Clamp(weight + 300, 700, 900);
            TerminalTypefaceCollection collection = Make(weight, weight);
            // 字体文件不一定包含 900；应匹配请求字重所解析出的真实 face，而非要求字体虚构一个字重。
            Assert.Same(TerminalTypefaceProvider.Match("JetBrainsMono Nerd Font", bold), collection.GetPrimaryTypeface(TerminalTypefaceStyle.Bold));
            Assert.Same(TerminalTypefaceProvider.Match("Noto Sans CJK SC", bold),
                resolver.ResolveTypeface(collection, TerminalTypefaceStyle.Bold, '中').Typeface);
        }
    }

    [FallbackFontsFact]
    public async Task ApplyAndCancelKeepFallbackChangesTransactional()
    {
        string directory = NewDirectory();
        try
        {
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            service.Current.FontFamily = "JetBrainsMono Nerd Font";
            service.Current.TerminalFallbackFontFamily = null;
            service.Current.TerminalFallbackFonts = [new() { Family = "Noto Sans CJK SC", Weight = 500 }];
            using SettingsViewModel model = new(service, directory);
            AppearanceSettingsPage page = model.Categories.Select(category => category.Page).OfType<AppearanceSettingsPage>().Single();
            page.TerminalFallbackFonts[0].SelectedWeight = page.TerminalFallbackFonts[0].AvailableWeights[0];
            page.TerminalFallbackFontCandidate = page.AvailableFonts.Single(font => font.Name == "JetBrainsMono Nerd Font");
            page.AddTerminalFallbackFont();
            page.TerminalFallbackFonts[1].MoveUpCommand.Execute(null);
            Assert.Equal(500, Assert.Single(service.Current.TerminalFallbackFonts).Weight);
            await model.CancelCommand.ExecuteAsync(null);
            Assert.Equal(500, Assert.Single(page.DraftFontSnapshot.FallbackFonts).Weight);
            Assert.Equal("Noto Sans CJK SC · Medium", page.TerminalFallbackFontsSummary);
            page.TerminalFallbackFonts[0].SelectedWeight = page.TerminalFallbackFonts[0].AvailableWeights[0];
            await model.ApplyCommand.ExecuteAsync(null);
            Assert.Null(Assert.Single(service.Current.TerminalFallbackFonts).Weight);
            page.TerminalFallbackFonts[0].RemoveCommand.Execute(null);
            await model.CancelCommand.ExecuteAsync(null);
            Assert.Null(Assert.Single(page.DraftFontSnapshot.FallbackFonts).Weight);
            AppSettings reopened = await new JsonSettingsService(Path.Combine(directory, "settings.json")).LoadSettingsAsync();
            Assert.Null(Assert.Single(reopened.TerminalFallbackFonts).Weight);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
