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
    [Theory]
    [InlineData(null, LayoutMode.Classic)]
    [InlineData("\"not-a-layout\"", LayoutMode.Classic)]
    [InlineData("\"\"", LayoutMode.Classic)]
    [InlineData("99", LayoutMode.Classic)]
    [InlineData("null", LayoutMode.Classic)]
    [InlineData("0", LayoutMode.Classic)]
    [InlineData("1", LayoutMode.Modern)]
    public async Task LayoutMode_PersistenceAndFallback(string? layoutJson, LayoutMode expected)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            string layoutProperty = layoutJson == null ? "" : $",\n  \"LayoutMode\": {layoutJson}";
            await File.WriteAllTextAsync(path, $$"""
                {
                  "ConfirmBeforeClose": false,
                  "HostKeyPolicy": 2,
                  "FileTransfer": {
                    "PollingIntervalSeconds": 17
                  }{{layoutProperty}}
                }
                """);

            JsonSettingsService reader = new(path);
            AppSettings loaded = await reader.LoadSettingsAsync();
            Assert.Equal(expected, loaded.LayoutMode);
            AssertDistinctFields(loaded);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task LayoutMode_SavedModernRoundTripsAsStringAndKeepsOtherSettings()
        => AssertSavedModernRoundTripsAsStringAndKeepsOtherSettings();

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

            page.SelectedLayoutMode = modern;

            // 改草稿不得写盘，也不得改已提交的布局模式。启动期有效模式由主窗口自行锁定。
            Assert.Equal(LayoutMode.Modern, page.SelectedLayoutMode.Mode);
            Assert.Equal(LayoutMode.Classic, service.Current.LayoutMode);
            Assert.Equal(beforeSelection, await File.ReadAllTextAsync(path));

            await model.CancelCommand.ExecuteAsync(null);

            JsonSettingsService afterCancel = new(path);
            AppSettings cancelled = await afterCancel.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Classic, cancelled.LayoutMode);
            SettingsViewModel reopenedAfterCancel = new(afterCancel, directory);
            Assert.Equal(LayoutMode.Classic, AppearanceOf(reopenedAfterCancel).SelectedLayoutMode.Mode);

            page.SelectedLayoutMode = modern;
            await model.SaveCommand.ExecuteAsync(null);
            Assert.True(model.IsConfirmed);

            JsonSettingsService afterSave = new(path);
            AppSettings saved = await afterSave.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Modern, saved.LayoutMode);
            await AssertLayoutModeJsonString(path, "Modern");

            SettingsViewModel reopenedAfterSave = new(afterSave, directory);
            Assert.Equal(LayoutMode.Modern, AppearanceOf(reopenedAfterSave).SelectedLayoutMode.Mode);
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

    private static AppSettings DistinctSettings(LayoutMode layoutMode)
    {
        return new AppSettings
        {
            LayoutMode = layoutMode,
            ConfirmBeforeClose = false,
            HostKeyPolicy = HostKeyPolicy.Strict,
            FileTransfer = new FileTransferSettings
            {
                PollingIntervalSeconds = 17,
            },
        };
    }

    private static void AssertDistinctFields(AppSettings loaded)
    {
        // 只留与默认值不同的哨兵，证明非法 LayoutMode 没有把整份设置打回默认。
        Assert.False(loaded.ConfirmBeforeClose);
        Assert.Equal(HostKeyPolicy.Strict, loaded.HostKeyPolicy);
        Assert.Equal(17, loaded.FileTransfer.PollingIntervalSeconds);
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
