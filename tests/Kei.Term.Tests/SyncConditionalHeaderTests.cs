using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.Tests;

public sealed class WebDavConditionalHeaderTests : IDisposable
{
    private readonly HeaderServer _server = HeaderServer.StartWebDav();

    [Fact]
    public async Task QuotedAndUnquotedEtagsAreSentVerbatimAnd412Conflicts()
    {
        await AssertRoundTrip("W/\"keep-quotes\"");
        _server.Reset();
        await AssertRoundTrip("nutstore-etag");
    }

    [Fact]
    public async Task DisabledConditionalWriteOmitsConditionHeaders()
    {
        using var location = Create(conditional: false);
        await location.WriteAsync([1], "stale");
        var put = Assert.Single(_server.Puts);
        Assert.Null(put.IfMatch);
        Assert.Null(put.IfNoneMatch);
        Assert.Equal(new byte[] { 1 }, (await location.ReadAsync())!.Content);
    }

    public void Dispose() => _server.Dispose();

    private async Task AssertRoundTrip(string etag)
    {
        _server.ETag = etag;
        using var location = Create(conditional: true);
        Assert.True(location.SupportsConditionalWrites);
        await location.WriteAsync([1, 2, 3]);
        Assert.Equal("*", _server.Puts[^1].IfNoneMatch);
        Assert.Null(_server.Puts[^1].IfMatch);

        var read = await location.ReadAsync();
        Assert.Equal(etag, read!.VersionTag);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Content);

        await location.WriteAsync([4], read.VersionTag);
        Assert.Equal(etag, _server.Puts[^1].IfMatch);
        await Assert.ThrowsAsync<SyncWriteConflictException>(() => location.WriteAsync([5], "stale"));
        Assert.Equal(new byte[] { 4 }, (await location.ReadAsync())!.Content);
    }

    private WebDavSyncLocation Create(bool conditional)
        => new(_server.Root, "user", "secret", conditional);
}

public sealed class S3ConditionalHeaderTests : IDisposable
{
    private readonly HeaderServer _server = HeaderServer.StartS3();

    [Fact]
    public async Task IfMatchIsTheReadEtagAnd412Or409Conflicts()
    {
        using var location = Create(conditional: true);
        await location.WriteAsync([1, 2, 3]);
        Assert.Equal("*", _server.Puts[^1].IfNoneMatch);
        var read = await location.ReadAsync();
        Assert.False(string.IsNullOrEmpty(read!.VersionTag));
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Content);
        await location.WriteAsync([4], read.VersionTag);
        Assert.Equal(read.VersionTag, _server.Puts[^1].IfMatch);
        await Assert.ThrowsAsync<SyncWriteConflictException>(() => location.WriteAsync([5], "stale"));
        Assert.Equal(new byte[] { 4 }, (await location.ReadAsync())!.Content);

        _server.NextStatus = 409;
        await Assert.ThrowsAsync<SyncWriteConflictException>(() => location.WriteAsync([6], read.VersionTag));
    }

    [Fact]
    public async Task DisabledConditionalWriteOmitsConditionHeaders()
    {
        using var location = Create(conditional: false);
        Assert.False(location.SupportsConditionalWrites);
        await location.WriteAsync([7], "stale");
        var put = Assert.Single(_server.Puts);
        Assert.Null(put.IfMatch);
        Assert.Null(put.IfNoneMatch);
    }

    [Fact]
    public async Task SignedMultiChunkPayloadRoundTripsWithoutTransportFraming()
    {
        using var location = Create(conditional: true);
        byte[] bytes = new byte[200_000];
        new Random(42).NextBytes(bytes);
        await location.WriteAsync(bytes);
        Assert.Equal(bytes, (await location.ReadAsync())!.Content);
    }

    public void Dispose() => _server.Dispose();

    private S3SyncLocation Create(bool conditional)
        => new(_server.Root, "us-east-1", "bucket", null, "access", "secret", pathStyle: true, conditionalWrites: conditional);
}

internal sealed class HeaderServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly bool _webDav;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Root { get; }
    public string ETag { get; set; } = "W/\"s3-etag\"";
    public int? NextStatus { get; set; }
    public List<(string? IfMatch, string? IfNoneMatch)> Puts { get; } = [];

    private HeaderServer(bool webDav)
    {
        _webDav = webDav;
        int port = FreePort();
        Root = webDav ? $"http://127.0.0.1:{port}/dav/" : $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    public static HeaderServer StartWebDav() => new(true);
    public static HeaderServer StartS3() => new(false);

    public void Reset()
    {
        lock (_gate)
        {
            _files.Clear();
            Puts.Clear();
            NextStatus = null;
        }
    }

    public void Dispose()
    {
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (!_listener.IsListening) { break; }
            try { await Handle(context); }
            catch { try { context.Response.Abort(); } catch { } }
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        using var bodyBuffer = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(bodyBuffer);
        var body = bodyBuffer.ToArray();
        // HttpListener 只解 HTTP transfer-encoding，不解 SigV4 的 aws-chunked 内容编码。
        if (context.Request.Headers["Content-Encoding"]?.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase) == true)
            body = DecodeAwsChunks(body, context.Request.Headers["x-amz-decoded-content-length"]);
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;
        if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            var ifMatch = BlankToNull(context.Request.Headers["If-Match"]);
            var ifNone = BlankToNull(context.Request.Headers["If-None-Match"]);
            lock (_gate) Puts.Add((ifMatch, ifNone));
            var forced = NextStatus;
            NextStatus = null;
            if (forced is 409 or 412)
            {
                await Write(context, forced.Value, _webDav ? [] : Encoding.UTF8.GetBytes(ErrorXml(forced.Value)), "application/xml");
                return;
            }
            lock (_gate)
            {
                if (ifMatch != null && (!_files.ContainsKey(path) || !string.Equals(ifMatch, ETag, StringComparison.Ordinal)))
                {
                    forced = 412;
                }
                else if (ifNone == "*" && _files.ContainsKey(path)) forced = 412;
                else _files[path] = body;
            }
            if (forced == 412)
            {
                await Write(context, 412, _webDav ? [] : Encoding.UTF8.GetBytes(ErrorXml(412)), "application/xml");
                return;
            }
            await Write(context, _webDav ? 201 : 200, [], "application/octet-stream", ETag);
            return;
        }

        if (_webDav && string.Equals(method, "MKCOL", StringComparison.OrdinalIgnoreCase))
        {
            await Write(context, 201, []);
            return;
        }

        if (_webDav && string.Equals(method, "PROPFIND", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? content;
            lock (_gate) _files.TryGetValue(path, out content);
            if (content == null)
            {
                await Write(context, 404, []);
                return;
            }
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <D:multistatus xmlns:D="DAV:">
                  <D:response>
                    <D:href>{path}</D:href>
                    <D:propstat>
                      <D:prop>
                        <D:getetag>{ETag}</D:getetag>
                        <D:getcontentlength>{content.Length}</D:getcontentlength>
                        <D:getlastmodified>Thu, 22 Oct 2026 00:00:00 GMT</D:getlastmodified>
                      </D:prop>
                      <D:status>HTTP/1.1 200 OK</D:status>
                    </D:propstat>
                  </D:response>
                </D:multistatus>
                """;
            await Write(context, 207, Encoding.UTF8.GetBytes(xml), "application/xml");
            return;
        }

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? content;
            lock (_gate) _files.TryGetValue(path, out content);
            if (content == null)
            {
                await Write(context, 404, _webDav ? [] : Encoding.UTF8.GetBytes(ErrorXml(404)), "application/xml");
                return;
            }
            await Write(context, 200, content, "application/octet-stream", ETag);
            return;
        }

        await Write(context, 204, []);
    }

    private static async Task Write(HttpListenerContext context, int status, byte[] body, string? contentType = null, string? etag = null)
    {
        context.Response.StatusCode = status;
        if (contentType != null) context.Response.ContentType = contentType;
        if (etag != null) context.Response.Headers["ETag"] = etag;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static string ErrorXml(int status) => status switch
    {
        409 => """<?xml version="1.0" encoding="UTF-8"?><Error><Code>ConditionalRequestConflict</Code><Message>conflict</Message></Error>""",
        404 => """<?xml version="1.0" encoding="UTF-8"?><Error><Code>NoSuchKey</Code><Message>missing</Message></Error>""",
        _ => """<?xml version="1.0" encoding="UTF-8"?><Error><Code>PreconditionFailed</Code><Message>precondition failed</Message></Error>""",
    };

    private static string? BlankToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static byte[] DecodeAwsChunks(byte[] encoded, string? decodedLength)
    {
        using var output = new MemoryStream();
        int offset = 0;
        while (true)
        {
            int lineEnd = encoded.AsSpan(offset).IndexOf("\r\n"u8);
            if (lineEnd < 0) throw new InvalidDataException("缺少 AWS chunk 头");
            string header = Encoding.ASCII.GetString(encoded, offset, lineEnd);
            int length = int.Parse(header.Split(';')[0], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            offset += lineEnd + 2;
            if (length == 0) break;
            if (length > encoded.Length - offset - 2 || encoded[offset + length] != 13 || encoded[offset + length + 1] != 10)
                throw new InvalidDataException("AWS chunk 长度无效");
            output.Write(encoded, offset, length);
            offset += length + 2;
        }
        if (output.Length != long.Parse(decodedLength!, System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidDataException("AWS 解码长度不匹配");
        return output.ToArray();
    }

    private static int FreePort()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
