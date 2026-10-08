namespace Kei.Term.Core.Services;

using System;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;

// 扁平三层配置解析：会话自身 → 全局设置 → 内建兜底，无任何目录继承
public static class SessionConfigBuilder
{
    public static ResolvedSessionConfig Build(SessionNode node, AppSettings settings)
    {
        // 端口：会话自身 → 全局默认端口
        var port = node.Port ?? settings.DefaultPort;

        // 用户名：会话自身 → 全局默认用户名 → 当前系统用户
        var username = !string.IsNullOrWhiteSpace(node.Username)
            ? node.Username
            : !string.IsNullOrWhiteSpace(settings.DefaultUsername)
                ? settings.DefaultUsername
                : Environment.UserName;

        // 身份：会话自身 → 全局默认身份
        var identityId = node.IdentityId ?? settings.DefaultIdentityId;

        // 终端仿真类型也遵循会话 → 全局 → 内建；空白会话字段保留继承语义。
        string terminalType = !string.IsNullOrWhiteSpace(node.TerminalType)
            ? node.TerminalType
            : !string.IsNullOrWhiteSpace(settings.DefaultTerminalType)
                ? settings.DefaultTerminalType
                : "xterm-256color";

        return new ResolvedSessionConfig(
            SessionId: node.Id,
            SessionName: node.Name,
            Host: node.Host,
            Port: port,
            Username: username,
            IdentityId: identityId,
            TerminalType: terminalType,
            StartupScript: node.StartupScript,
            // 跳板机为会话级配置，不回退
            JumpHostSessionId: node.JumpHostSessionId,
            EnvironmentVariables: node.EnvironmentVariables,
            // 终端配色：显式 ID 透传，交由 App 层做三级回退解析
            TerminalProfileId: node.TerminalProfileId,
            // 行为覆盖：会话显式值 → 全局设置
            FollowRemoteTitle: node.Overrides.FollowRemoteTitle ?? settings.TabTitleFollowsRemote,
            CwdFollow: node.Overrides.CwdFollow ?? settings.CwdFollowMode,
            ConnectTimeoutSeconds: node.Overrides.ConnectTimeoutSeconds ?? settings.ConnectTimeoutSeconds,
            ProxyProfileId: node.ProxyProfileId
        );
    }
}
