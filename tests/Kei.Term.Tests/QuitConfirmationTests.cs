using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

public class QuitConfirmationTests
{
    [Theory]
    [InlineData(QuitTrigger.Application, false)]
    [InlineData(QuitTrigger.Application, true)]
    [InlineData(QuitTrigger.CloseWindow, true)]
    public Task DoNotAskAgain_PersistsOnlyOnConfirmation_AndRestartSkipsPrompt(QuitTrigger trigger, bool repeatRequest) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_quit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        JsonSettingsService settings = new(path);
        await settings.SaveSettingsAsync(new AppSettings { ConfirmBeforeClose = true });
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow(settings);
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); }, settings);
        window.SetQuitConfirmationService(quit);
        try
        {
            window.Show();
            Task first = quit.RequestAsync(trigger);
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            prompt.FindControl<CheckBox>("DoNotAskAgainCheckBox")!.IsChecked = true;
            if (repeatRequest) await quit.RequestAsync(trigger);
            else prompt.FindControl<Button>("ExitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);

            JsonSettingsService restartedSettings = new(path);
            await restartedSettings.LoadSettingsAsync();
            Assert.False(restartedSettings.Current.ConfirmBeforeClose);
            (MainWindow restartedWindow, MainViewModel restartedModel, IInteractionService restartedInteraction) = CreateWindow(restartedSettings);
            TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using QuitConfirmationService restartedQuit = new(restartedInteraction, async () =>
            {
                await restartedWindow.PrepareForQuitAsync();
                restartedWindow.Close();
                exited.TrySetResult();
            }, restartedSettings);
            restartedWindow.SetQuitConfirmationService(restartedQuit);
            try
            {
                restartedWindow.Show();
                if (trigger == QuitTrigger.CloseWindow)
                    await restartedModel.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
                else restartedWindow.Close();
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(restartedWindow.IsVisible);
                Assert.Empty(restartedWindow.OwnedWindows);
            }
            finally { await restartedWindow.PrepareForQuitAsync(); restartedWindow.Close(); }
        }
        finally { await window.PrepareForQuitAsync(); window.Close(); Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task DoNotAskAgain_WhenCancelled_DoesNotSuppressTheNextPrompt() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_quit_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        JsonSettingsService settings = new(path);
        await settings.SaveSettingsAsync(new AppSettings { ConfirmBeforeClose = true });
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow(settings);
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); }, settings);
        window.SetQuitConfirmationService(quit);
        try
        {
            window.Show();
            Task first = quit.RequestAsync();
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            prompt.FindControl<CheckBox>("DoNotAskAgainCheckBox")!.IsChecked = true;
            prompt.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            Assert.True(reader.Current.ConfirmBeforeClose);
            Task next = quit.RequestAsync();
            HeadlessAvalonia.Pump();
            prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            Assert.False(prompt.DoNotAskAgain);
            prompt.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsVisible);
        }
        finally { await window.PrepareForQuitAsync(); window.Close(); Directory.Delete(directory, recursive: true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SystemClose_UsesApplicationQuitPrompt_AndCanCancelOrConfirm(bool hasTerminal) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using QuitConfirmationService quit = new(interaction, async () =>
        {
            await window.PrepareForQuitAsync();
            window.Close();
            exited.TrySetResult();
        });
        window.SetQuitConfirmationService(quit);
        try
        {
            window.Show();
            if (hasTerminal)
                ((IConnectionHost)model).OpenTab(new(Guid.NewGuid(), "LAN", "lan.example", 22, "ops", null,
                    "xterm-256color", null, null, new Dictionary<string, string>()));

            // Close 进入与平台窗口关闭按钮相同的 OnClosing 路径。
            window.Close();
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            Assert.Equal(WindowDecorations.None, prompt.WindowDecorations);
            Assert.Equal(Strings.Format("Quit.Confirm.Message", AppShortcuts.Label(AppShortcuts.QuitApplication)),
                prompt.FindControl<TextBlock>("ConfirmationMessage")!.Text);
            Assert.True(prompt.FindControl<Button>("ExitButton")!.IsEffectivelyVisible);
            Assert.True(prompt.FindControl<Button>("CancelButton")!.IsEffectivelyVisible);
            Assert.True(window.IsVisible);

            RawInputModifiers modifiers = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            prompt.KeyPress(Key.W, modifiers, PhysicalKey.None, null);
            HeadlessAvalonia.Pump();
            Assert.Same(prompt, Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>()));
            Assert.True(window.IsVisible);
            Assert.Equal(hasTerminal ? 1 : 0, model.WorkspaceTabs.Count);
            prompt.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            HeadlessAvalonia.Pump();
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.IsVisible);
            Assert.Equal(hasTerminal ? 1 : 0, model.WorkspaceTabs.Count);

            window.Close();
            HeadlessAvalonia.Pump();
            prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            prompt.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessAvalonia.Pump();
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.IsVisible);

            window.Close();
            HeadlessAvalonia.Pump();
            prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            prompt.KeyPress(Key.Q, modifiers, PhysicalKey.None, null);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
            Assert.Empty(model.WorkspaceTabs);
            Assert.Empty(window.OwnedWindows);
        }
        finally { await window.PrepareForQuitAsync(); window.Close(); }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public Task CloseShortcut_UsesQuitConfirmationWhenNoTabsRemain_AndConfirmsItOnRepeat(int count) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        window.SetQuitConfirmationService(quit);
        try
        {
            window.Show();
            for (int index = 0; index < count; index++) model.NewTabCommand.Execute(null);
            if (count > 0)
            {
                await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
                Assert.Single(model.WorkspaceTabs);
                Assert.Empty(window.OwnedWindows);
                await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
                HeadlessAvalonia.Pump();
                Assert.Empty(model.WorkspaceTabs);
                Assert.Empty(window.OwnedWindows);
                Assert.True(window.IsVisible);
            }
            await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            Assert.Empty(model.WorkspaceTabs);
            QuitConfirmationWindow fromClose = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            Assert.Contains(fromClose.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text == Strings.Format("Quit.Confirm.Message", AppShortcuts.Label(AppShortcuts.CloseTab)));
            Assert.True(window.IsVisible);
            // ⌘Q 不能确认由 ⌘W 打开的弹窗，也不能改掉原来的确认键。
            await quit.RequestAsync(QuitTrigger.Application);
            HeadlessAvalonia.Pump();
            Assert.Same(fromClose, Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>()));
            Assert.True(window.IsVisible);
            await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            Assert.Empty(window.OwnedWindows);
            Assert.False(window.IsVisible);
        }
        finally { await window.PrepareForQuitAsync(); window.Close(); }
    });

    [Fact]
    public Task CloseShortcut_IsIgnoredInQuitPrompt_WithoutClosingTheTabBehindIt() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        window.SetQuitConfirmationService(quit);
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            NewTabViewModel tab = Assert.Single(model.NewTabs);
            Task first = quit.RequestAsync();
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            Assert.False(first.IsCompleted);
            Assert.Same(prompt, Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>()));
            RawInputModifiers modifiers = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            prompt.KeyPress(Key.W, modifiers, PhysicalKey.None, null);
            HeadlessAvalonia.Pump();
            Assert.False(first.IsCompleted);
            Assert.Same(prompt, Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>()));
            Assert.Equal(Strings.Get("Quit.Confirm.Detail"), prompt.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Name == "ConfirmationDetail").Text);
            prompt.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(tab, Assert.Single(model.WorkspaceTabs));
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.IsVisible);
        }
        finally { await window.PrepareForQuitAsync(); window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Buttons_ConfirmOrCancelQuit_InSingleSurfaceWithoutTitleBar(bool confirm) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            Task first = quit.RequestAsync();
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            Assert.Equal(WindowDecorations.None, prompt.WindowDecorations);
            Border content = Assert.IsType<Border>(prompt.Content);
            Assert.Null(content.Background);
            Button exit = prompt.FindControl<Button>("ExitButton")!;
            Button cancel = prompt.FindControl<Button>("CancelButton")!;
            Assert.True(exit.IsEffectivelyVisible);
            Assert.True(cancel.IsEffectivelyVisible);
            Assert.False(exit.IsDefault);
            (confirm ? exit : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!confirm, window.IsVisible);
            Assert.Equal(confirm ? 0 : 1, model.WorkspaceTabs.Count);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task SecondRequest_ReleasesConnectedTerminal_ThenClosesWithoutAnotherPrompt() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        DeferredSession session = new();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        try
        {
            window.Show();
            IConnectionTarget target = ((IConnectionHost)model).OpenTab(new(Guid.NewGuid(), "LAN", "lan.example", 22, "ops", null,
                "xterm-256color", null, null, new Dictionary<string, string>()));
            target.AttachSession(session);
            target.MarkConnected();
            model.CurrentSettings.ConfirmBeforeClose = true;
            Task first = quit.RequestAsync();
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            Assert.True(prompt.IsVisible);
            Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text == Strings.Format("Quit.Confirm.Message", AppShortcuts.Label(AppShortcuts.QuitApplication)));
            Assert.Single(model.WorkspaceTabs);
            Task second = quit.RequestAsync();
            await session.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsVisible);
            Assert.False(second.IsCompleted);
            session.AllowDisposal.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(session.IsConnected);
            Assert.Empty(model.WorkspaceTabs);
            Assert.False(window.IsVisible);
        }
        finally { session.AllowDisposal.TrySetResult(); window.Close(); await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData(QuitTrigger.Application)]
    public Task CancelKeys_CancelConfirmation_AndNextRequestStartsAgain(QuitTrigger trigger) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            Task first = quit.RequestAsync(trigger);
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            prompt.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsVisible);
            Assert.Single(model.WorkspaceTabs);
            Task next = quit.RequestAsync(trigger);
            HeadlessAvalonia.Pump();
            Assert.True(window.IsVisible);
            Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            await quit.RequestAsync(trigger);
            await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData(QuitTrigger.CloseWindow, Key.W)]
    public Task MatchingKeyInDialog_ConfirmsOriginalShortcut(QuitTrigger trigger, Key key) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        try
        {
            window.Show();
            Task first = quit.RequestAsync(trigger);
            HeadlessAvalonia.Pump();
            QuitConfirmationWindow prompt = Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            RawInputModifiers modifiers = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            if (trigger == QuitTrigger.CloseWindow)
            {
                prompt.KeyPress(Key.Q, modifiers, PhysicalKey.None, null);
                HeadlessAvalonia.Pump();
                Assert.True(prompt.IsVisible);
                Assert.False(first.IsCompleted);
            }
            prompt.KeyPress(key, modifiers, PhysicalKey.None, null);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task Confirmation_RemainsOpenPastFiveSeconds_AndMatchingRequestExits() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        try
        {
            window.Show();
            Task first = quit.RequestAsync(QuitTrigger.Application);
            // 真实等待超过原来的超时期限，确保定时取消回归时测试会失败。两种 trigger 行为相同，只留一行以免付两次 6 秒。
            await Task.Delay(TimeSpan.FromSeconds(6));
            HeadlessAvalonia.Pump();
            Assert.True(window.IsVisible);
            Assert.True(Assert.Single(window.OwnedWindows.OfType<QuitConfirmationWindow>()).IsVisible);
            Assert.False(first.IsCompleted);
            await quit.RequestAsync(QuitTrigger.Application);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task ChildWindow_ReceivesVisibleQuitConfirmation() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        (MainWindow window, MainViewModel model, IInteractionService interaction) = CreateWindow();
        using QuitConfirmationService quit = new(interaction, async () => { await window.PrepareForQuitAsync(); window.Close(); });
        try
        {
            window.Show();
            Window child = new() { Title = "Settings" };
            Task childClosed = child.ShowDialog(window);
            Task first = quit.RequestAsync();
            HeadlessAvalonia.Pump();
            Assert.Empty(window.OwnedWindows.OfType<QuitConfirmationWindow>());
            Assert.True(Assert.Single(child.OwnedWindows.OfType<QuitConfirmationWindow>()).IsVisible);
            await quit.RequestAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await childClosed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
            Assert.False(child.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    private static (MainWindow, MainViewModel, IInteractionService) CreateWindow(ISettingsService? settingsService = null)
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = VaultTestDb.CreateVault(database);
        SqliteIdentityRepository identities = new(database);
        ISettingsService settings = settingsService ?? new FixedSettingsService();
        MainViewModel model = new(new SqliteTreeRepository(database), identities, vault, vault, settings, new SshSessionFactory());
        MainWindow window = new() { DataContext = model };
        MainWindowInteractionService interaction = new(window, model, new IdentityManagerViewModel(identities, vault, vault),
            new SettingsViewModel(settings), null, NullLogger.Instance);
        model.Interaction = interaction;
        return (window, model, interaction);
    }

    private sealed class DeferredSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected { get; private set; } = true;
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CS0067
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            await AllowDisposal.Task;
            IsConnected = false;
        }
    }

}
