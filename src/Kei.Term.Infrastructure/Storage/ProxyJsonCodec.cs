namespace Kei.Term.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using Kei.Term.Core.Models;
using Microsoft.Extensions.Logging;

// proxy_json / config_json 的唯一编解码处。损坏行不抛到调用方。
internal static class ProxyJsonCodec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string? WriteExit(Guid? proxyId, Guid? sessionId)
    {
        // 同一条会话只有一个引用。代理优先，避免两列各写各的。
        if (proxyId is Guid pid)
        {
            return JsonSerializer.Serialize(new ExitDto("proxy", pid.ToString()), Json);
        }

        if (sessionId is Guid sid)
        {
            return JsonSerializer.Serialize(new ExitDto("session", sid.ToString()), Json);
        }

        return null;
    }

    // 迁移回填：把 jump_host_id 原值写进 kind=session，不在这里校验 GUID
    public static string WriteSessionKind(string sessionId)
        => JsonSerializer.Serialize(new ExitDto("session", sessionId), Json);

    public static (Guid? ProxyId, Guid? SessionId) ReadExit(string? json, ILogger? logger, string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        ExitDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ExitDto>(json, Json);
        }
        catch (JsonException)
        {
            logger?.LogWarning("会话出口 JSON 损坏，按无代理加载 node={NodeId}", nodeId);
            return (null, null);
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Kind) || dto.Kind.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (!Guid.TryParse(dto.Id, out Guid id))
        {
            logger?.LogWarning("会话出口 id 不是 GUID，按无代理加载 node={NodeId}", nodeId);
            return (null, null);
        }

        if (dto.Kind.Equals("proxy", StringComparison.OrdinalIgnoreCase))
        {
            return (id, null);
        }

        if (dto.Kind.Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            return (null, id);
        }

        logger?.LogWarning("会话出口 kind 未知，按无代理加载 node={NodeId} kind={Kind}", nodeId, dto.Kind);
        return (null, null);
    }

    public static string WriteConfig(ProxyConfig config) => config switch
    {
        Socks5ProxyConfig socks => JsonSerializer.Serialize(new ConfigDto { Type = "socks5", Host = socks.Host, Port = socks.Port }, Json),
        HttpProxyConfig http => JsonSerializer.Serialize(new ConfigDto { Type = "http", Host = http.Host, Port = http.Port }, Json),
        SessionProxyConfig session => JsonSerializer.Serialize(new ConfigDto { Type = "session", SessionId = session.SessionId.ToString() }, Json),
        _ => throw new ArgumentException("未知代理配置", nameof(config))
    };

    // 返回 null 表示该行损坏或类型尚未识别，调用方跳过
    public static ProxyConfig? ReadConfig(string? json, ILogger? logger, string? proxyId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            logger?.LogWarning("代理配置为空，已跳过 proxy={ProxyId}", proxyId);
            return null;
        }

        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ConfigDto>(json, Json);
        }
        catch (JsonException)
        {
            logger?.LogWarning("代理配置 JSON 损坏，已跳过 proxy={ProxyId}", proxyId);
            return null;
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Type))
        {
            logger?.LogWarning("代理配置缺少 type，已跳过 proxy={ProxyId}", proxyId);
            return null;
        }

        if (dto.Type.Equals("socks5", StringComparison.OrdinalIgnoreCase)
            || dto.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            string host = dto.Host?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(host) || dto.Port is < 1 or > 65535)
            {
                logger?.LogWarning("代理端点无效，已跳过 proxy={ProxyId} type={Type}", proxyId, dto.Type);
                return null;
            }

            return dto.Type.Equals("http", StringComparison.OrdinalIgnoreCase)
                ? new HttpProxyConfig(host, dto.Port)
                : new Socks5ProxyConfig(host, dto.Port);
        }

        if (dto.Type.Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(dto.SessionId, out Guid sessionId))
            {
                logger?.LogWarning("代理援引的会话 id 无效，已跳过 proxy={ProxyId}", proxyId);
                return null;
            }

            return new SessionProxyConfig(sessionId);
        }

        logger?.LogWarning("代理 type 未知，已跳过 proxy={ProxyId} type={Type}", proxyId, dto.Type);
        return null;
    }

    private sealed record ExitDto(string Kind, string? Id);

    private sealed class ConfigDto
    {
        public string? Type { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? SessionId { get; set; }
    }
}
