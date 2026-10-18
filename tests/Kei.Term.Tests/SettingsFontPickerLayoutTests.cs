using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.Tests;

public class SettingsFontPickerLayoutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task FontSuggestions_LongNamesStayWithinInputAndUseOneCompactRow(bool terminal, bool fallback)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
            context.Model.SelectedCategory = context.Model.Categories.Single(category => terminal
                ? category.Page is TerminalAppearanceSettingsPage : category.Page is AppearanceSettingsPage);
            AppearanceSettingsPage appearance = terminal
                ? ((TerminalAppearanceSettingsPage)context.Model.SelectedCategory.Page).Appearance
                : (AppearanceSettingsPage)context.Model.SelectedCategory.Page;
            if (fallback)
            {
                if (terminal) appearance.DrillDownTerminalFallbackFontsCommand.Execute(null);
                else appearance.DrillDownUiFallbackFontsCommand.Execute(null);
            }
            context.Layout();
            AutoCompleteBox picker = context.Window.GetVisualDescendants().OfType<AutoCompleteBox>()
                .Single(control => control.IsEffectivelyVisible);
            SettingsHeadlessTestContext.AssertVisible(picker);
            // 注入极长候选触发真实模板测量；没有宽度约束或截断时，弹层会超过输入框。
            picker.ItemsSource = new[] { new FontFamilyOption("KeiFont Exceptionally Long Family Name For Popup Measurement Nerd Font Mono", true) };
            picker.Focus();
            picker.Text = "KeiFont";
            picker.IsDropDownOpen = true;
            Popup popup = picker.GetVisualDescendants().OfType<Popup>().Single();
            HeadlessAvalonia.WaitUntil(() => popup.IsOpen && popup.Child?.Bounds.Width > 0);
            context.Layout();
            Assert.InRange(popup.Child!.Bounds.Width, picker.Bounds.Width - 2, picker.Bounds.Width + 2);
            ListBoxItem row = popup.Child.GetVisualDescendants().OfType<ListBoxItem>().First();
            Assert.InRange(row.Bounds.Height, 22, 32);
            TextBlock name = row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text?.StartsWith("KeiFont") == true);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, name.TextTrimming);
            Grid symbols = row.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Classes.Contains("fontSymbols"));
            Assert.Equal(48, symbols.Bounds.Width);
            // 图标槽位之外尽量留给字族名，避免文字标签再次挤占名称。
            Assert.True(name.Bounds.Width >= picker.Bounds.Width - 80);
            picker.IsDropDownOpen = false;
        });
}
