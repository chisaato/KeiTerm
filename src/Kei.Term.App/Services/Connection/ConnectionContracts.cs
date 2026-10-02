using System;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Services.Connection;

// 一个正在连接的终端标签：编排器只通过这几个动作驱动它，不直接接触 Avalonia 控件
public interface IConnectionTarget
{
    // 标签已被用户关闭：编排器应停止并释放新建的会话
    bool IsDisposed { get; }

    // 挂载/替换会话（UI 线程调用）
    void AttachSession(ITerminalSession session);

    // 认证失败重试前卸下失败会话
    Task DetachSessionAsync();

    void MarkConnected();

    void ReportError(string message);

    // 连接已成功，但某条转发没起来。不把整次 SSH 标成失败。
    void ReportWarning(string message) { }

    // 只写到本地终端，不发给远端。默认空，测试替身可忽略。
    void WriteLocalStatus(string message) { }

    // 连接成功后挂载文件侧栏（后台线程调用；实现负责回到 UI 线程与本地缓存跟踪器装配）
    Task AttachFileSystemAsync(IRemoteFileSystem fileSystem);

    // 原地重连：卸下旧会话与侧栏、保留终端历史，并换上本次解析的配置（UI 线程调用）
    Task ResetForReconnectAsync(ResolvedSessionConfig config);
}

// 编排器的宿主（主窗口 VM）：按解析后的配置开标签、按 Id 查会话节点（跳板链解析用）
public interface IConnectionHost
{
    IConnectionTarget OpenTab(ResolvedSessionConfig config);

    SessionNode? FindSession(Guid id);
}

// 一次连接请求：是否按会话/全局默认身份认证；快速连接可预置一条密码材料；
// ReuseTarget 非空表示在该标签内原地重连，而不是新开标签
public sealed record ConnectionRequest(
    ResolvedSessionConfig Config,
    bool UseIdentity,
    MaterializedAuthMethod? Preloaded = null,
    IConnectionTarget? ReuseTarget = null);
