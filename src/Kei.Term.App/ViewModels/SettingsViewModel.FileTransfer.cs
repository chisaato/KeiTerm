using System;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Models;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

public partial class SettingsViewModel
{
    private void LoadFileTransferSettings(AppSettings settings)
    {
        FileTransferSettings transfer = settings.FileTransfer;
        _fileTransfer.SetSizeDisplayMode(transfer.SizeDisplayMode);
        _fileTransfer.CacheDirectory = transfer.CacheDirectory;
        _fileTransfer.SelectedWatcherMode = transfer.WatcherMode.ToString();
        _fileTransfer.PollingIntervalSeconds = Math.Clamp(transfer.PollingIntervalSeconds, 1, 3600);
        _fileTransfer.WriteDebounceMilliseconds = Math.Clamp(transfer.WriteDebounceMilliseconds, 200, 60000);
        _fileTransfer.CustomEditorPath = transfer.CustomEditorPath;
        _fileTransfer.IsFileManagerOnLeft = transfer.IsFileManagerOnLeft;
    }

    private void ApplyFileTransferSettings(AppSettings settings)
    {
        FileTransferSettings transfer = settings.FileTransfer;
        transfer.SizeDisplayMode = _fileTransfer.SelectedSizeDisplay.Mode;
        transfer.CacheDirectory = _fileTransfer.CacheDirectory.Trim();
        transfer.WatcherMode = Enum.TryParse(_fileTransfer.SelectedWatcherMode, out FileWatcherMode mode)
            ? mode : FileWatcherMode.Auto;
        // 与 LocalFileTracker 的最小周期保持一致，避免界面显示的值与实际监视行为不同。
        transfer.PollingIntervalSeconds = Math.Clamp(_fileTransfer.PollingIntervalSeconds, 1, 3600);
        transfer.WriteDebounceMilliseconds = Math.Clamp(_fileTransfer.WriteDebounceMilliseconds, 200, 60000);
        transfer.CustomEditorPath = _fileTransfer.CustomEditorPath.Trim();
        transfer.IsFileManagerOnLeft = _fileTransfer.IsFileManagerOnLeft;
    }

    private void NotifyFileDisplaySettings()
        => WeakReferenceMessenger.Default.Send(new FileSizeDisplayChangedMessage(_settingsService.Current.FileTransfer.SizeDisplayMode));
}
