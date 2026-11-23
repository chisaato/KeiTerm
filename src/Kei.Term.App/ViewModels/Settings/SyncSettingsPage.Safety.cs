using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Sync;

namespace Kei.Term.App.ViewModels.Settings;

public partial class SyncSettingsPage
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteLocalBackupsCommand))]
    private IReadOnlyList<SyncLocalBackup> _localBackups = Array.Empty<SyncLocalBackup>();
    [ObservableProperty] private IReadOnlyList<SyncHistoryEntry> _remoteHistory = Array.Empty<SyncHistoryEntry>();
    [ObservableProperty] private SyncLocalBackup? _selectedLocalBackup;
    [ObservableProperty] private SyncHistoryEntry? _selectedRemoteHistory;
    [ObservableProperty] private bool _hasPendingPassphraseChange;

    private async Task CheckPendingPassphraseChangeAsync()
    {
        if (_secrets is not ISyncSafetyStore safety) return;
        try
        {
            char[]? pending = await safety.GetPendingPassphraseAsync();
            HasPendingPassphraseChange = pending != null;
            if (pending != null) Array.Clear(pending);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"检查待处理同步口令状态失败: {ex}");
        }
    }

    private async Task<SyncResult> CompleteManualAsync(ISyncLocation location, SyncResult result)
    {
        if (_coordinator == null) return result;
        while (result.Confirmation is { } preview)
        {
            SyncApproval approval;
            if (preview.Kind == SyncConfirmationKind.FirstJoin)
            {
                var choice = await Interaction.PromptSyncJoinAsync(preview);
                if (choice == null) return new(false, "已取消首次加入");
                approval = new(preview.Token, choice.Value);
            }
            else
            {
                if (!await Interaction.ConfirmSyncDeletionAsync(preview)) return new(false, "已取消批量删除");
                approval = new(preview.Token);
            }
            result = await Task.Run(() => _coordinator.SyncApprovedAsync(location, approval));
        }
        if (result.Message == "同步口令不正确")
        {
            string? password = await Interaction.PromptSyncPassphraseAsync(false, result.Message);
            if (!string.IsNullOrEmpty(password))
            {
                await _secrets.SavePassphraseAsync(password.AsMemory());
                result = await CompleteManualAsync(location, await Task.Run(() => _coordinator.SyncAsync(location)));
            }
        }
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanUseCoordinator))]
    private async Task RefreshRecoveryAsync()
    {
        if (_coordinator == null || IsBusy) return;
        IsBusy = true;
        ISyncLocation? location = null;
        try
        {
            LocalBackups = await _coordinator.ListBackupsAsync();
            await CheckPendingPassphraseChangeAsync();
            location = await OpenLocationAsync();
            RemoteHistory = await location.ListHistoryAsync();
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { Release(location); IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteLocalBackups))]
    private async Task DeleteLocalBackupsAsync()
    {
        if (IsBusy || _secrets is not ISyncSafetyStore safety) return;
        if (!await Interaction.ConfirmDeleteAsync("永久删除全部本机同步快照（不删除当前数据或远端历史）")) return;
        IsBusy = true;
        try
        {
            await safety.DeleteSyncBackupsAsync();
            LocalBackups = Array.Empty<SyncLocalBackup>();
            SelectedLocalBackup = null;
            SetStatus(Strings.Get("Settings.Sync.Status.SnapshotDeleted"));
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreLocalAsync()
    {
        if (_coordinator == null || SelectedLocalBackup == null || IsBusy) return;
        if (!await Interaction.ConfirmDeleteAsync("用所选本机快照恢复全部同步数据（不影响本机专属项）")) return;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _coordinator.RestoreLocalAsync(SelectedLocalBackup.Id));
            SetStatus(result.Message, isError: !result.Success);
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreHistoryAsync()
    {
        if (_coordinator == null || SelectedRemoteHistory == null || IsBusy) return;
        if (!await Interaction.ConfirmDeleteAsync("用所选远端历史恢复全部同步数据（不影响本机专属项）")) return;
        IsBusy = true;
        ISyncLocation? location = null;
        try
        {
            location = await OpenLocationAsync();
            var result = await Task.Run(() => _coordinator.RestoreHistoryAsync(location, SelectedRemoteHistory.Name));
            SetStatus(result.Message, isError: !result.Success);
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { Release(location); IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUseCoordinator))]
    private async Task ChangePassphraseAsync()
    {
        if (_coordinator == null || IsBusy) return;
        string? password = await Interaction.PromptSyncPassphraseAsync(true);
        if (string.IsNullOrEmpty(password)) return;
        IsBusy = true;
        ISyncLocation? location = null;
        try
        {
            location = await OpenLocationAsync();
            var result = await Task.Run(() => _coordinator.ChangePassphraseAsync(location, password.AsMemory()));
            if (result.Confirmation != null)
            {
                result = await CompleteManualAsync(location, result);
                if (result.Success) result = await Task.Run(() => _coordinator.ChangePassphraseAsync(location, password.AsMemory()));
            }
            SetStatus(result.Message, isError: !result.Success);
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { Release(location); IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUseCoordinator))]
    private async Task ResumePassphraseChangeAsync()
    {
        if (_coordinator == null || IsBusy) return;
        IsBusy = true;
        ISyncLocation? location = null;
        try
        {
            location = await OpenLocationAsync();
            var result = await Task.Run(() => _coordinator.ResumePassphraseChangeAsync(location));
            SetStatus(result.Message, isError: !result.Success);
            if (result.Success) HasPendingPassphraseChange = false;
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { Release(location); IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUseCoordinator))]
    private async Task DisableAsync()
    {
        if (_coordinator == null || IsBusy) return;
        bool? delete = await Interaction.PromptDisableSyncAsync();
        if (delete == null) return;
        IsBusy = true;
        ISyncLocation? location = null;
        try
        {
            location = await OpenLocationAsync();
            var result = await Task.Run(() => _coordinator.DisableAsync(location, delete.Value));
            SetStatus(result.Message, isError: !result.Success);
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { SetStatus(ex.Message, isError: true); }
        finally { Release(location); IsBusy = false; }
    }
}
