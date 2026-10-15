using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Dock.Model.Controls;
using Dock.Model.Core;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Xunit;

namespace Kei.Term.Tests;

public partial class WorkspaceTabsTests
{
    [Fact]
    public Task TabShortcuts_SelectNumbersAndCycleInDockOrder() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            // 空工作区的导航也应安全。
            Press(Key.OemMinus);
            Assert.Null(model.ActiveWorkspaceTab);
            for (int index = 0; index < 9; index++) model.NewTabCommand.Execute(null);
            ViewModelBase[] original = model.WorkspaceTabs.ToArray();
            for (int index = 0; index < 9; index++)
            {
                Press((Key)((int)Key.D1 + index));
                Assert.Same(original[index], model.ActiveWorkspaceTab);
            }

            model.CurrentSettings.TabNavigationModifier = "Alt+Shift";
            Press(Key.D3);
            Assert.Same(original[8], model.ActiveWorkspaceTab);
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.D3,
                KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift
            });
            Assert.Same(original[2], model.ActiveWorkspaceTab);
            model.CurrentSettings.TabNavigationModifier = "Primary";
            Press(Key.D9);
            Press(Key.OemPlus);
            Assert.Same(original[0], model.ActiveWorkspaceTab);
            Press(Key.OemMinus);
            Assert.Same(original[8], model.ActiveWorkspaceTab);

            // 模拟 Dock 拖拽后，快捷键必须使用可见顺序而非创建顺序。
            model.Workspace.ReorderTab(original[8], 0);
            Press(Key.D2);
            Assert.Same(original[0], model.ActiveWorkspaceTab);
            Press(Key.D1);
            Assert.Same(original[8], model.ActiveWorkspaceTab);

            // 额外修饰键和普通数字仍交给输入控件。
            Press(Key.D3, KeyModifiers.Shift);
            Assert.Same(original[8], model.ActiveWorkspaceTab);
            await model.CloseWorkspaceTabCommand.ExecuteAsync(original[7]);
            Press(Key.D9);
            Assert.Same(original[8], model.ActiveWorkspaceTab);
            foreach (ViewModelBase tab in model.WorkspaceTabs.ToArray().Skip(1))
                await model.CloseWorkspaceTabCommand.ExecuteAsync(tab);
            Press(Key.OemPlus);
            Press(Key.OemMinus);
            Assert.Same(Assert.Single(model.WorkspaceTabs), model.ActiveWorkspaceTab);
        }
        finally
        {
            window.Close();
            await model.DisposeAsync();
        }

        void Press(Key key, KeyModifiers extra = KeyModifiers.None)
        {
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = AppShortcuts.NewTab.KeyModifiers | extra
            });
        }
    });

    [Fact]
    public Task TabNavigation_UsesActiveGroupAndHandlesKeysBeforeFocusedContent() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            ViewModelBase starter = model.ActiveWorkspaceTab!;
            ((IConnectionHost)model).OpenTab(Config("navigation"));
            TerminalTabViewModel terminal = Assert.Single(model.Tabs);
            model.SelectWorkspaceTabCommand.Execute(starter);
            HeadlessAvalonia.Pump();
            InputElement input = window.GetVisualDescendants().OfType<TextBox>().First();
            bool receivedNavigation = false;
            input.AddHandler(InputElement.KeyDownEvent, (_, _) => receivedNavigation = true, RoutingStrategies.Bubble);
            input.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.D2,
                KeyModifiers = AppShortcuts.NewTab.KeyModifiers
            });
            Assert.Same(terminal, model.ActiveWorkspaceTab);
            Assert.False(receivedNavigation);

            // 分屏后只导航当前标签组。
            IDocumentDock group = Assert.IsAssignableFrom<IDocumentDock>(model.Workspace.FindDocument(terminal)!.Owner);
            model.Workspace.SplitTab(terminal, group, DockOperation.Right);
            model.SelectWorkspaceTabCommand.Execute(terminal);
            model.SelectWorkspaceTabByIndexCommand.Execute(0);
            model.MoveWorkspaceTabSelectionCommand.Execute(1);
            Assert.Same(terminal, model.ActiveWorkspaceTab);
        }
        finally
        {
            window.Close();
            await model.DisposeAsync();
        }
    });

    [Theory]
    [InlineData(Key.D1)]
    [InlineData(Key.D5)]
    [InlineData(Key.D9)]
    [InlineData(Key.OemMinus)]
    [InlineData(Key.OemPlus)]
    public void TabShortcuts_CannotBeAssignedToCommandPalette(Key key)
    {
        string shortcut = AppShortcuts.Serialize(new KeyGesture(key, AppShortcuts.NewTab.KeyModifiers));
        Assert.False(AppShortcuts.TryParsePaletteShortcut(shortcut, out _));
    }
}
