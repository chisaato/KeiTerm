using System;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.Services;

public enum QuitTrigger
{
    Application,
    CloseWindow
}

public readonly record struct QuitConfirmationResult(bool Confirmed, bool DoNotAskAgain);

// 原生应用菜单与文件菜单共享两次退出确认；用户取消提示后重新等待第一次按键。
public sealed class QuitConfirmationService(IInteractionService interaction, Func<Task> exitAsync, ISettingsService? settings = null) : IDisposable
{
    private CancellationTokenSource? _confirmation;
    private QuitTrigger _trigger;
    private bool _exiting;

    public async Task RequestAsync(QuitTrigger trigger = QuitTrigger.Application)
    {
        if (_exiting) return;
        if (settings?.Current.ConfirmBeforeClose == false)
        {
            await ExitAsync(false);
            return;
        }
        if (_confirmation is { IsCancellationRequested: false })
        {
            // 只有打开确认框的那个快捷键能再次确认，其他退出入口不能误确认。
            if (trigger == _trigger) await ExitAsync();
            return;
        }

        CancellationTokenSource confirmation = new();
        _confirmation = confirmation;
        _trigger = trigger;
        try
        {
            // 确认只由用户操作结束，不设置任何自动取消期限。
            QuitConfirmationResult result = await interaction.ShowQuitConfirmationAsync(trigger, confirmation.Token);
            if (result.Confirmed && ReferenceEquals(_confirmation, confirmation) && !confirmation.IsCancellationRequested)
                await ExitAsync(result.DoNotAskAgain);
        }
        finally
        {
            if (ReferenceEquals(_confirmation, confirmation)) _confirmation = null;
            confirmation.Dispose();
        }
    }

    private async Task ExitAsync(bool? doNotAskAgain = null)
    {
        if (_exiting) return;
        _exiting = true;
        // 原生二次快捷键可能先取消窗口的模态任务，必须在关闭提示前读取复选框。
        bool remember = doNotAskAgain ?? interaction.IsQuitConfirmationSuppressionSelected;
        CancellationTokenSource? pending = _confirmation;
        _confirmation = null;
        pending?.Cancel();
        try
        {
            // 只有最终确认退出才保存；取消弹窗不会改变下一次的提示行为。
            if (remember && settings != null)
            {
                settings.Current.ConfirmBeforeClose = false;
                await settings.SaveSettingsAsync(settings.Current);
            }
            await exitAsync();
        }
        catch { _exiting = false; throw; }
    }

    public void Dispose()
    {
        _confirmation?.Cancel();
        _confirmation = null;
    }
}
