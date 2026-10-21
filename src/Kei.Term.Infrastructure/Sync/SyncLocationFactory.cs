using Kei.Term.Core.Sync;

namespace Kei.Term.Infrastructure.Sync;

public static class SyncLocationFactory
{
    public static ISyncLocation Create(SyncLocationSettings settings, string? webDavPassword, string? s3SecretKey)
    {
        return settings.Kind switch
        {
            SyncLocationKinds.WebDav => CreateWebDav(settings, webDavPassword),
            SyncLocationKinds.S3 => CreateS3(settings, s3SecretKey),
            SyncLocationKinds.LocalFolder => CreateLocal(settings),
            _ => throw new ArgumentException($"不支持的同步位置类型：{settings.Kind}"),
        };
    }

    private static ISyncLocation CreateLocal(SyncLocationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Directory)) throw new ArgumentException("请选择同步目录");
        return new LocalFolderSyncLocation(settings.Directory.Trim());
    }

    private static ISyncLocation CreateWebDav(SyncLocationSettings settings, string? password)
    {
        if (string.IsNullOrWhiteSpace(settings.WebDavUrl)) throw new ArgumentException("请填写 WebDAV 地址");
        if (string.IsNullOrWhiteSpace(settings.WebDavUser)) throw new ArgumentException("请填写 WebDAV 用户名");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("请填写 WebDAV 密码");
        return new WebDavSyncLocation(settings.WebDavUrl, settings.WebDavUser.Trim(), password, settings.WebDavConditionalWrites);
    }

    private static ISyncLocation CreateS3(SyncLocationSettings settings, string? secretKey)
    {
        if (string.IsNullOrWhiteSpace(settings.S3Endpoint)) throw new ArgumentException("请填写 S3 端点");
        if (string.IsNullOrWhiteSpace(settings.S3Bucket)) throw new ArgumentException("请填写存储桶");
        if (string.IsNullOrWhiteSpace(settings.S3AccessKeyId)) throw new ArgumentException("请填写 Access Key ID");
        if (string.IsNullOrEmpty(secretKey)) throw new ArgumentException("请填写 S3 Secret Key");
        return new S3SyncLocation(settings.S3Endpoint, settings.S3Region, settings.S3Bucket, settings.S3Prefix,
            settings.S3AccessKeyId, secretKey, settings.S3PathStyle, settings.UseS3ConditionalWrites);
    }
}
