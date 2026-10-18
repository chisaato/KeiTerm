using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.Tests;

// 复用已有事务测试替身，但验证草稿输入到真实标签和控件的完整生产链路。
public partial class TerminalProfileApplicationTests
{
    [Fact]
    public Task PaletteDraft_EditDeleteAndCancel_RefreshOpenControls() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewTempDir();
        InMemorySettingsService settings = new();
        ProfileManagerService manager = new(settings, directory);
        TerminalProfile original = Profile("hot-original", "#123456");
        TerminalProfile alternate = Profile("hot-alternate", "#ABCDEF");
        manager.AddOrUpdateCustomTerminalProfile(original);
        manager.AddOrUpdateCustomTerminalProfile(alternate);
        manager.NotifyDefaultProfileSelectionChanged(original.Id);
        await manager.CommitAndSaveProfilesAsync();
        await using MainViewModel main = CreateMainViewModel(settings, manager);
        using SettingsViewModel editor = new(settings, directory, profileManager: manager);
        TerminalTabViewModel inherited = new("Inherited", Font(), original, null);
        TerminalTabViewModel explicitTab = new("Explicit", Font(), original, original.Id);
        main.Tabs.Add(inherited);
        main.Tabs.Add(explicitTab);
        TerminalControl inheritedControl = inherited.Terminal;
        TerminalControl explicitControl = explicitTab.Terminal;
        // 与 OpenTab 一致，构造后显式注入初始方案。
        inherited.ApplyTerminalProfile(original);
        explicitTab.ApplyTerminalProfile(original);
        try
        {
            AppearanceSettingsPage page = AppearanceOf(editor);
            page.SelectedTerminalProfile = alternate;
            Assert.Equal(alternate.Id, inherited.EffectiveProfileId);
            Assert.Equal(original.Id, explicitTab.EffectiveProfileId);
            Assert.NotNull(inheritedControl.Theme);
            Assert.NotNull(explicitControl.Theme);
            Assert.Equal(0xFFABCDEFu, inheritedControl.Theme.DefaultForeground);
            Assert.Equal(0xFF123456u, explicitControl.Theme.DefaultForeground);
            Assert.NotEqual(inheritedControl.Theme.DefaultForeground, explicitControl.Theme.DefaultForeground);

            TerminalProfile edited = Profile(original.Id, "#FEDCBA");
            manager.AddOrUpdateCustomTerminalProfile(edited);
            Assert.Equal("#FEDCBA", explicitTab.CurrentProfile.Foreground);
            Assert.Equal(0xFFFEDCBAu, explicitControl.Theme.DefaultForeground);
            Assert.NotEqual(inheritedControl.Theme.DefaultForeground, explicitControl.Theme.DefaultForeground);
            manager.RemoveCustomTerminalProfile(original.Id);
            Assert.Equal(alternate.Id, explicitTab.EffectiveProfileId);
            Assert.Equal(inheritedControl.Theme.DefaultForeground, explicitControl.Theme.DefaultForeground);

            await editor.CancelCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            Assert.Equal(original.Id, inherited.EffectiveProfileId);
            Assert.Equal(original.Id, explicitTab.EffectiveProfileId);
            Assert.Equal("#123456", explicitTab.CurrentProfile.Foreground);
            Assert.Equal(0xFF123456u, explicitControl.Theme.DefaultForeground);
            Assert.Equal(inheritedControl.Theme.DefaultForeground, explicitControl.Theme.DefaultForeground);
        }
        finally
        {
            await inherited.DisposeAsync();
            await explicitTab.DisposeAsync();
            Directory.Delete(directory, true);
        }
    });

    [MonospaceFact]
    public Task FontDraft_AllFieldsReachControls_CoalesceAndCancelRestores() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewTempDir();
        string family = InstalledMonospace.Require();
        InMemorySettingsService settings = new(new AppSettings { FontFamily = family, FontSize = 14, CursorBlink = true });
        ProfileManagerService manager = new(settings, directory);
        await using MainViewModel main = CreateMainViewModel(settings, manager);
        using SettingsViewModel editor = new(settings, directory, profileManager: manager);
        TerminalFontSnapshot committed = AppearanceOf(editor).DraftFontSnapshot;
        TerminalTabViewModel first = new("First", committed, manager.DefaultTerminalProfile, null);
        TerminalTabViewModel second = new("Second", committed, manager.DefaultTerminalProfile, manager.DefaultTerminalProfile.Id);
        main.Tabs.Add(first);
        main.Tabs.Add(second);
        Window host = new() { Width = 800, Height = 500, Content = first.Terminal };
        host.Show();
        TerminalControl other = second.Terminal;
        HeadlessAvalonia.Pump();
        try
        {
            AppearanceSettingsPage page = AppearanceOf(editor);
            // 不存在族也必须更新公开属性，并清除上一份 typeface collection，而非黏住旧字体。
            page.FontFamily = "KeiTerm missing hot reload family";
            HeadlessAvalonia.Pump();
            Assert.Equal(page.FontFamily, first.Terminal.FontFamilyName);
            Assert.Equal(page.FontFamily, other.FontFamilyName);
            Assert.Null(first.Terminal.TypefaceCollection);

            page.FontFamily = family;
            page.FontWeight = 700;
            page.SetTerminalFallbackFonts([new FallbackFontSetting { Family = family, Weight = 400 }]);
            page.CursorBlink = false;
            int sizeChanges = 0;
            first.Terminal.PropertyChanged += (_, args) =>
            {
                if (args.Property == TerminalControl.TerminalFontSizeProperty) sizeChanges++;
            };
            page.FontSize = 17;
            page.FontSize = 18;
            page.FontSize = 19;
            Assert.Equal(14, first.CurrentFontSnapshot.FontSize);
            HeadlessAvalonia.Pump();
            Assert.Equal(1, sizeChanges);
            foreach (TerminalTabViewModel tab in new[] { first, second })
            {
                Assert.Equal(family, tab.CurrentFontSnapshot.PrimaryFontFamily);
                Assert.Equal(700, tab.CurrentFontSnapshot.FontWeight);
                Assert.False(tab.CurrentFontSnapshot.CursorBlink);
                Assert.Equal(400, Assert.Single(tab.CurrentFontSnapshot.FallbackFonts).Weight);
                Assert.Equal(19, tab.Terminal.TerminalFontSize);
                Assert.Equal(family, tab.Terminal.FontFamilyName);
                Assert.NotNull(tab.Terminal.TypefaceCollection);
                Assert.True(tab.Terminal.TypefaceCollection.GetPrimaryTypeface().FontWeight >= 600);
            }
            Assert.Equal(first.Terminal.Renderer!.CellHeight, first.Terminal.ScrollData!.CellHeight);
            Assert.Equal(14, settings.Current.FontSize);
            Assert.Equal(400, settings.Current.TerminalFontWeight);

            // 回退项自身的字重变化也必须触发完整快照，而不只是集合增删。
            page.TerminalFallbackFonts[0].SelectedWeight = new FontWeightOption(700, "Bold");
            HeadlessAvalonia.Pump();
            Assert.Equal(700, Assert.Single(first.CurrentFontSnapshot.FallbackFonts).Weight);

            await editor.CancelCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            foreach (TerminalTabViewModel tab in new[] { first, second })
            {
                Assert.Equal(committed.PrimaryFontFamily, tab.CurrentFontSnapshot.PrimaryFontFamily);
                Assert.Equal(committed.FontWeight, tab.CurrentFontSnapshot.FontWeight);
                Assert.Equal(committed.FontSize, tab.Terminal.TerminalFontSize);
                Assert.Equal(committed.CursorBlink, tab.CurrentFontSnapshot.CursorBlink);
                Assert.Equal(committed.FallbackFonts, tab.CurrentFontSnapshot.FallbackFonts);
                Assert.NotNull(tab.Terminal.TypefaceCollection);
            }
        }
        finally
        {
            host.Close();
            await first.DisposeAsync();
            await second.DisposeAsync();
            Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingOrFailedApply_RestoresOpenTerminalFont(bool failApply) => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewTempDir();
        InMemorySettingsService settings = new();
        ProfileManagerService manager = new(settings, directory);
        await using MainViewModel main = CreateMainViewModel(settings, manager);
        using SettingsViewModel editor = new(settings, directory, profileManager: manager);
        TerminalFontSnapshot committed = AppearanceOf(editor).DraftFontSnapshot;
        TerminalTabViewModel tab = CreateTab(manager.DefaultTerminalProfile, null, new RecordingThemeSink());
        main.Tabs.Add(tab);
        SettingsWindow window = new(editor);
        window.Show();
        try
        {
            AppearanceOf(editor).FontFamily = "Draft only";
            AppearanceOf(editor).FontSize = 23;
            HeadlessAvalonia.Pump();
            Assert.Equal("Draft only", tab.CurrentFontSnapshot.PrimaryFontFamily);
            if (failApply)
            {
                settings.FailuresRemaining = 1;
                Assert.False(await editor.ApplyChangesAsync());
            }
            else
            {
                window.Close();
            }
            HeadlessAvalonia.Pump();
            Assert.Equal(committed.PrimaryFontFamily, tab.CurrentFontSnapshot.PrimaryFontFamily);
            Assert.Equal(committed.FontSize, tab.CurrentFontSnapshot.FontSize);
        }
        finally
        {
            window.Close();
            await tab.DisposeAsync();
            Directory.Delete(directory, true);
        }
    });
}
