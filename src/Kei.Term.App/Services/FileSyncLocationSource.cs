using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.App.Services;

// 读取同步页已经保存的位置配置，再用现有工厂打开。密码仍从保险库取，不在这里另存一份。
public sealed class FileSyncLocationSource(string dataDirectory, ISyncLocationCredentialStore credentials) : ISyncLocationSource
{
    public bool HasConfiguration
    {
        get
        {
            try { return IsFilled(SyncLocationSettingsStore.Load(dataDirectory)); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { return false; }
        }
    }

    public async Task<ISyncLocation?> OpenAsync(CancellationToken cancellationToken = default)
    {
        SyncLocationSettings settings;
        try { settings = SyncLocationSettingsStore.Load(dataDirectory); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { return null; }
        if (!IsFilled(settings)) return null;
        try
        {
            string? webDavPassword = settings.Kind == SyncLocationKinds.WebDav
                ? await credentials.GetWebDavPasswordAsync(cancellationToken).ConfigureAwait(false) : null;
            string? s3Secret = settings.Kind == SyncLocationKinds.S3
                ? await credentials.GetS3SecretKeyAsync(cancellationToken).ConfigureAwait(false) : null;
            return SyncLocationFactory.Create(settings, webDavPassword, s3Secret);
        }
        catch (ArgumentException) { return null; }
    }

    private static bool IsFilled(SyncLocationSettings settings) => settings.Kind switch
    {
        SyncLocationKinds.WebDav => !string.IsNullOrWhiteSpace(settings.WebDavUrl) && !string.IsNullOrWhiteSpace(settings.WebDavUser),
        SyncLocationKinds.S3 => !string.IsNullOrWhiteSpace(settings.S3Endpoint) && !string.IsNullOrWhiteSpace(settings.S3Bucket)
            && !string.IsNullOrWhiteSpace(settings.S3AccessKeyId),
        _ => !string.IsNullOrWhiteSpace(settings.Directory),
    };
}
