using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Transfers;
using Xunit;

namespace Kei.Term.Tests;

public class SettingsDrillDownAndNavigationTests
{
    [Fact]
    public void SettingsCategoryItem_GroupAndHeaderProperties_AreCorrect()
    {
        var header = SettingsCategoryItem.CreateHeader("应用");
        Assert.True(header.IsHeader);
        Assert.Equal("应用", header.Title);
        Assert.Equal("应用", header.Group);
        Assert.Empty(header.Icon);

        var item = new SettingsCategoryItem("常规", "窗口显示与行为", new object(), SettingsIcons.General, "应用");
        Assert.False(item.IsHeader);
        Assert.Equal("常规", item.Title);
        Assert.Equal("窗口显示与行为", item.Description);
        Assert.True(item.HasDescription);
        Assert.Equal("应用", item.Group);
        Assert.Equal(SettingsIcons.General, item.Icon);
    }

    [Fact]
    public void TransferSettingsPage_DrillDownLifecycle_NavigatesAndResets()
    {
        TransferSettingsPage page = new();
        Assert.False(page.IsInSubPage);
        Assert.Null(page.CurrentSubPage);

        // 模拟下钻到哈希算法
        page.DrillDownHashAlgorithmsCommand.Execute(null);
        Assert.True(page.IsInSubPage);
        Assert.NotNull(page.CurrentSubPage);
        Assert.IsType<HashAlgorithmsSubPage>(page.CurrentSubPage!.Content);

        // 面包屑 / 返回命令触发返回
        page.NavigateBackCommand.Execute(null);
        Assert.False(page.IsInSubPage);
        Assert.Null(page.CurrentSubPage);

        // 下钻到归档格式
        page.DrillDownArchiveFormatsCommand.Execute(null);
        Assert.True(page.IsInSubPage);
        Assert.NotNull(page.CurrentSubPage);
        Assert.IsType<ArchiveFormatsSubPage>(page.CurrentSubPage!.Content);

        // 再次返回
        page.NavigateBack();
        Assert.False(page.IsInSubPage);
        Assert.Null(page.CurrentSubPage);
    }

    [Fact]
    public void TransferSettingsPage_Summaries_UpdateWhenAlgorithmsOrFormatsChange()
    {
        TransferSettingsPage page = new();
        page.InitializeTransferPreferences(
            verifyHashEnabled: true,
            hashOrder: [TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Md5],
            archiveDirectoriesEnabled: true,
            archiveOrder: [TransferArchiveFormat.Zstd, TransferArchiveFormat.Gzip]);

        Assert.Equal("SHA-256 › MD5", page.HashAlgorithmSummary);
        Assert.Equal("tar.zst (Zstandard) › tar.gz (Gzip)", page.ArchiveFormatSummary);

        // 移动或改变启用状态时摘要联动刷新
        var first = page.HashAlgorithms.First(h => h.Value == TransferHashAlgorithm.Sha256);
        first.IsEnabled = false;
        Assert.Equal("MD5", page.HashAlgorithmSummary);

        page.SelectedArchiveFormat = page.ArchiveFormats.First(a => a.Value == TransferArchiveFormat.Gzip);
        page.MoveUpArchiveFormatCommand.Execute(null);
        Assert.Equal("tar.gz (Gzip) › tar.zst (Zstandard)", page.ArchiveFormatSummary);
    }
}
