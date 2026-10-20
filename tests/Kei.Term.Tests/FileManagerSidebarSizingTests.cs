using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

public class FileManagerSidebarSizingTests
{
    [MonospaceFact]
    public Task SettingsApply_UpdatesUntouchedConnectionButKeepsDraggedRatio() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-sidebar-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TerminalTabViewModel tab = new("files",
            new TerminalFontSnapshot(InstalledMonospace.Require(), [], 14, false),
            BuiltInPresets.GetDefaultTerminalProfile(), explicitProfileId: null);
        tab.IsFileManagerVisible = true;
        TerminalConnectionView view = new() { DataContext = tab };
        Window window = new() { Content = view, Width = 1200, Height = 600 };
        try
        {
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            using SettingsViewModel model = new(service, directory);
            FileManagerSettingsPage page = model.Categories.Select(category => category.Page).OfType<FileManagerSettingsPage>().Single();
            Assert.Equal(40, page.FileManagerSidebarWidthPercent);
            window.Show();
            HeadlessAvalonia.Pump();
            Grid layout = view.FindControl<Grid>("ConnectionLayout")!;
            Assert.Equal(layout.Bounds.Width * 0.4, layout.ColumnDefinitions[2].ActualWidth, 2);

            page.FileManagerSidebarWidthPercent = 60;
            Assert.True(await model.ApplyChangesAsync());
            HeadlessAvalonia.Pump();
            Assert.Equal(layout.Bounds.Width * 0.6, layout.ColumnDefinitions[2].ActualWidth, 2);

            layout.ColumnDefinitions[2].Width = new GridLength(layout.Bounds.Width * 0.5);
            window.UpdateLayout();
            view.FindControl<GridSplitter>("FileManagerSplitter")!.RaiseEvent(new VectorEventArgs
            {
                RoutedEvent = Thumb.DragCompletedEvent,
                Vector = new Vector(0, 0)
            });
            page.FileManagerSidebarWidthPercent = 70;
            Assert.True(await model.ApplyChangesAsync());
            HeadlessAvalonia.Pump();
            Assert.Equal(layout.Bounds.Width * 0.5, layout.ColumnDefinitions[2].ActualWidth, 2);
            Assert.Equal(0.7, service.Current.FileTransfer.FileManagerSidebarRatio);

            window.Width = 1500;
            HeadlessAvalonia.Pump();
            Assert.Equal(layout.Bounds.Width * 0.5, layout.ColumnDefinitions[2].ActualWidth, 2);
            tab.IsShellVisible = false;
            HeadlessAvalonia.Pump();
            Assert.True(layout.ColumnDefinitions[2].Width.IsStar);
            Assert.Equal(layout.Bounds.Width, layout.ColumnDefinitions[2].ActualWidth, 2);
        }
        finally
        {
            window.Close();
            await tab.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    });

    [Theory]
    [InlineData(1000, 400)]
    [InlineData(2000, 800)]
    [InlineData(700, 320)]
    [InlineData(500, 256)]
    [InlineData(200, 0)]
    [InlineData(0, 0)]
    [InlineData(-10, 0)]
    public void Width_FollowsRatioAndReservesTerminal(double container, double expected)
    {
        FileManagerSidebarSizing sizing = new();
        Assert.Equal(expected, sizing.GetWidth(container));
    }

    [Fact]
    public void Drag_RecordsRatioAndIgnoresLaterDefaultChanges()
    {
        FileManagerSidebarSizing sizing = new();
        sizing.RecordDrag(600, 1200);
        sizing.ApplyDefault(0.7);
        Assert.True(sizing.IsManuallyResized);
        Assert.Equal(0.5, sizing.Ratio);
        Assert.Equal(900, sizing.GetWidth(1800));
        Assert.Equal(256, sizing.GetWidth(500));
        Assert.Equal(600, sizing.GetWidth(1200));
    }

    [Theory]
    [InlineData(0.1, 0.2, 400)]
    [InlineData(0.6, 0.6, 1200)]
    [InlineData(0.9, 0.7, 1400)]
    [InlineData(double.NaN, 0.4, 800)]
    public void DefaultChanges_ClampAndUpdateUntouchedView(double ratio, double expectedRatio, double width)
    {
        FileManagerSidebarSizing sizing = new();
        sizing.ApplyDefault(ratio);
        Assert.Equal(expectedRatio, sizing.Ratio);
        Assert.Equal(width, sizing.GetWidth(2000));
        Assert.False(sizing.IsManuallyResized);
    }

    [Fact]
    public void Drag_ClampsToAvailableWidthWithoutOverwritingDesiredRatio()
    {
        FileManagerSidebarSizing sizing = new();
        sizing.RecordDrag(950, 1000);
        Assert.Equal(756, sizing.GetWidth(1000));
        Assert.Equal(3756, sizing.GetWidth(4000));
        sizing.RecordDrag(100, 0);
        Assert.Equal(0.95, sizing.Ratio);
    }

    [Fact]
    public async Task Settings_MissingFieldUsesDefaultAndCustomRatioRoundTrips()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-sidebar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"FileTransfer\":{\"PollingIntervalSeconds\":17}}");
            JsonSettingsService service = new(path);
            AppSettings settings = await service.LoadSettingsAsync();
            FileManagerSidebarSizing sizing = new();
            sizing.ApplyDefault(settings.FileTransfer.FileManagerSidebarRatio);
            Assert.Equal(400, sizing.GetWidth(1000));
            settings.FileTransfer.FileManagerSidebarRatio = 0.55;
            await service.SaveSettingsAsync(settings);
            JsonSettingsService reader = new(path);
            AppSettings loaded = await reader.LoadSettingsAsync();
            sizing.ApplyDefault(loaded.FileTransfer.FileManagerSidebarRatio);
            Assert.Equal(550, sizing.GetWidth(1000));
            Assert.Equal(17, loaded.FileTransfer.PollingIntervalSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
