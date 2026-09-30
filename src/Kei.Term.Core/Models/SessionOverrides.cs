namespace Kei.Term.Core.Models;

// 文件侧栏跟随终端当前目录的方式（见 docs/design/09）
public enum CwdFollowMode
{
    // 从不自动导航
    Off = 0,
    // 本次连接中第一次打开侧栏时同步一次，之后不再跟随
    OnceOnOpen = 1,
    // 侧栏打开期间始终跟随终端目录
    Always = 2
}

// 会话级行为覆盖：每项 null 表示继承全局设置。
// 以 JSON 整体存于 session_details.options_json，新增覆盖项只需在此加可空属性，无需迁移。
public sealed class SessionOverrides
{
    // 标签标题是否跟随远端（OSC 0/2）标题
    public bool? FollowRemoteTitle { get; set; }

    // 文件侧栏目录跟随方式
    public CwdFollowMode? CwdFollow { get; set; }

    // SSH 连接超时（秒，null 表示继承全局设置）
    public int? ConnectTimeoutSeconds { get; set; }

    public SessionOverrides Clone() => (SessionOverrides)MemberwiseClone();
}
