using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Services;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.App.ViewModels.Settings;

// 独立子页，不增加设置主 VM 的职责；同步配置立即保存且不进入 settings.json。
public partial class SyncSettingsPage : ViewModelBase
{
    private readonly string _configurationPath;
    private readonly string _statusPath;
    private readonly SyncCoordinator _coordinator;
    private readonly ISyncPassphraseStore _secrets;
    public IInteractionService Interaction { get; set; } = NullInteractionService.Instance;
    public event Func<Task>? Synchronized;

    [ObservableProperty] private string _directoryPath = "";
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _status = "尚未同步";
    [ObservableProperty] private bool _isBusy;

    public SyncSettingsPage(string dataDirectory, SyncCoordinator coordinator, ISyncPassphraseStore secrets)
    {
        _configurationPath = Path.Combine(dataDirectory, "sync-location.txt");
        _statusPath = Path.Combine(dataDirectory, "sync-status.txt");
        _coordinator = coordinator;
        _secrets = secrets;
        if (File.Exists(_configurationPath)) DirectoryPath = File.ReadAllText(_configurationPath);
        if (File.Exists(_statusPath)) Status = File.ReadAllText(_statusPath);
    }

    [RelayCommand]
    private async Task ChooseDirectoryAsync()
    {
        string? path = await Interaction.PickFolderAsync("选择同步位置（本地目录）");
        if (path != null) DirectoryPath = path;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) throw new InvalidOperationException("请选择同步目录");
            await new LocalFolderSyncLocation(DirectoryPath).TestConnectionAsync();
            Status = "同步位置连接成功";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task SavePassphraseAsync()
    {
        try
        {
            await _secrets.SavePassphraseAsync(Passphrase.AsMemory());
            Passphrase = "";
            Status = "同步口令已保存到保险库";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) throw new InvalidOperationException("请选择同步目录");
            await File.WriteAllTextAsync(_configurationPath, DirectoryPath);
            // age KDF 在后台执行，不阻塞 Avalonia UI 线程。
            SyncResult result = await Task.Run(() => _coordinator.SyncAsync(new LocalFolderSyncLocation(DirectoryPath)));
            Status = $"{DateTime.Now:g}：{result.Message}" +
                (result.WriterDeviceId == null ? "" : $"\n最后写入设备：{result.WriterDeviceId}\n写入时间：{result.WrittenAtUtc:u}");
            await File.WriteAllTextAsync(_statusPath, Status);
            // 失败也刷新：并发上传重试耗尽之前，本机可能已写入合并结果。
            if (Synchronized != null) await Synchronized.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
