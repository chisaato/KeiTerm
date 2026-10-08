using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

// 布局模式只经真实设置服务持久化。非法字段单独回退，取消不得落盘，保存才写入下次启动值。
public class LayoutModeSettingsTests
{
    [Fact]
    public async Task LayoutMode_PersistenceAndFallback()
    {
        await AssertFieldFallback(null);
        await AssertFieldFallback("\"not-a-layout\"");
        await AssertFieldFallback("\"\"");
        await AssertFieldFallback("99");
        await AssertFieldFallback("null");
        await AssertKnownNumberLoads(0, LayoutMode.Classic);
        await AssertKnownNumberLoads(1, LayoutMode.Modern);
        await AssertSavedModernRoundTripsAsStringAndKeepsOtherSettings();
    }

    [Fact]
    public async Task AppearanceSettingsPage_SelectionUpdatesDraftWithoutInstantRerender()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            AppSettings committed = DistinctSettings(LayoutMode.Classic);
            await service.SaveSettingsAsync(committed);
            string beforeSelection = await File.ReadAllTextAsync(path);

            SettingsViewModel model = new(service, directory);
            AppearanceSettingsPage page = AppearanceOf(model);
            LayoutModeOption modern = page.LayoutModeOptions.Single(option => option.Mode == LayoutMode.Modern);
            string guiId = page.SelectedGuiProfile?.Id ?? "";
            string theme = page.NormalizedThemeKey;
            string tabPlacement = page.SelectedTabPlacement.Key;
            string fontFamily = page.FontFamily;
            double fontSize = page.FontSize;
            bool cursorBlink = page.CursorBlink;

            page.SelectedLayoutMode = modern;

            // 改草稿不得写盘，也不得改已提交值。启动期有效模式由主窗口自行锁定，这里不能提前切换。
            Assert.Equal(LayoutMode.Modern, page.SelectedLayoutMode.Mode);
            Assert.Equal(LayoutMode.Classic, service.Current.LayoutMode);
            Assert.Equal(beforeSelection, await File.ReadAllTextAsync(path));
            Assert.Equal(guiId, page.SelectedGuiProfile?.Id);
            Assert.Equal(theme, page.NormalizedThemeKey);
            Assert.Equal(tabPlacement, page.SelectedTabPlacement.Key);
            Assert.Equal(fontFamily, page.FontFamily);
            Assert.Equal(fontSize, page.FontSize);
            Assert.Equal(cursorBlink, page.CursorBlink);
            Assert.Equal("System", page.NormalizedThemeKey);
            Assert.Equal("Bottom", page.SelectedTabPlacement.Key);

            await model.CancelCommand.ExecuteAsync(null);

            JsonSettingsService afterCancel = new(path);
            AppSettings cancelled = await afterCancel.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Classic, cancelled.LayoutMode);
            AssertDistinctFields(cancelled);
            SettingsViewModel reopenedAfterCancel = new(afterCancel, directory);
            Assert.Equal(LayoutMode.Classic, AppearanceOf(reopenedAfterCancel).SelectedLayoutMode.Mode);

            page.SelectedLayoutMode = modern;
            await model.SaveCommand.ExecuteAsync(null);
            Assert.True(model.IsConfirmed);

            JsonSettingsService afterSave = new(path);
            AppSettings saved = await afterSave.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Modern, saved.LayoutMode);
            AssertDistinctFields(saved);
            await AssertLayoutModeJsonString(path, "Modern");

            SettingsViewModel reopenedAfterSave = new(afterSave, directory);
            AppearanceSettingsPage reopenedPage = AppearanceOf(reopenedAfterSave);
            Assert.Equal(LayoutMode.Modern, reopenedPage.SelectedLayoutMode.Mode);
            Assert.Equal("System", reopenedPage.NormalizedThemeKey);
            Assert.Equal("Bottom", reopenedPage.SelectedTabPlacement.Key);
            Assert.False(reopenedPage.CursorBlink);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertSavedModernRoundTripsAsStringAndKeepsOtherSettings()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService writer = new(path);
            await writer.SaveSettingsAsync(DistinctSettings(LayoutMode.Modern));

            await AssertLayoutModeJsonString(path, "Modern");
            using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            JsonElement policy = saved.RootElement.GetProperty("HostKeyPolicy");
            Assert.Equal(JsonValueKind.Number, policy.ValueKind);
            Assert.Equal(2, policy.GetInt32());

            JsonSettingsService reader = new(path);
            AppSettings loaded = await reader.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Modern, loaded.LayoutMode);
            AssertDistinctFields(loaded);
            Assert.NotSame(writer.Current, loaded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertFieldFallback(string? layoutJson)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            string layoutProperty = layoutJson == null ? "" : $",\n  \"LayoutMode\": {layoutJson}";
            await File.WriteAllTextAsync(path, $$"""
                {
                  "ConfirmBeforeClose": false,
                  "LastSessionManagerVisible": false,
                  "FontSize": 19,
                  "DefaultPort": 2222,
                  "UiTheme": "System",
                  "TabPlacement": "Bottom",
                  "CursorBlink": false,
                  "HostKeyPolicy": 2,
                  "FileTransfer": {
                    "PollingIntervalSeconds": 17,
                    "IsFileManagerOnLeft": true
                  }{{layoutProperty}}
                }
                """);

            JsonSettingsService reader = new(path);
            AppSettings loaded = await reader.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Classic, loaded.LayoutMode);
            AssertDistinctFields(loaded);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertKnownNumberLoads(int number, LayoutMode expected)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, $$"""
                {
                  "LayoutMode": {{number}},
                  "ConfirmBeforeClose": false,
                  "LastSessionManagerVisible": false,
                  "FontSize": 19,
                  "DefaultPort": 2222,
                  "UiTheme": "System",
                  "TabPlacement": "Bottom",
                  "CursorBlink": false,
                  "HostKeyPolicy": 2,
                  "FileTransfer": {
                    "PollingIntervalSeconds": 17,
                    "IsFileManagerOnLeft": true
                  }
                }
                """);

            JsonSettingsService reader = new(path);
            AppSettings loaded = await reader.LoadSettingsAsync();
            Assert.Equal(expected, loaded.LayoutMode);
            AssertDistinctFields(loaded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AppSettings DistinctSettings(LayoutMode layoutMode)
    {
        return new AppSettings
        {
            LayoutMode = layoutMode,
            ConfirmBeforeClose = false,
            LastSessionManagerVisible = false,
            FontSize = 19,
            DefaultPort = 2222,
            UiTheme = "System",
            TabPlacement = "Bottom",
            CursorBlink = false,
            HostKeyPolicy = HostKeyPolicy.Strict,
            FileTransfer = new FileTransferSettings
            {
                PollingIntervalSeconds = 17,
                IsFileManagerOnLeft = true,
            },
        };
    }

    private static void AssertDistinctFields(AppSettings loaded)
    {
        Assert.False(loaded.ConfirmBeforeClose);
        Assert.False(loaded.LastSessionManagerVisible);
        Assert.Equal(19, loaded.FontSize);
        Assert.Equal(2222, loaded.DefaultPort);
        Assert.Equal("System", loaded.UiTheme);
        Assert.Equal("Bottom", loaded.TabPlacement);
        Assert.False(loaded.CursorBlink);
        Assert.Equal(HostKeyPolicy.Strict, loaded.HostKeyPolicy);
        Assert.Equal(17, loaded.FileTransfer.PollingIntervalSeconds);
        Assert.True(loaded.FileTransfer.IsFileManagerOnLeft);
    }

    private static async Task AssertLayoutModeJsonString(string path, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement layoutMode = document.RootElement.GetProperty("LayoutMode");
        Assert.Equal(JsonValueKind.String, layoutMode.ValueKind);
        Assert.Equal(expected, layoutMode.GetString());
    }

    private static AppearanceSettingsPage AppearanceOf(SettingsViewModel model)
    {
        return Assert.IsType<AppearanceSettingsPage>(
            Assert.Single(model.Categories, category => category.Page is AppearanceSettingsPage).Page);
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-layout-mode-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        return directory;
    }
}
