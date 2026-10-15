using Kei.Term.Core.Models;

namespace Kei.Term.Core.Services;

public static class TerminalFileDropPolicy
{
    public static TerminalFileDropAction? ResolveAction(TerminalFileDropAction? connectionAction, TerminalFileDropAction? globalAction)
        => Validate(connectionAction) ?? Validate(globalAction);

    public static TerminalUploadProtocol ResolveProtocol(TerminalUploadProtocol? sessionOverride, TerminalUploadProtocol global)
        => sessionOverride is { } value && Enum.IsDefined(value) ? value
            : Enum.IsDefined(global) ? global : TerminalUploadProtocol.Scp;

    public static string BuildPasteText(IReadOnlyList<string> paths, TerminalFileDropAction action)
    {
        if (action is not (TerminalFileDropAction.PastePath or TerminalFileDropAction.PasteFileName))
            throw new ArgumentOutOfRangeException(nameof(action));

        // 拖入不提交命令；带空格/引号的路径作为一个 shell 参数，控制字符不进入终端输入。
        return string.Join(" ", paths.Select(path =>
        {
            string text = action == TerminalFileDropAction.PasteFileName ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : path;
            if (text.Any(char.IsControl)) throw new ArgumentException("文件名包含终端控制字符。", nameof(paths));
            return "'" + text.Replace("'", "'\"'\"'") + "'";
        }));
    }

    private static TerminalFileDropAction? Validate(TerminalFileDropAction? action)
        => action is { } value && Enum.IsDefined(value) ? value : null;
}
