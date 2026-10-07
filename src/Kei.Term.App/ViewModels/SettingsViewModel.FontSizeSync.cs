using System;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

// 外部字号（Ctrl+滚轮）与设置草稿的同步：只改字号，不重载其他草稿。
public partial class SettingsViewModel : IDisposable
{
    // 上次从已提交设置同步来的字号。草稿与它一致表示用户没有手改字号。
    private double _syncedFontSize;
    private INotifySettingsCommitted? _fontSizeNotifications;

    private bool IsFontSizeDraftClean
        => Math.Abs(_appearance.FontSize - _syncedFontSize) < 0.001;

    private void SubscribeExternalFontSize()
    {
        if (_settingsService is not INotifySettingsCommitted notifications)
        {
            return;
        }

        _fontSizeNotifications = notifications;
        notifications.SettingsCommitted += OnExternalSettingsCommitted;
    }

    // 应用进程只持有一个设置 VM，订阅随进程结束即可。
    // 类型本身不是单例：额外实例必须 Dispose，否则事件会把 VM 钉在设置服务上。
    public void Dispose()
    {
        if (_fontSizeNotifications == null)
        {
            return;
        }

        _fontSizeNotifications.SettingsCommitted -= OnExternalSettingsCommitted;
        _fontSizeNotifications = null;
    }

    private void OnExternalSettingsCommitted(object? sender, EventArgs e)
    {
        // 自己的保存进行中：快照已冻结，不在这里改草稿
        if (IsBusy || _isReloading)
        {
            return;
        }

        AdoptExternalFontSize(_settingsService.Current.FontSize);
    }

    // 未手改字号才跟外部缩放。手改中的字号保留，避免滚轮冲掉正在编辑的值。
    private void AdoptExternalFontSize(double fontSize)
    {
        if (_isReloading || !IsFontSizeDraftClean)
        {
            return;
        }

        ApplySyncedFontSize(fontSize);
    }

    private void ApplySyncedFontSize(double fontSize)
    {
        if (Math.Abs(_appearance.FontSize - fontSize) >= 0.001)
        {
            _appearance.FontSize = fontSize;
        }

        RememberSyncedFontSize(fontSize);
        // 失败补偿写回的是这份快照；字号必须跟着已提交值，否则补偿会把滚轮结果退回去
        _lastAppliedSettings.FontSize = fontSize;
    }

    private void RememberSyncedFontSize(double fontSize)
    {
        _syncedFontSize = fontSize;
    }

    // 干净草稿用当前已提交字号；手改草稿用用户输入。两者都钳制到全局字号范围。
    private double ResolveFontSizeForSave()
    {
        double raw = IsFontSizeDraftClean ? _settingsService.Current.FontSize : _appearance.FontSize;
        return Math.Clamp(raw, TerminalFontZoom.MinFontSize, TerminalFontZoom.MaxFontSize);
    }
}
