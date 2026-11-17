namespace Kei.Term.App.Models;

// 终端鼠标上报开关变更消息：设置保存后通知所有已打开的终端标签页即时生效
public sealed record TerminalMouseReportingChangedMessage(bool Enabled);
