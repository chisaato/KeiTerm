namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

public partial class RemoteFileManagerViewModel
{
    private ConnectionTransferBoard? _board;
    private bool _ownsTransferBoard;
    private string? _transferUnavailableReason;

    internal ConnectionTransferBoard? TransferBoard => _owningFileView?._board ?? _board;

    internal void UseTransferBoard(ConnectionTransferBoard board, bool owns)
    {
        if (_owningFileView != null) return;
        _board = board;
        _ownsTransferBoard = owns;
    }

    internal void NoteTransferUnavailable(string? reason)
    {
        if (_owningFileView != null)
        {
            _owningFileView.NoteTransferUnavailable(reason);
            return;
        }

        _transferUnavailableReason = reason;
        if (!string.IsNullOrWhiteSpace(reason)) TransferStatusMessage = reason;
    }

    private bool EnsureTransfers()
    {
        if (TransferBoard != null) return true;
        TransferStatusMessage = _transferUnavailableReason ?? "传输通道不可用";
        return false;
    }

    [RelayCommand]
    public Task DownloadAsync(RemoteFileItem? singleItem)
        => DownloadToAsync(singleItem, ResolveDownloadDirectory());

    [RelayCommand]
    public async Task DownloadToAsync(RemoteFileItem? singleItem)
    {
        string? directory = await (_interaction ?? NullInteractionService.Instance).PickFolderAsync(Strings.Get("FileManager.DownloadToDialogTitle"));
        if (string.IsNullOrWhiteSpace(directory)) return;
        await DownloadToAsync(singleItem, directory);
    }

    [RelayCommand]
    public Task UploadLocalFilesAsync(string[] filePaths)
    {
        if (filePaths == null || filePaths.Length == 0 || _disposed || !EnsureTransfers()) return Task.CompletedTask;
        ShowFileActivities(0);
        TransferBoard!.EnqueueUploads(filePaths, CurrentPath);
        return Task.CompletedTask;
    }

    [RelayCommand]
    public void RetryAllFailedTransfers() => TransferBoard?.RetryAllFailed();

    private Task DownloadToAsync(RemoteFileItem? singleItem, string directory)
    {
        if (_disposed || string.IsNullOrWhiteSpace(directory) || !EnsureTransfers()) return Task.CompletedTask;
        List<RemoteFileItem> targets = SelectedItems.Count > 0
            ? SelectedItems.Where(item => item.Name != "..").ToList()
            : singleItem is { Name: not ".." } ? [singleItem] : [];
        if (targets.Count == 0) return Task.CompletedTask;
        ShowFileActivities(0);
        TransferBoard!.EnqueueDownloads(targets, directory);
        return Task.CompletedTask;
    }

    private string ResolveDownloadDirectory()
    {
        string directory = _settingsService?.Current.FileTransfer.ResolveDownloadDirectory()
            ?? DownloadDirectoryResolver.Resolve(null);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public int ActiveTransferCount => TransferTasks.Count(task => task.State is FileTransferState.Transferring or FileTransferState.Packing or FileTransferState.Verifying or FileTransferState.Unpacking);

    public double TotalTransferSpeedMBs => TransferTasks.Where(task => task.State == FileTransferState.Transferring).Sum(task => task.SpeedMBs);

    public string TotalTransferSpeedText => TotalTransferSpeedMBs > 0.01 ? $"{TotalTransferSpeedMBs:F1} MB/s" : string.Empty;

    public string TransferActivitySummaryText
    {
        get
        {
            int count = ActiveTransferCount;
            if (count == 0) return string.Empty;
            string countText = Strings.Format("FileActivity.Summary.Active", count);
            double speed = TotalTransferSpeedMBs;
            return speed > 0.01 ? $"{countText} · {speed:F1} MB/s" : countText;
        }
    }

    private void UpdateOverallTransferringState()
    {
        IsTransferring = TransferTasks.Any(task => task.State is FileTransferState.Pending
            or FileTransferState.Transferring
            or FileTransferState.Packing
            or FileTransferState.Verifying
            or FileTransferState.Unpacking
            or FileTransferState.AwaitingConflict
            or FileTransferState.RetryWait);
        OnPropertyChanged(nameof(ActiveTransferCount));
        OnPropertyChanged(nameof(TotalTransferSpeedMBs));
        OnPropertyChanged(nameof(TotalTransferSpeedText));
        OnPropertyChanged(nameof(TransferActivitySummaryText));
    }
}
