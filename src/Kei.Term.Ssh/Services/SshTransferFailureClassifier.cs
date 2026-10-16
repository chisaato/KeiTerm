namespace Kei.Term.Ssh.Services;

using Kei.Term.Core.Transfers;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

// SSH.NET 异常先在这里分类，其余交给 DotNetTransferFailureClassifier。
// 取消不是失败，与 Core 的约定一致。
public sealed class SshTransferFailureClassifier : ITransferFailureClassifier
{
    public static SshTransferFailureClassifier Instance { get; } = new();

    public TransferFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsCancellation(exception))
        {
            throw new ArgumentException("取消不是传输失败。", nameof(exception));
        }

        TransferFailureKind? ssh = ClassifySsh(exception);
        if (ssh is not null)
        {
            return ssh.Value;
        }

        return DotNetTransferFailureClassifier.Instance.Classify(exception);
    }

    private static TransferFailureKind? ClassifySsh(Exception exception)
    {
        foreach (Exception current in Flatten(exception))
        {
            TransferFailureKind? kind = ClassifyOne(current);
            if (kind is not null)
            {
                return kind;
            }
        }

        return null;
    }

    private static TransferFailureKind? ClassifyOne(Exception exception)
    {
        if (exception is SftpPermissionDeniedException or SftpPathNotFoundException or SshAuthenticationException)
        {
            return TransferFailureKind.Permanent;
        }

        // 握手失败、连接被关、超时、代理失败都是 SshConnectionException / 超时 / ProxyException。
        if (exception is SshConnectionException or SshOperationTimeoutException or ProxyException)
        {
            return TransferFailureKind.Transient;
        }

        if (exception is SftpException sftp)
        {
            return sftp.StatusCode is StatusCode.ConnectionLost or StatusCode.NoConnection
                ? TransferFailureKind.Transient
                : TransferFailureKind.Unknown;
        }

        // 执行请求被拒、通道或流被关闭时，SSH.NET 抛的是普通 SshException，不是 SshConnectionException。
        if (exception is SshException ssh && IsClosedOrRejected(ssh.Message))
        {
            return TransferFailureKind.Transient;
        }

        // 专用传输连接关闭后，租约抛 IOException。DotNet 会把它当成无法判断。
        if (exception is IOException io && IsTransferConnectionClosed(io.Message))
        {
            return TransferFailureKind.Transient;
        }

        return null;
    }

    private static bool IsClosedOrRejected(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection lost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransferConnectionClosed(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("传输连接", StringComparison.Ordinal)
            || message.Contains("专用传输连接", StringComparison.Ordinal);
    }

    private static bool IsCancellation(Exception exception)
    {
        foreach (Exception current in Flatten(exception))
        {
            if (current is OperationCanceledException)
            {
                return true;
            }

            if (current is AggregateException aggregate
                && aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(static inner => inner is OperationCanceledException))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        Stack<Exception> pending = new();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            Exception current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                for (int i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                {
                    pending.Push(aggregate.InnerExceptions[i]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
