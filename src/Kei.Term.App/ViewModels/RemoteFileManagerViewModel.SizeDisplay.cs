using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Models;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    private bool _sizeDisplayDisposed;

    [ObservableProperty]
    private FileSizeDisplayMode _sizeDisplayMode;

    private void InitializeSizeDisplay()
    {
        SizeDisplayMode = _settingsService?.Current.FileTransfer.SizeDisplayMode ?? FileSizeDisplayMode.Iec;
        WeakReferenceMessenger.Default.Register<RemoteFileManagerViewModel, FileSizeDisplayChangedMessage>(this,
            static (recipient, message) => recipient.UpdateSizeDisplay(message.Mode));
    }

    private void UpdateSizeDisplay(FileSizeDisplayMode mode)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySizeDisplay();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplySizeDisplay);
        }

        void ApplySizeDisplay()
        {
            // 已卸载的文件管理器不再接受排队的设置通知。
            if (!_sizeDisplayDisposed)
            {
                SizeDisplayMode = mode;
            }
        }
    }

    private void DisposeSizeDisplay()
    {
        _sizeDisplayDisposed = true;
        WeakReferenceMessenger.Default.Unregister<FileSizeDisplayChangedMessage>(this);
    }
}
