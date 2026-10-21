using System.Net;
using Kei.Term.Core.Sync;
using WebDav;

namespace Kei.Term.Infrastructure.Sync;

// WebDav.Client 的请求头集合会校验 If-Match，无引号的 ETag（坚果云）会被拒绝。
// 条件头改由自己的 HttpClient 处理器原样附加，不增删引号。
public sealed class WebDavSyncLocation : ISyncLocation, IDisposable
{
    private static readonly AsyncLocal<KeyValuePair<string, string>?> PendingCondition = new();

    private readonly WebDavClient _client;
    private readonly Uri _directory;

    public WebDavSyncLocation(string url, string user, string password, bool conditionalWrites)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("请填写 WebDAV 用户名");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("请填写 WebDAV 密码");
        _directory = NormalizeDirectory(url);
        ConditionalWrites = conditionalWrites;
        var handler = new ConditionalHeaderHandler
        {
            InnerHandler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(user, password),
                PreAuthenticate = true,
                UseDefaultCredentials = false,
            },
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = _directory,
            Timeout = TimeSpan.FromSeconds(100),
        };
        _client = new WebDavClient(http);
    }

    public bool SupportsConditionalWrites => ConditionalWrites;
    private bool ConditionalWrites { get; }

    public Task TestConnectionAsync(CancellationToken ct = default) => Guard(async () =>
    {
        await EnsureCollectionAsync(_directory, ct);
        var probe = ".probe-" + Guid.NewGuid().ToString("N");
        using var stream = new MemoryStream([]);
        var created = await _client.PutFile(probe, stream, new PutFileParameters { CancellationToken = ct });
        EnsureSuccess(created, "测试连接");
        var deleted = await _client.Delete(probe, new DeleteParameters { CancellationToken = ct });
        if (deleted.StatusCode != 404) EnsureSuccess(deleted, "测试连接");
    }, ct);

    public Task<SyncObject?> ReadAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var found = await FindFileAsync(SyncObjectNames.PackageFileName, ct);
        if (found == null) return null;
        using var response = await _client.GetRawFile(SyncObjectNames.PackageFileName, new GetFileParameters { CancellationToken = ct });
        if (response.StatusCode == 404) return null;
        EnsureSuccess(response, "读取同步包");
        using var buffer = new MemoryStream();
        await response.Stream.CopyToAsync(buffer, ct);
        if (ConditionalWrites && string.IsNullOrEmpty(found.ETag))
            throw new IOException("WebDAV 未返回 ETag，请关闭此位置的条件写");
        return new SyncObject(buffer.ToArray(), string.IsNullOrEmpty(found.ETag) ? null : found.ETag);
    }, ct);

    public Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken ct = default) => Guard(async () =>
    {
        await EnsureCollectionAsync(_directory, ct);
        await PutAsync(SyncObjectNames.PackageFileName, content, expectedVersionTag, ct);
    }, ct);

    public Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var response = await _client.Propfind("history/", new PropfindParameters
        {
            ApplyTo = ApplyTo.Propfind.ResourceAndChildren,
            CancellationToken = ct,
        });
        if (response.StatusCode == 404) return (IReadOnlyList<SyncHistoryEntry>)[];
        EnsureSuccess(response, "列出历史同步包");
        return (IReadOnlyList<SyncHistoryEntry>)(response.Resources ?? [])
            .Where(resource => !resource.IsCollection)
            .Select(resource => (Name: FileName(resource.Uri), Resource: resource))
            .Where(item => item.Name != null && item.Name.EndsWith(".age", StringComparison.Ordinal) && IsSafeHistoryName(item.Name))
            .Select(item => new SyncHistoryEntry(item.Name!, AsUtc(item.Resource.LastModifiedDate)))
            .OrderByDescending(entry => entry.WrittenAtUtc)
            .ToArray();
    }, ct);

    public Task<byte[]?> ReadHistoryAsync(string name, CancellationToken ct = default) => Guard(async () =>
    {
        SyncObjectNames.ValidateHistoryName(name);
        using var response = await _client.GetRawFile(HistoryRelative(name), new GetFileParameters { CancellationToken = ct });
        if (response.StatusCode == 404) return null;
        EnsureSuccess(response, "读取历史同步包");
        using var buffer = new MemoryStream();
        await response.Stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }, ct);

    public Task WriteHistoryAsync(string name, byte[] content, CancellationToken ct = default) => Guard(async () =>
    {
        SyncObjectNames.ValidateHistoryName(name);
        await EnsureCollectionAsync(new Uri(_directory, "history/"), ct);
        await PutAsync(HistoryRelative(name), content, expectedVersionTag: null, ct, conditional: false);
    }, ct);

    public Task DeleteHistoryAsync(string name, CancellationToken ct = default) => Guard(async () =>
    {
        SyncObjectNames.ValidateHistoryName(name);
        var response = await _client.Delete(HistoryRelative(name), new DeleteParameters { CancellationToken = ct });
        // 远端删除是幂等的：已经不在的历史包不必让清理失败。
        if (response.StatusCode == 404) return;
        EnsureSuccess(response, "删除历史同步包");
    }, ct);

    public void Dispose() => _client.Dispose();

    private async Task PutAsync(string relativeName, byte[] content, string? expectedVersionTag, CancellationToken ct, bool? conditional = null)
    {
        var useCondition = conditional ?? ConditionalWrites;
        KeyValuePair<string, string>? header = useCondition
            ? new KeyValuePair<string, string>(expectedVersionTag == null ? "If-None-Match" : "If-Match", expectedVersionTag ?? "*")
            : null;
        var previous = PendingCondition.Value;
        PendingCondition.Value = header;
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var response = await _client.PutFile(relativeName, stream, new PutFileParameters { CancellationToken = ct });
            if (response.StatusCode == 412) throw new SyncWriteConflictException();
            EnsureSuccess(response, "写入同步包");
        }
        finally { PendingCondition.Value = previous; }
    }

    private async Task<WebDavResource?> FindFileAsync(string relativeName, CancellationToken ct)
    {
        var response = await _client.Propfind(relativeName, new PropfindParameters
        {
            ApplyTo = ApplyTo.Propfind.ResourceOnly,
            CancellationToken = ct,
        });
        if (response.StatusCode == 404) return null;
        EnsureSuccess(response, "读取同步包");
        return (response.Resources ?? []).FirstOrDefault(resource => !resource.IsCollection);
    }

    private async Task EnsureCollectionAsync(Uri collection, CancellationToken ct, int depth = 0)
    {
        if (depth > 32) throw new IOException("无法创建 WebDAV 目录");
        var response = await _client.Mkcol(collection, new MkColParameters { CancellationToken = ct });
        if (IsCollectionReady(response.StatusCode)) return;
        if (response.StatusCode == 409)
        {
            var parent = ParentOf(collection);
            if (parent == null) throw CollectionFailure(response);
            await EnsureCollectionAsync(parent, ct, depth + 1);
            response = await _client.Mkcol(collection, new MkColParameters { CancellationToken = ct });
            if (IsCollectionReady(response.StatusCode)) return;
        }
        throw CollectionFailure(response);
    }

    private static bool IsCollectionReady(int status) => status is 200 or 201 or 204 or 301 or 405;

    private static IOException CollectionFailure(WebDavResponse response)
        => new($"无法创建 WebDAV 目录：HTTP {response.StatusCode} {response.Description}".Trim());

    private static void EnsureSuccess(WebDavResponse response, string action)
    {
        if (response.StatusCode is >= 200 and <= 299) return;
        throw new IOException($"{action}失败：HTTP {response.StatusCode} {response.Description}".Trim());
    }

    private static Uri NormalizeDirectory(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("WebDAV 地址必须是 http 或 https");
        if (uri.AbsolutePath.EndsWith('/')) return uri;
        return new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
    }

    private static Uri? ParentOf(Uri collection)
    {
        var path = collection.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/") return null;
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0) return null;
        var parentPath = slash == 0 ? "/" : trimmed[..slash] + "/";
        return new UriBuilder(collection) { Path = parentPath, Query = "", Fragment = "" }.Uri;
    }

    private static string HistoryRelative(string name) => "history/" + Uri.EscapeDataString(name);

    private static string? FileName(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var path = uri.Split('?')[0].TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return Uri.UnescapeDataString(name);
    }

    private static bool IsSafeHistoryName(string name)
    {
        try { SyncObjectNames.ValidateHistoryName(name); return true; }
        catch (ArgumentException) { return false; }
    }

    private static DateTime AsUtc(DateTime? value)
    {
        if (value == null) return DateTime.UnixEpoch;
        return value.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value.Value.ToUniversalTime();
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

    private sealed class ConditionalHeaderHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && PendingCondition.Value is { } header)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
