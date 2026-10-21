using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Services;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.App.ViewModels.Settings;

public sealed record SyncLocationKindOption(string Id, string Label);
public sealed record S3ProviderOption(string Id, string Label, bool ConditionalWrites, bool PathStyle);

// 独立子页，不增加设置主 VM 的职责；同步配置立即保存且不进入 settings.json。
public partial class SyncSettingsPage : ViewModelBase
{
    private static readonly SyncLocationKindOption LocalKind = new(SyncLocationKinds.LocalFolder, "本地目录");
    private static readonly S3ProviderOption CustomProvider = new("custom", "其他", true, true);
    private readonly string _dataDirectory;
    private readonly string _statusPath;
    private readonly SyncScheduler _scheduler;
    private readonly ISyncPassphraseStore _secrets;
    private readonly ISyncLocationCredentialStore _credentials;
    private bool _loading;

    public IInteractionService Interaction { get; set; } = NullInteractionService.Instance;
    public event Func<Task>? Synchronized;

    public IReadOnlyList<SyncLocationKindOption> LocationKinds { get; } =
    [
        LocalKind,
        new(SyncLocationKinds.WebDav, "WebDAV"),
        new(SyncLocationKinds.S3, "S3 兼容存储"),
    ];

    public IReadOnlyList<S3ProviderOption> S3Providers { get; } =
    [
        new("aws", "AWS S3", true, false),
        new("r2", "Cloudflare R2", true, true),
        new("minio", "MinIO", true, true),
        new("aliyun", "阿里云 OSS", false, true),
        new("tencent", "腾讯云 COS", false, true),
        CustomProvider,
    ];

    [ObservableProperty] private SyncLocationKindOption _selectedKind = LocalKind;
    [ObservableProperty] private string _directoryPath = "";
    [ObservableProperty] private string _webDavUrl = "";
    [ObservableProperty] private string _webDavUser = "";
    [ObservableProperty] private string _webDavPassword = "";
    [ObservableProperty] private bool _webDavConditionalWrites = true;
    [ObservableProperty] private S3ProviderOption _selectedS3Provider = CustomProvider;
    [ObservableProperty] private string _s3Endpoint = "";
    [ObservableProperty] private string _s3Region = "";
    [ObservableProperty] private string _s3Bucket = "";
    [ObservableProperty] private string _s3Prefix = "";
    [ObservableProperty] private string _s3AccessKeyId = "";
    [ObservableProperty] private string _s3SecretKey = "";
    [ObservableProperty] private bool _s3PathStyle = true;
    [ObservableProperty] private bool _s3ConditionalWrites = true;
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _status = "尚未同步";
    [ObservableProperty] private bool _isBusy;

    public bool IsLocalFolder => SelectedKind.Id == SyncLocationKinds.LocalFolder;
    public bool IsWebDav => SelectedKind.Id == SyncLocationKinds.WebDav;
    public bool IsS3 => SelectedKind.Id == SyncLocationKinds.S3;

    public SyncSettingsPage(string dataDirectory, SyncScheduler scheduler, ISyncPassphraseStore secrets, ISyncLocationCredentialStore credentials)
    {
        _dataDirectory = dataDirectory;
        _statusPath = Path.Combine(dataDirectory, "sync-status.txt");
        _scheduler = scheduler;
        _secrets = secrets;
        _credentials = credentials;
        if (File.Exists(_statusPath)) Status = File.ReadAllText(_statusPath);
        try { Apply(SyncLocationSettingsStore.Load(dataDirectory)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Status = "同步位置配置无法读取：" + ex.Message; }
        scheduler.StatusChanged += ApplyStatus;
        scheduler.Synchronized += OnSynchronizedAsync;
    }

    private void ApplyStatus(string status)
    {
        void Write()
        {
            Status = status;
            try { File.WriteAllText(_statusPath, status); }
            catch (IOException) { }
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) Write();
            else Dispatcher.UIThread.Post(Write);
        }
        catch (InvalidOperationException) { Write(); }
    }

    private Task OnSynchronizedAsync()
    {
        Task Raise() => Synchronized == null ? Task.CompletedTask : Synchronized.Invoke();
        try
        {
            return Dispatcher.UIThread.CheckAccess() ? Raise() : Dispatcher.UIThread.InvokeAsync(Raise);
        }
        catch (InvalidOperationException) { return Raise(); }
    }

    partial void OnSelectedKindChanged(SyncLocationKindOption value)
    {
        OnPropertyChanged(nameof(IsLocalFolder));
        OnPropertyChanged(nameof(IsWebDav));
        OnPropertyChanged(nameof(IsS3));
    }

    partial void OnSelectedS3ProviderChanged(S3ProviderOption value)
    {
        if (_loading || value == null) return;
        S3ConditionalWrites = value.ConditionalWrites;
        S3PathStyle = value.PathStyle;
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
        if (IsBusy) return;
        IsBusy = true;
        ISyncLocation? location = null;
        try
        {
            location = await OpenLocationAsync();
            await location.TestConnectionAsync();
            Status = "同步位置连接成功";
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { Status = ex.Message; }
        finally
        {
            Release(location);
            IsBusy = false;
        }
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
        ISyncLocation? location = null;
        try
        {
            location = await OpenLocationAsync();
            // 先保存位置，再交给调度器。正在进行的一轮不会并行，结束后再补一轮。
            await _scheduler.SyncNowAsync();
        }
        catch (Exception ex) when (IsSyncFailure(ex)) { Status = ex.Message; }
        finally
        {
            Release(location);
            IsBusy = false;
        }
    }

    private async Task<ISyncLocation> OpenLocationAsync()
    {
        var settings = Capture();
        string? webDavPassword = null;
        string? s3Secret = null;
        if (IsWebDav)
        {
            if (string.IsNullOrEmpty(WebDavPassword)) webDavPassword = await _credentials.GetWebDavPasswordAsync();
            else
            {
                webDavPassword = WebDavPassword;
            }
        }
        else if (IsS3)
        {
            if (string.IsNullOrEmpty(S3SecretKey)) s3Secret = await _credentials.GetS3SecretKeyAsync();
            else
            {
                s3Secret = S3SecretKey;
            }
        }
        // 构造会验证地址、必填项与 SDK 配置；无效草稿不能覆盖已保存的位置或凭据。
        var location = SyncLocationFactory.Create(settings, webDavPassword, s3Secret);
        try
        {
            if (IsWebDav && !string.IsNullOrEmpty(WebDavPassword))
                await _credentials.SaveWebDavPasswordAsync(webDavPassword!);
            if (IsS3 && !string.IsNullOrEmpty(S3SecretKey))
                await _credentials.SaveS3SecretKeyAsync(s3Secret!);
            SyncLocationSettingsStore.Save(_dataDirectory, settings);
            if (IsWebDav) WebDavPassword = "";
            if (IsS3) S3SecretKey = "";
            return location;
        }
        catch
        {
            Release(location);
            throw;
        }
    }

    private SyncLocationSettings Capture() => new()
    {
        Kind = SelectedKind.Id,
        Directory = DirectoryPath,
        WebDavUrl = WebDavUrl,
        WebDavUser = WebDavUser,
        WebDavConditionalWrites = WebDavConditionalWrites,
        S3Provider = SelectedS3Provider.Id,
        S3Endpoint = S3Endpoint,
        S3Region = S3Region,
        S3Bucket = S3Bucket,
        S3Prefix = S3Prefix,
        S3AccessKeyId = S3AccessKeyId,
        S3PathStyle = S3PathStyle,
        S3ConditionalWrites = S3ConditionalWrites,
    };

    private void Apply(SyncLocationSettings settings)
    {
        _loading = true;
        try
        {
            SelectedKind = LocationKinds.FirstOrDefault(option => option.Id == settings.Kind) ?? LocalKind;
            DirectoryPath = settings.Directory;
            WebDavUrl = settings.WebDavUrl;
            WebDavUser = settings.WebDavUser;
            WebDavConditionalWrites = settings.WebDavConditionalWrites;
            SelectedS3Provider = S3Providers.FirstOrDefault(option => option.Id == settings.S3Provider) ?? CustomProvider;
            S3Endpoint = settings.S3Endpoint;
            S3Region = settings.S3Region;
            S3Bucket = settings.S3Bucket;
            S3Prefix = settings.S3Prefix;
            S3AccessKeyId = settings.S3AccessKeyId;
            S3PathStyle = settings.S3PathStyle;
            S3ConditionalWrites = settings.S3ConditionalWrites ?? SelectedS3Provider.ConditionalWrites;
        }
        finally { _loading = false; }
    }

    private static void Release(ISyncLocation? location) => (location as IDisposable)?.Dispose();

    private static bool IsSyncFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;
}
