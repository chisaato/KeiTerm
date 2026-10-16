using System.Net.Sockets;

namespace Kei.Term.Core.Transfers;

// 无法判断按不重试处理，与永久性相同，但原因不同，调用方不要合并这两类。
public enum TransferFailureKind
{
    Transient,
    Permanent,
    Unknown,
}

// SFTP 状态码到类别的映射不在这里。实现不得把取消报成失败。
public interface ITransferFailureClassifier
{
    // OperationCanceledException（含 TaskCanceledException，以及只含取消的 AggregateException）不是失败，应抛出 ArgumentException。
    TransferFailureKind Classify(Exception exception);
}

public sealed class DotNetTransferFailureClassifier : ITransferFailureClassifier
{
    public static DotNetTransferFailureClassifier Instance { get; } = new();

    // Unix ENOSPC。Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL 的 HRESULT。
    private const int UnixEnospc = 28;
    private const int WindowsDiskFull = unchecked((int)0x80070070);
    private const int WindowsHandleDiskFull = unchecked((int)0x80070027);

    // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION，以及直接写成 Win32 错误码的形式。
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int SharingViolationCode = 32;
    private const int LockViolationCode = 33;

    public TransferFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsCancellation(exception))
        {
            throw new ArgumentException("取消不是传输失败。", nameof(exception));
        }

        return ClassifyFailure(exception);
    }

    private static TransferFailureKind ClassifyFailure(Exception exception)
    {
        // 部分文件长度和源不一致时，重试有可能写完。必须在普通 IOException 之前判断。
        if (exception is TransferLengthMismatchException)
        {
            return TransferFailureKind.Transient;
        }

        if (exception is TransferPathRejectedException or TransferAtomicOverwriteUnsupportedException)
        {
            return TransferFailureKind.Permanent;
        }

        if (exception is UnauthorizedAccessException
            or FileNotFoundException
            or DirectoryNotFoundException
            or PathTooLongException)
        {
            return TransferFailureKind.Permanent;
        }

        if (exception is SocketException or TimeoutException)
        {
            return TransferFailureKind.Transient;
        }

        if (exception is IOException io)
        {
            if (IsDiskFull(io) || IsSharingViolation(io))
            {
                return TransferFailureKind.Permanent;
            }

            if (WrapsSocketOrTimeout(io))
            {
                return TransferFailureKind.Transient;
            }
        }

        return TransferFailureKind.Unknown;
    }

    private static bool IsCancellation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(static inner => inner is OperationCanceledException))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WrapsSocketOrTimeout(IOException exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SocketException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDiskFull(IOException exception)
    {
        int hr = exception.HResult;
        return hr is UnixEnospc or WindowsDiskFull or WindowsHandleDiskFull;
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int hr = exception.HResult;
        return hr is SharingViolation or LockViolation or SharingViolationCode or LockViolationCode;
    }
}
