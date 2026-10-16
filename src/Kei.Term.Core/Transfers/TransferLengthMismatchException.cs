namespace Kei.Term.Core.Transfers;

// 部分文件写完后长度和源文件不一致。按暂时性失败处理，调用方可以重试。
public sealed class TransferLengthMismatchException : Exception
{
    public TransferLengthMismatchException(long expected, long actual)
        : base($"部分文件长度 {actual} 与源文件 {expected} 不一致。")
    {
        Expected = expected;
        Actual = actual;
    }

    public long Expected { get; }

    public long Actual { get; }
}
