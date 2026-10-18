using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views.Settings.Controls;

namespace Kei.Term.Tests;

public class SettingsPageInteractionBehaviorTests
{
    // 每行明确对应一个真实 XAML 绑定，且改变后的值必须从 VM 读回，不能只检查控件自身。
    [Theory]
    [InlineData(typeof(GeneralSettingsPage), "", "check", "ConfirmBeforeClose")]
    [InlineData(typeof(AppearanceSettingsPage), "", "combo", "SelectedLayoutMode")]
    [InlineData(typeof(TreeSettingsProxyPage), "General", "combo", "SelectedTreeSort")]
    [InlineData(typeof(TerminalSettingsCombinedPage), "Terminal", "number", "ScrollbackLines")]
    [InlineData(typeof(TerminalAppearanceSettingsPage), "Appearance", "number", "FontSize")]
    [InlineData(typeof(TabBarSettingsProxyPage), "Appearance", "combo", "SelectedTabPlacement")]
    [InlineData(typeof(SshSettingsPage), "", "text", "DefaultUsername")]
    [InlineData(typeof(SecuritySettingsPage), "", "number", "LockTimeoutMinutes")]
    [InlineData(typeof(TransferSettingsPage), "", "text", "DefaultDownloadDirectory")]
    [InlineData(typeof(FileManagerSettingsPage), "", "check", "IsFileManagerOnLeft")]
    public Task RepresentativeControl_IsVisibleSizedAndWritesBack(Type pageType, string targetPath, string kind, string propertyName)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
            SettingsCategoryItem category = context.Model.Categories.Single(c => c.Page.GetType() == pageType);
            context.Model.SelectedCategory = category;
            context.Layout();
            object target = targetPath.Length == 0 ? category.Page : Property(category.Page, targetPath).GetValue(category.Page)!;
            PropertyInfo property = Property(target, propertyName);
            object? before = property.GetValue(target);
            Control control = FindControl(context, target, kind);
            SettingsHeadlessTestContext.AssertVisible(control);
            object expected;
            switch (control)
            {
                case ComboBox combo:
                    expected = combo.Items.Cast<object>().First(item => !Equals(item, before));
                    combo.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, expected);
                    break;
                case NumericUpDown number:
                    decimal next = Convert.ToDecimal(before) + (propertyName == "ScrollbackLines" ? 100 : 1);
                    number.SetCurrentValue(NumericUpDown.ValueProperty, next);
                    expected = Convert.ChangeType(next, property.PropertyType);
                    break;
                case ToggleButton check:
                    expected = !(bool)before!;
                    check.SetCurrentValue(ToggleButton.IsCheckedProperty, expected);
                    break;
                case TextBox text:
                    expected = "lane-c-input";
                    text.SetCurrentValue(TextBox.TextProperty, expected);
                    break;
                default:
                    throw new InvalidOperationException(control.GetType().Name);
            }
            context.Layout();
            Assert.NotEqual(before, property.GetValue(target));
            Assert.Equal(expected, property.GetValue(target));
            // 分类切走再回来，草稿仍由同一页面实例持有，重新构建的绑定也能读取它。
            context.Model.SelectedCategory = context.Model.Categories.First(c => !c.IsHeader && c != category);
            context.Layout();
            context.Model.SelectedCategory = category;
            context.Layout();
            Assert.Same(category.Page, context.Model.SelectedCategory.Page);
            Assert.Equal(expected, property.GetValue(target));
            SettingsHeadlessTestContext.AssertVisible(FindControl(context, target, kind));
        });

    [Theory]
    [InlineData(typeof(AppearanceSettingsPage), "", "DrillDownUiFallbackFontsCommand", "FontSize")]
    [InlineData(typeof(TerminalAppearanceSettingsPage), "Appearance", "DrillDownTerminalFallbackFontsCommand", "FontSize")]
    [InlineData(typeof(TabBarSettingsProxyPage), "", "DrillDownVariablesCommand", "Terminal")]
    [InlineData(typeof(TransferSettingsPage), "", "DrillDownHashAlgorithmsCommand", "DefaultDownloadDirectory")]
    [InlineData(typeof(TransferSettingsPage), "", "DrillDownArchiveFormatsCommand", "DefaultDownloadDirectory")]
    public Task DrillDownButtonAndHeaderBack_PreserveCategoryAndDraft(Type pageType, string hostPath, string commandName, string stateProperty)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
            SettingsCategoryItem category = context.Model.Categories.Single(c => c.Page.GetType() == pageType);
            context.Model.SelectedCategory = category;
            context.Layout();
            object hostObject = hostPath.Length == 0 ? category.Page : Property(category.Page, hostPath).GetValue(category.Page)!;
            ISettingsDrillDownHost host = Assert.IsAssignableFrom<ISettingsDrillDownHost>(hostObject);
            PropertyInfo state = Property(hostObject, stateProperty);
            if (state.PropertyType == typeof(string)) state.SetValue(hostObject, "retained-download-path");
            if (state.PropertyType == typeof(double)) state.SetValue(hostObject, 19d);
            object? expectedState = state.GetValue(hostObject);
            if (expectedState is TerminalSettingsPage terminal) terminal.TabTitleTemplate = "retained-{host}";
            ICommand command = Assert.IsAssignableFrom<ICommand>(Property(hostObject, commandName).GetValue(hostObject));
            DrillDownRow row = context.Window.GetVisualDescendants().OfType<DrillDownRow>().Single(r => r.Command == command && r.IsEffectivelyVisible);
            if (row.HasSwitch)
            {
                CheckBox enabled = row.GetVisualDescendants().OfType<CheckBox>().Single();
                SettingsHeadlessTestContext.AssertVisible(enabled);
                enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                context.Layout();
                Assert.True(row.SwitchChecked);
            }
            Button enter = row.GetVisualDescendants().OfType<Button>().Single(b => b.Command == command);
            Assert.True(enter.IsEffectivelyEnabled);
            SettingsHeadlessTestContext.Click(context.Window, enter);
            context.Layout();
            Assert.True(host.IsInSubPage);
            Assert.NotNull(host.CurrentSubPage);
            SettingsPageHeader header = context.Window.GetVisualDescendants().OfType<SettingsPageHeader>().Single(h => h.IsEffectivelyVisible);
            Assert.Equal(host.CurrentSubPage.Title, header.CurrentDisplayTitle);
            Assert.True(header.HasSubPage);
            Assert.Same(category, context.Model.SelectedCategory);
            Button back = header.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Command == header.BackCommand);
            SettingsHeadlessTestContext.Click(context.Window, back);
            context.Layout();
            Assert.False(host.IsInSubPage);
            Assert.Null(host.CurrentSubPage);
            Assert.False(header.HasSubPage);
            Assert.Equal(category.Title, header.CurrentDisplayTitle);
            Assert.Same(category, context.Model.SelectedCategory);
            Assert.Same(category.Page, context.Model.SelectedCategory.Page);
            Assert.Equal(expectedState, state.GetValue(hostObject));
            if (expectedState is TerminalSettingsPage retained) Assert.Equal("retained-{host}", retained.TabTitleTemplate);
            Assert.Same(row, context.Window.GetVisualDescendants().OfType<DrillDownRow>().Single(r => r.Command == command && r.IsEffectivelyVisible));
        });

    private static PropertyInfo Property(object target, string name)
        => target.GetType().GetProperty(name) ?? throw new InvalidOperationException($"{target.GetType().Name}.{name}");

    private static Control FindControl(SettingsHeadlessTestContext context, object target, string kind)
    {
        Type type = kind switch
        {
            "check" => typeof(CheckBox),
            "toggle" => typeof(ToggleSwitch),
            "combo" => typeof(ComboBox),
            "number" => typeof(NumericUpDown),
            "text" => typeof(TextBox),
            _ => throw new InvalidOperationException(kind),
        };
        return context.Window.GetVisualDescendants().OfType<Control>().First(control =>
            control.GetType() == type && (ReferenceEquals(control.DataContext, target) || ReferenceEquals(control.DataContext, context.Model.SelectedCategory.Page)) && control.IsEffectivelyVisible
            && (type != typeof(TextBox) || control.FindAncestorOfType<NumericUpDown>() == null));
    }
}
