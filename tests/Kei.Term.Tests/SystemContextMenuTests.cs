namespace Kei.Term.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services.ContextMenus;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.App.Views.Controls;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Xunit;

public class SystemContextMenuTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setting_RealControlSavesAndReloads_WithoutResettingOtherPreferences(bool enabled)
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_context_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            await service.SaveSettingsAsync(new AppSettings
            {
                UseNativeContextMenus = !enabled,
                UseNativeGlobalMenu = false,
                LastSessionManagerVisible = false,
            });
            SettingsViewModel? viewModel = null;
            await HeadlessAvalonia.RunAsync(() =>
            {
                viewModel = new SettingsViewModel(service, directory);
                GeneralSettingsPage page = viewModel.Categories.Select(c => c.Page).OfType<GeneralSettingsPage>().Single();
                SettingsWindow window = new(viewModel);
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    CheckBox checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single(c =>
                        Equals(c.Content, Strings.Get("Settings.General.UseNativeContextMenus")));
                    Assert.True(checkbox.IsEnabled);
                    Assert.Equal(!enabled, checkbox.IsChecked);
                    checkbox.IsChecked = enabled;
                    Assert.Equal(enabled, page.UseNativeContextMenus);
                }
                finally { window.Close(); }
            });
            Assert.True(await viewModel!.ApplyChangesAsync());
            JsonSettingsService reloaded = new(path);
            await reloaded.LoadSettingsAsync();
            Assert.Equal(enabled, reloaded.Current.UseNativeContextMenus);
            Assert.False(reloaded.Current.UseNativeGlobalMenu);
            Assert.False(reloaded.Current.LastSessionManagerVisible);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public Task NativeSelection_UsesHostBindings_DynamicItemsAndOriginalClickCommand() => HeadlessAvalonia.RunAsync(() =>
    {
        int clicked = 0;
        object parameter = new();
        MenuModel model = new(new RelayCommand<object>(value => { Assert.Same(parameter, value); clicked++; }));
        MenuItem action = new() { Header = "启用", ToggleType = MenuItemToggleType.CheckBox, CommandParameter = parameter };
        action.Bind(MenuItem.CommandProperty, new Binding(nameof(MenuModel.Action)));
        ContextMenu menu = new();
        menu.Items.Add(new Separator());
        menu.Items.Add(action);
        menu.Items.Add(new MenuItem { Header = "隐藏", IsVisible = false });
        menu.Items.Add(new MenuItem { Header = "禁用", IsEnabled = false });
        menu.Items.Add(new Separator());
        MenuItem editors = new() { Header = "打开方式" };
        menu.Items.Add(editors);
        menu.Items.Add(new Separator());
        menu.Opening += (_, _) => editors.Items.Add(new MenuItem { Header = "本地编辑器" });
        Button owner = new() { Content = "右键", ContextMenu = menu };
        Window window = new() { Content = owner, DataContext = model };
        RecordingPresenter presenter = new();
        using SystemContextMenuController controller = new(window, () => true, presenter);
        try
        {
            window.Show();
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.Equal(1, clicked);
            Assert.True(action.IsChecked);
            Assert.False(menu.IsOpen);
            Assert.Null(menu.Parent);
            Assert.Equal(new[] { "启用", "禁用", "", "打开方式" }, presenter.Snapshot!.Entries.Select(e => e.Label));
            Assert.False(presenter.Snapshot.Entries[1].IsEnabled);
            Assert.Equal("本地编辑器", Assert.Single(presenter.Snapshot.Entries[^1].Children).Label);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public Task DisabledUnsupportedOrFailedPresenter_OpensExistingAvaloniaMenu(bool enabled, bool supported, bool succeeds)
        => HeadlessAvalonia.RunAsync(() =>
    {
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "复制" });
        Button owner = new() { ContextMenu = menu };
        Window window = new() { Content = owner };
        RecordingPresenter presenter = new() { Supported = supported, Succeeds = succeeds };
        using SystemContextMenuController controller = new(window, () => enabled, presenter);
        try
        {
            window.Show();
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.True(menu.IsOpen);
        }
        finally { menu.Close(); window.Close(); }
    });

    [Fact]
    public Task CancelledOpening_DoesNotShowEitherMenu() => HeadlessAvalonia.RunAsync(() =>
    {
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "删除" });
        menu.Opening += (_, opening) => opening.Cancel = true;
        Button owner = new() { ContextMenu = menu };
        Window window = new() { Content = owner };
        RecordingPresenter presenter = new();
        using SystemContextMenuController controller = new(window, () => true, presenter);
        try
        {
            window.Show();
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.False(menu.IsOpen);
            Assert.Null(presenter.Snapshot);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ChangedSetting_IsReadOnNextRequest_AndDoesNotAccumulateOpeningHandlers() => HeadlessAvalonia.RunAsync(() =>
    {
        bool enabled = true;
        int clicked = 0;
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "复制", Command = new RelayCommand(() => clicked++) });
        Button owner = new() { ContextMenu = menu };
        Window window = new() { Content = owner };
        RecordingPresenter presenter = new();
        using SystemContextMenuController controller = new(window, () => enabled, presenter);
        try
        {
            window.Show();
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.Equal(1, clicked);
            enabled = false;
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.True(menu.IsOpen);
            Assert.Equal(1, clicked);
            menu.Close();
            enabled = true;
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.False(menu.IsOpen);
            Assert.Equal(2, clicked);
        }
        finally { menu.Close(); window.Close(); }
    });

    [Fact]
    public Task SelectionRechecksCanExecute_AndCustomMenuContentFallsBack() => HeadlessAvalonia.RunAsync(() =>
    {
        bool canExecute = true;
        int clicked = 0;
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "删除", Command = new RelayCommand(() => clicked++, () => canExecute) });
        ContextMenuSnapshot snapshot = ContextMenuSnapshot.Create(menu)!;
        canExecute = false;
        snapshot.Invoke(snapshot.Entries[0].Id);
        Assert.Equal(0, clicked);
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "自定义内容" } });
        Assert.Null(ContextMenuSnapshot.Create(menu));
    });

    private sealed record MenuModel(IRelayCommand<object> Action);

    [Fact]
    public Task RealMainWindow_SessionContextMenu_PreservesCompiledParentCommandBinding() => HeadlessAvalonia.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_context_xaml_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        Window? window = null;
        try
        {
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "test.db")}");
            InternalVaultManager vault = new(database);
            MainViewModel viewModel = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database),
                vault, vault, new JsonSettingsService(Path.Combine(directory, "settings.json")), new SshSessionFactory());
            SessionNode session = new() { Name = "测试会话", Host = "test.example" };
            viewModel.TreeNodes.Add(session);
            viewModel.HasNodes = true;
            viewModel.SelectedTreeNode = session;
            window = new MainWindow { DataContext = viewModel };
            RecordingPresenter presenter = new() { SelectedLabel = Strings.Get("Tree.Menu.Rename") };
            using SystemContextMenuController controller = new(window, () => true, presenter);
            window.Show();
            window.UpdateLayout();
            SessionTreeItemView item = window.GetVisualDescendants().OfType<SessionTreeItemView>()
                .Single(control => ReferenceEquals(control.DataContext, session));
            item.RaiseEvent(new ContextRequestedEventArgs());
            Assert.Same(session, viewModel.RenamingNode);
            Assert.NotNull(presenter.Snapshot);
            Assert.False(item.ContextMenu!.IsOpen);
            // 关闭窗口不触发会话关闭确认。
            viewModel.CurrentSettings.ConfirmBeforeClose = false;
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task ApplicationService_CoversNewWindows_AndStopsAfterDisposal() => HeadlessAvalonia.RunAsync(() =>
    {
        int clicked = 0;
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "复制", Command = new RelayCommand(() => clicked++) });
        Button owner = new() { ContextMenu = menu };
        Window window = new() { Content = owner };
        using SystemContextMenuService service = new(() => true, new RecordingPresenter());
        try
        {
            window.Show();
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.Equal(1, clicked);
            service.Dispose();
            owner.RaiseEvent(new ContextRequestedEventArgs());
            Assert.True(menu.IsOpen);
            Assert.Equal(1, clicked);
        }
        finally { menu.Close(); window.Close(); }
    });

    // 只替代操作系统显示层；事件路由、绑定、菜单打开与命令执行全部使用生产代码。
    private sealed class RecordingPresenter : ISystemContextMenuPresenter
    {
        public bool Supported { get; init; } = true;
        public bool Succeeds { get; init; } = true;
        public string? SelectedLabel { get; init; }
        public ContextMenuSnapshot? Snapshot { get; private set; }
        public bool IsSupported(TopLevel topLevel) => Supported;
        public bool TryShow(Control owner, Point position, ContextMenuSnapshot snapshot, out int selectedId)
        {
            Snapshot = snapshot;
            selectedId = SelectedLabel == null ? snapshot.Entries[0].Id
                : snapshot.Entries.Single(entry => entry.Label == SelectedLabel).Id;
            return Succeeds;
        }
    }
}
