using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kei.Term.App.DesignSystem;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Models.Profiles.BuiltIns.Gui;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class UiRefinementsContractTests
{
    [Fact]
    public Task Settings_TabTitleVariableReference_IsAccessibleViaDrillDown() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_tabtitle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            SettingsViewModel model = new(service, directory);
            model.SelectedCategory = model.Categories.Single(category => category.Page is TabBarSettingsProxyPage);
            window = new(model);
            window.Show();
            window.UpdateLayout();
            HeadlessAvalonia.Pump();

            TabBarSettingsProxyPage page = (TabBarSettingsProxyPage)model.SelectedCategory.Page;
            page.DrillDownVariablesCommand.Execute(null);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            SelectableTextBlock variable = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(), control => control.Text == "{remote_path}");
            Assert.True(variable.IsEffectivelyVisible);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task TerminalToolbar_ContainsFileManagerToggleButton_TogglingState() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel tab = new("prod-db", "monospace", 14);

        TerminalConnectionView view = new() { DataContext = tab };
        Window window = new() { Content = view, Width = 800, Height = 600 };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            Button toggleBtn = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TerminalFileManagerToggleButton");
            Assert.NotNull(toggleBtn);
            Assert.False(tab.IsFileManagerVisible);

            toggleBtn.Command?.Execute(null);
            Assert.True(tab.IsFileManagerVisible);

            toggleBtn.Command?.Execute(null);
            Assert.False(tab.IsFileManagerVisible);
        }
        finally
        {
            window.Close();
            await tab.DisposeAsync();
        }
    });

    [Fact]
    public Task FileManagerDockPosition_BroadcastsAcrossTabs_AndPersistsToDisk() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_dock_broadcast_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            service.Current.FileTransfer.IsFileManagerOnLeft = false;
            await service.SaveSettingsAsync(service.Current);

            TerminalTabViewModel tab1 = new("tab1", "monospace", 14);
            TerminalTabViewModel tab2 = new("tab2", "monospace", 14);

            try
            {
                Assert.False(tab1.IsFileManagerOnLeft);
                Assert.False(tab2.IsFileManagerOnLeft);

                SettingsViewModel settingsVm = new(service, directory);
                settingsVm.FileTransfer.IsFileManagerOnLeft = true;
                await settingsVm.SaveCommand.ExecuteAsync(null);

                // 验证存活的多标签即时生效
                Assert.True(tab1.IsFileManagerOnLeft);
                Assert.True(tab2.IsFileManagerOnLeft);
                Assert.True(service.Current.FileTransfer.IsFileManagerOnLeft);

                // 重新从磁盘加载，验证持久化往返
                JsonSettingsService reloadedService = new(path);
                await reloadedService.LoadSettingsAsync();
                Assert.True(reloadedService.Current.FileTransfer.IsFileManagerOnLeft);

            }
            finally
            {
                await tab1.DisposeAsync();
                await tab2.DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    [Theory]
    [InlineData(VsCodeDarkGuiPreset.Id)]
    [InlineData(OneDarkGuiPreset.Id)]
    [InlineData(VeritasHareGuiPreset.Id)]
    public void ThemePresets_TextAndMutedTokens_MeetWcagContrastRequirements(string presetId)
    {
        GuiProfile profile = Assert.Single(BuiltInPresets.DefaultGuiProfiles, p => p.Id == presetId);
        var tokens = GuiThemeTokens.FromProfile(profile);

        Color windowBg = Color.Parse(profile.WindowBackground);
        Color panelBg = Color.Parse(profile.PanelBackground);
        Color panelAltBg = Color.Parse(profile.PanelAltBackground);
        Color primary = Color.Parse(profile.PrimaryText);
        Color secondary = Color.Parse(profile.SecondaryText);
        Color muted = tokens["Kei.Text.Muted"];
        Color cardBg = tokens["Kei.Bg.Card"];

        // 验证主文本在各层背景（Window、Panel、PanelAlt、Card）上的对比度 >= 4.5:1 (WCAG AA)
        foreach (Color bg in new[] { windowBg, panelBg, panelAltBg, cardBg })
        {
            double primaryContrast = ContrastRatio(primary, bg);
            Assert.True(primaryContrast >= 4.5,
                $"预置 {profile.Name} 的 PrimaryText 对比度不足: {primaryContrast:F2}:1 (底色 {bg})");
        }

        // 验证次要文本与辅助文本在主面板及窗口底色上的可读性对比度 >= 3.0:1
        foreach (Color bg in new[] { windowBg, panelBg })
        {
            double secondaryContrast = ContrastRatio(secondary, bg);
            double mutedContrast = ContrastRatio(muted, bg);

            Assert.True(secondaryContrast >= 3.0,
                $"预置 {profile.Name} 的 SecondaryText 对比度不足: {secondaryContrast:F2}:1 (底色 {bg})");
            Assert.True(mutedContrast >= 3.0,
                $"预置 {profile.Name} 的 Kei.Text.Muted 对比度不足: {mutedContrast:F2}:1 (底色 {bg})");
        }
    }

    private static double ContrastRatio(Color c1, Color c2)
    {
        double l1 = RelativeLuminance(c1);
        double l2 = RelativeLuminance(c2);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte c)
        {
            double val = c / 255.0;
            return val <= 0.04045 ? val / 12.92 : Math.Pow((val + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
