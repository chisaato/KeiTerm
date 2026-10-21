namespace Kei.Term.Core.Sync;

/// <summary>二进制同步包；调用方拥有输入/返回缓冲区，须在用完后清零秘密和明文。</summary>
public interface ISyncPackage
{
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<char> passphrase);
    // 完整验证成功才返回明文，不向调用方释放未验证的部分结果。
    byte[] Decrypt(ReadOnlySpan<byte> package, ReadOnlySpan<char> passphrase);
}

public enum SyncPackageError
{
    NotAge,
    WrongPassphrase,
    CorruptOrTruncated,
    HeaderAuthenticationFailed,
    WorkFactorTooHigh,
    UnsupportedFormat,
}

public sealed class SyncPackageException(SyncPackageError error, string message) : Exception(message)
{
    public SyncPackageError Error { get; } = error;
}
