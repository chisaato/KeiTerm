using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Kei.Term.App.Logging;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Views;

public partial class MainWindow
{
    private QuitConfirmationService? _quitService;
    private QuitConfirmationWindow? _quitPrompt;
    private bool _closeConfirmed;

    public void SetQuitConfirmationService(QuitConfirmationService service) => _quitService = service;

    public Task RequestQuitAsync(QuitTrigger trigger) => _quitService?.RequestAsync(trigger) ?? Task.CompletedTask;

    public bool IsQuitConfirmationSuppressionSelected => _quitPrompt?.DoNotAskAgain == true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel) return;

        // 系统关闭按钮也表示退出应用，复用 ⌘Q 的确认、取消与资源排空流程。
        e.Cancel = true;
        _ = Safe.RunAsync(_logger ?? NullLogger.Instance, "确认退出应用", () =>
            _quitService is { } service ? service.RequestAsync(QuitTrigger.Application) : CloseAfterDrainAsync());
    }

    private async Task CloseAfterDrainAsync()
    {
        // 未装配应用生命周期的窗口仍需先释放资源，避免绕过异步排空。
        await PrepareForQuitAsync();
        Close();
    }

    public bool HandleCloseShortcutInQuitConfirmation()
    {
        if (_quitPrompt is not { IsVisible: true } prompt) return false;
        prompt.HandleCloseShortcut();
        return true;
    }

    public async Task<QuitConfirmationResult> ShowQuitConfirmationAsync(QuitTrigger trigger, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return default;
        // 设置、认证等子窗口也必须能看到提示；原生 ⌘Q 仍由应用生命周期接收。
        Window owner = this;
        while (owner.OwnedWindows.LastOrDefault(window => window.IsVisible) is { } child) owner = child;
        owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .Windows.LastOrDefault(window => window.IsActive) ?? owner;
        QuitConfirmationWindow prompt = new(trigger);
        _quitPrompt = prompt;
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => prompt.Close(false)));
        try
        {
            bool confirmed = await prompt.ShowDialog<bool>(owner);
            return new QuitConfirmationResult(confirmed, prompt.DoNotAskAgain);
        }
        finally { if (ReferenceEquals(_quitPrompt, prompt)) _quitPrompt = null; }
    }

    public async Task PrepareForQuitAsync()
    {
        // 确认退出后，排空终端资源再放行窗口关闭。
        if (DataContext is MainViewModel model)
        {
            foreach (ViewModelBase tab in model.WorkspaceTabs.ToArray())
                await model.CloseWorkspaceTabCommand.ExecuteAsync(tab);
            await model.DisposeAsync();
        }
        _closeConfirmed = true;
    }
}
