namespace Kei.Term.Core.Transfers;

// 部分文件或目标路径是符号链接、重解析点或目录。拒绝跟随、删除或覆盖。
// 远程检查和随后打开之间仍可能被替换，不能当作绝对安全。
public sealed class TransferPathRejectedException : IOException
{
    public TransferPathRejectedException(string message)
        : base(message)
    {
    }
}

// 服务器不能原子覆盖。旧目标和已完成的部分文件都还在，调用方不要删掉唯一的新副本。
public sealed class TransferAtomicOverwriteUnsupportedException : IOException
{
    public TransferAtomicOverwriteUnsupportedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
