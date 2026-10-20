using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class TransferTableLayoutTests
{
    [Fact]
    public void Defaults_ShowSizeProgressSpeedStatus_HideRemotePath()
    {
        TransferTableLayout layout = new();

        Assert.False(layout.RemotePath.IsVisible);
        Assert.True(layout.Size.IsVisible);
        Assert.True(layout.Progress.IsVisible);
        Assert.True(layout.Speed.IsVisible);
        Assert.True(layout.Status.IsVisible);
    }

    [Fact]
    public void SetWidth_ClampsToMinimumAndMaximum_AndResetRestoresDefault()
    {
        TransferTableLayout layout = new();

        layout.SetWidth("Size", 5);
        Assert.Equal(layout.Size.MinWidth, layout.Size.Width);

        layout.SetWidth("Size", 99999);
        Assert.Equal(TransferTableColumn.MaxWidth, layout.Size.Width);

        layout.SetWidth("Size", double.NaN);
        Assert.Equal(layout.Size.DefaultWidth, layout.Size.Width);

        layout.SetWidth("Speed", 150);
        Assert.Equal(150, layout.Speed.Width);
        layout.ResetWidth("Speed");
        Assert.Equal(layout.Speed.DefaultWidth, layout.Speed.Width);
    }

    [Fact]
    public void HidingColumn_ZeroesGridWidthAndMinWidth_AndShrinksTableMinWidth()
    {
        TransferTableLayout layout = new();
        using TransferTableView view = new(layout);
        double before = view.TableMinWidth;

        layout.Size.IsVisible = false;

        Assert.Equal(0, view.Size.GridWidth.Value);
        Assert.Equal(0, view.Size.GridMinWidth);
        Assert.Equal(before - layout.Size.Width, view.TableMinWidth);
    }

    [Fact]
    public void NarrowViewport_ShrinksColumnsTowardMinimum_NeverBelowIt()
    {
        TransferTableLayout layout = new();
        using TransferTableView view = new(layout);

        // 宽视口：保持用户设定宽度
        view.SetAvailableWidth(900);
        Assert.Equal(layout.Size.Width, view.Size.EffectiveWidth);

        // 中等视口：各列被压缩但不低于最小宽度，且总宽恰好放进视口
        view.SetAvailableWidth(450);
        Assert.True(view.Size.EffectiveWidth < layout.Size.Width);
        Assert.True(view.Size.EffectiveWidth >= layout.Size.MinWidth);
        Assert.Equal(450, view.TableMinWidth, 3);
        // 压缩不改动共享的用户设定
        Assert.Equal(150, layout.Size.Width);

        // 极窄视口：全部到最小宽度，表格最小总宽超出视口（外层水平滚动）
        view.SetAvailableWidth(200);
        Assert.Equal(layout.Size.MinWidth, view.Size.EffectiveWidth);
        Assert.Equal(layout.Speed.MinWidth, view.Speed.EffectiveWidth);
        Assert.True(view.TableMinWidth > 200);
    }

    [Fact]
    public void TwoViewsShareWidths_ButFitIndependently()
    {
        TransferTableLayout layout = new();
        using TransferTableView wide = new(layout);
        using TransferTableView narrow = new(layout);
        wide.SetAvailableWidth(900);
        narrow.SetAvailableWidth(400);

        layout.SetWidth("Speed", 120);

        Assert.Equal(120, wide.Speed.EffectiveWidth);
        Assert.True(narrow.Speed.EffectiveWidth < 120);
    }

    [Fact]
    public void SameSettingsService_SharesOneLayoutInstance()
    {
        JsonSettingsService service = new(Path.Combine(Path.GetTempPath(), "keiterm-" + Guid.NewGuid() + ".json"));

        Assert.Same(TransferTableLayout.For(service), TransferTableLayout.For(service));
        Assert.NotSame(TransferTableLayout.For(null), TransferTableLayout.For(null));
    }

    [Fact]
    public async Task Layout_RoundTripsThroughSettingsFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "keiterm-" + Guid.NewGuid() + ".json");
        try
        {
            JsonSettingsService first = new(path);
            await first.LoadSettingsAsync();
            TransferTableLayout layout = TransferTableLayout.For(first);
            layout.SetWidth("Speed", 123);
            layout.RemotePath.IsVisible = true;
            layout.Status.IsVisible = false;
            // 拖动结束后提交；等待异步落盘完成
            layout.Commit();
            await first.SaveSettingsAsync(first.Current);

            JsonSettingsService second = new(path);
            await second.LoadSettingsAsync();
            TransferTableLayout restored = TransferTableLayout.For(second);

            Assert.Equal(123, restored.Speed.Width);
            Assert.True(restored.RemotePath.IsVisible);
            Assert.False(restored.Status.IsVisible);
            Assert.True(restored.Size.IsVisible);
            Assert.Equal(restored.Size.DefaultWidth, restored.Size.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFrom_ClampsCorruptWidthsFromSettingsFile()
    {
        TransferTableSettings stored = new();
        stored.ColumnWidths["Size"] = 1;
        stored.ColumnWidths["Speed"] = 1e9;

        TransferTableLayout layout = new();
        layout.LoadFrom(stored);

        Assert.Equal(layout.Size.MinWidth, layout.Size.Width);
        Assert.Equal(TransferTableColumn.MaxWidth, layout.Speed.Width);
    }
}
