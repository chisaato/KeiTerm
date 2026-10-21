using System.Text.Json.Serialization;

namespace Kei.Term.Infrastructure.Sync;

public static class SyncLocationKinds
{
    public const string LocalFolder = "local-folder";
    public const string WebDav = "webdav";
    public const string S3 = "s3";
}

// 不含密码和 Secret Key。那些只进保险库。
public sealed class SyncLocationSettings
{
    public string Kind { get; set; } = SyncLocationKinds.LocalFolder;
    public string Directory { get; set; } = "";
    public string WebDavUrl { get; set; } = "";
    public string WebDavUser { get; set; } = "";
    public bool WebDavConditionalWrites { get; set; } = true;
    public string S3Endpoint { get; set; } = "";
    public string S3Region { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string S3Prefix { get; set; } = "";
    public string S3AccessKeyId { get; set; } = "";
    public bool S3PathStyle { get; set; } = true;
    // null 表示按服务商预设；界面保存后会写成明确的 true/false。
    public bool? S3ConditionalWrites { get; set; }
    public string S3Provider { get; set; } = "custom";

    [JsonIgnore]
    public bool UseS3ConditionalWrites => S3ConditionalWrites ?? S3ConditionalWritePolicy.IsEnabled(S3Provider);
}

public static class S3ConditionalWritePolicy
{
    // 阿里云 OSS、腾讯云 COS 的条件写不可靠，默认改走协调器的读回检查。
    public static bool IsEnabled(string? provider)
    {
        var name = provider?.Trim().ToLowerInvariant();
        return name is not ("aliyun" or "aliyun-oss" or "oss" or "tencent" or "tencent-cos" or "cos");
    }
}
