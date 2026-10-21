using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;
using WebDav;

namespace Kei.Term.Tests;

// 本机 WebDAV / MinIO 契约。默认跳过。启动示例（podman 或 docker 均可，下面用 podman）：
//
// WebDAV（Apache 支持 If-Match；rclone serve webdav 会忽略该条件，不能验证条件写）：
//   podman run --rm -d --name keiterm-webdav -p 127.0.0.1:18080:80 \
//     -e AUTH_TYPE=Basic -e USERNAME=keiterm -e PASSWORD=keitermpass docker.io/bytemark/webdav:latest
//
// MinIO：
//   podman run --rm -d --name keiterm-minio -p 127.0.0.1:19000:9000 \
//     -e MINIO_ROOT_USER=keiterm -e MINIO_ROOT_PASSWORD=keitermsecret \
//     docker.io/minio/minio:latest server /data
// 若 MinIO 镜像不可用，可补充验证 S3Mock（不能替代 MinIO 验证）：
//   podman run --rm -d --name keiterm-s3mock -p 127.0.0.1:19000:9090 docker.io/adobe/s3mock:latest
//
// 然后：
//   KEITERM_SYNC_BACKEND_TESTS=1 \
//   KEITERM_SYNC_WEBDAV_URL=http://127.0.0.1:18080/ \
//   KEITERM_SYNC_WEBDAV_USER=keiterm \
//   KEITERM_SYNC_WEBDAV_PASS=keitermpass \
//   KEITERM_SYNC_S3_ENDPOINT=http://127.0.0.1:19000 \
//   KEITERM_SYNC_S3_BUCKET=keiterm \
//   KEITERM_SYNC_S3_ACCESS_KEY=keiterm \
//   KEITERM_SYNC_S3_SECRET_KEY=keitermsecret \
//   KEITERM_SYNC_S3_REGION=us-east-1 \
//   dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter FullyQualifiedName~SyncLocationContractTests
// 测试结束：podman stop keiterm-webdav keiterm-minio（若用了替代后端，则停止 keiterm-s3mock）。
public sealed class SyncBackendFactAttribute : FactAttribute
{
    public SyncBackendFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KEITERM_SYNC_BACKEND_TESTS") != "1")
            Skip = "需要 KEITERM_SYNC_BACKEND_TESTS=1，并按 SyncBackendLocationTests 文件头注释启动本机 WebDAV 与 MinIO";
    }
}

public sealed class WebDavSyncLocationContractTests : SyncLocationContractTests
{
    private readonly string? _user = Environment.GetEnvironmentVariable("KEITERM_SYNC_WEBDAV_USER");
    private readonly string? _password = Environment.GetEnvironmentVariable("KEITERM_SYNC_WEBDAV_PASS");
    private readonly Uri? _folder;
    private WebDavSyncLocation? _location;

    public WebDavSyncLocationContractTests()
    {
        if (Environment.GetEnvironmentVariable("KEITERM_SYNC_BACKEND_TESTS") != "1") return;
        var url = Environment.GetEnvironmentVariable("KEITERM_SYNC_WEBDAV_URL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(_user) || string.IsNullOrEmpty(_password))
            throw new InvalidOperationException("已设置 KEITERM_SYNC_BACKEND_TESTS=1，但缺少 KEITERM_SYNC_WEBDAV_URL、KEITERM_SYNC_WEBDAV_USER 或 KEITERM_SYNC_WEBDAV_PASS");
        if (!url.EndsWith('/')) url += "/";
        _folder = new Uri(new Uri(url), "keiterm-contract-" + Guid.NewGuid().ToString("N") + "/");
    }

    protected override ISyncLocation CreateLocation()
    {
        _location?.Dispose();
        return _location = new WebDavSyncLocation(_folder!.AbsoluteUri, _user!, _password!, conditionalWrites: true);
    }

    [SyncBackendFact]
    public Task MissingReadAndRoundTrip() => RunMissingReadAndRoundTrip();

    [SyncBackendFact]
    public Task HistoryRoundTripListAndDelete() => RunHistoryRoundTrip();

    public override void Dispose()
    {
        _location?.Dispose();
        if (_folder == null || _user == null || _password == null) return;
        try
        {
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                Credentials = new NetworkCredential(_user, _password),
                PreAuthenticate = true,
            });
            using var client = new WebDavClient(http);
            client.Delete(_folder).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}

public sealed class S3SyncLocationContractTests : SyncLocationContractTests
{
    private readonly string? _endpoint = Environment.GetEnvironmentVariable("KEITERM_SYNC_S3_ENDPOINT");
    private readonly string? _bucket = Environment.GetEnvironmentVariable("KEITERM_SYNC_S3_BUCKET");
    private readonly string? _accessKey = Environment.GetEnvironmentVariable("KEITERM_SYNC_S3_ACCESS_KEY");
    private readonly string? _secretKey = Environment.GetEnvironmentVariable("KEITERM_SYNC_S3_SECRET_KEY");
    private readonly string _region;
    private readonly string _prefix = "keiterm-contract-" + Guid.NewGuid().ToString("N") + "/";
    private S3SyncLocation? _location;
    private bool _bucketReady;

    public S3SyncLocationContractTests()
    {
        var region = Environment.GetEnvironmentVariable("KEITERM_SYNC_S3_REGION");
        _region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
        if (Environment.GetEnvironmentVariable("KEITERM_SYNC_BACKEND_TESTS") != "1") return;
        if (string.IsNullOrWhiteSpace(_endpoint) || string.IsNullOrWhiteSpace(_bucket)
            || string.IsNullOrWhiteSpace(_accessKey) || string.IsNullOrEmpty(_secretKey))
            throw new InvalidOperationException("已设置 KEITERM_SYNC_BACKEND_TESTS=1，但缺少 KEITERM_SYNC_S3_ENDPOINT、KEITERM_SYNC_S3_BUCKET、KEITERM_SYNC_S3_ACCESS_KEY 或 KEITERM_SYNC_S3_SECRET_KEY");
    }

    protected override ISyncLocation CreateLocation()
    {
        EnsureBucket();
        _location?.Dispose();
        return _location = new S3SyncLocation(_endpoint!, _region, _bucket!, _prefix, _accessKey!, _secretKey!, pathStyle: true, conditionalWrites: true);
    }

    [SyncBackendFact]
    public Task MissingReadAndRoundTrip() => RunMissingReadAndRoundTrip();

    [SyncBackendFact]
    public Task HistoryRoundTripListAndDelete() => RunHistoryRoundTrip();

    public override void Dispose()
    {
        try
        {
            if (_bucketReady) DeletePrefix();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _location?.Dispose();
    }

    private void EnsureBucket()
    {
        if (_bucketReady) return;
        using var client = CreateClient();
        try { client.PutBucketAsync(_bucket).GetAwaiter().GetResult(); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict
            || ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        _bucketReady = true;
    }

    private void DeletePrefix()
    {
        using var client = CreateClient();
        string? token = null;
        do
        {
            var page = client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _prefix,
                ContinuationToken = token,
            }).GetAwaiter().GetResult();
            foreach (var item in page.S3Objects ?? [])
                client.DeleteObjectAsync(_bucket, item.Key).GetAwaiter().GetResult();
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token != null);
    }

    private AmazonS3Client CreateClient() => new(_accessKey, _secretKey, new AmazonS3Config
    {
        ServiceURL = _endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = _region,
        UseHttp = _endpoint!.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    });
}
