using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Kei.Term.Core.Sync;

namespace Kei.Term.Infrastructure.Sync;

public sealed class S3SyncLocation : ISyncLocation, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly bool _conditional;

    public S3SyncLocation(string endpoint, string? region, string bucket, string? prefix, string accessKeyId, string secretKey, bool pathStyle, bool conditionalWrites)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(endpointUri.Host))
            throw new ArgumentException("S3 端点必须以 http:// 或 https:// 开头");
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("请填写存储桶");
        if (string.IsNullOrWhiteSpace(accessKeyId)) throw new ArgumentException("请填写 Access Key ID");
        if (string.IsNullOrEmpty(secretKey)) throw new ArgumentException("请填写 S3 Secret Key");
        var address = endpoint.Trim();
        _bucket = bucket.Trim();
        _prefix = NormalizePrefix(prefix);
        _conditional = conditionalWrites;
        var config = new AmazonS3Config
        {
            ServiceURL = address,
            ForcePathStyle = pathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region.Trim(),
            UseHttp = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            // 兼容 MinIO、R2 等不接受默认灵活校验和的服务。
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(100),
        };
        _client = new AmazonS3Client(accessKeyId.Trim(), secretKey, config);
    }

    public bool SupportsConditionalWrites => _conditional;
    public Task DeleteAsync(CancellationToken ct = default) => Guard(async () =>
    {
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = Key(SyncObjectNames.PackageFileName) }, ct);
    }, ct);

    public Task TestConnectionAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var key = _prefix + ".probe-" + Guid.NewGuid().ToString("N");
        using var stream = new MemoryStream([]);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
        }, ct);
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
    }, ct);

    public Task<SyncObject?> ReadAsync(CancellationToken ct = default) => Guard(async () =>
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, Key(SyncObjectNames.PackageFileName), ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return new SyncObject(buffer.ToArray(), string.IsNullOrEmpty(response.ETag) ? null : response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }, ct);

    public Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken ct = default) => Guard(async () =>
    {
        using var stream = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Key(SyncObjectNames.PackageFileName),
            InputStream = stream,
            AutoCloseStream = false,
        };
        ApplyCondition(request, expectedVersionTag);
        try { await _client.PutObjectAsync(request, ct); }
        catch (AmazonS3Exception ex) when (IsConflict(ex)) { throw new SyncWriteConflictException(); }
    }, ct);

    public Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var prefix = _prefix + "history/";
        var entries = new List<SyncHistoryEntry>();
        string? token = null;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);
            foreach (var item in response.S3Objects ?? [])
            {
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = item.Key[prefix.Length..];
                if (!name.EndsWith(".age", StringComparison.Ordinal) || !IsSafeHistoryName(name)) continue;
                var modified = item.LastModified ?? DateTime.UnixEpoch;
                var written = modified.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(modified, DateTimeKind.Utc)
                    : modified.ToUniversalTime();
                entries.Add(new SyncHistoryEntry(name, written));
            }
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (token != null);
        return (IReadOnlyList<SyncHistoryEntry>)entries.OrderByDescending(entry => entry.WrittenAtUtc).ToArray();
    }, ct);

    public Task<byte[]?> ReadHistoryAsync(string name, CancellationToken ct = default) => Guard(async () =>
    {
        SyncObjectNames.ValidateHistoryName(name);
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, Key("history/" + name), ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }, ct);

    public Task WriteHistoryAsync(string name, byte[] content, CancellationToken ct = default) => Guard(async () =>
    {
        SyncObjectNames.ValidateHistoryName(name);
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Key("history/" + name),
            InputStream = stream,
            AutoCloseStream = false,
        }, ct);
    }, ct);

    public Task DeleteHistoryAsync(string name, CancellationToken ct = default) => Guard(async () =>
    {
        SyncObjectNames.ValidateHistoryName(name);
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = Key("history/" + name) }, ct);
    }, ct);

    public void Dispose() => _client.Dispose();

    private void ApplyCondition(PutObjectRequest request, string? expectedVersionTag)
    {
        if (!_conditional) return;
        if (expectedVersionTag == null) request.IfNoneMatch = "*";
        else request.IfMatch = expectedVersionTag;
    }

    private static bool IsConflict(AmazonS3Exception ex)
        => ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;

    private string Key(string name) => _prefix + name;

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "";
        var value = prefix.Trim().Replace('\\', '/').Trim('/');
        return value.Length == 0 ? "" : value + "/";
    }

    private static bool IsSafeHistoryName(string name)
    {
        try { SyncObjectNames.ValidateHistoryName(name); return true; }
        catch (ArgumentException) { return false; }
    }

    private static async Task Guard(Func<Task> action, CancellationToken ct) => await Guard(async () => { await action(); return 0; }, ct);

    private static async Task<T> Guard<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is not (IOException or ArgumentException) && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
