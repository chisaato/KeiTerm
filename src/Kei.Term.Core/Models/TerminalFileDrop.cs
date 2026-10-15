namespace Kei.Term.Core.Models;

// 终端拖入文件的上传方式与文件管理器的 SFTP/SCP 浏览通道独立。
public enum TerminalUploadProtocol
{
    Scp = 0,
    ZModem = 1,
    YModem = 2,
    Sftp = 3
}

public enum TerminalFileDropAction
{
    Upload = 0,
    PastePath = 1,
    PasteFileName = 2
}

public enum TerminalFileDropRememberScope
{
    AskAgain = 0,
    Connection = 1,
    Global = 2
}

public sealed record TerminalFileDropChoice(TerminalFileDropAction Action, TerminalFileDropRememberScope RememberScope);
