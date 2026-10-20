namespace Kei.Term.App.Models;

// 通知级别：显式指定，映射到 Toast 的展示样式与留存时长。
public enum NotificationLevel
{
    // 一般信息通知（~4s 自动消失）
    Info,

    // 操作成功通知（~4s 自动消失）
    Success,

    // 警告提示（~8s 自动消失）
    Warning,

    // 错误告警（常驻，需手动关闭）
    Error,
}
