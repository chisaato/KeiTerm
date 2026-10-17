namespace Kei.Term.Core.Transfers;

// POSIX 单引号转义。单引号内 $ ` \ 都不展开，内部单引号用 '\'' 拼回去。
// 双引号挡不住 $(...) 和反引号，远端文件名不能走那条路。
public static class PosixShell
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
