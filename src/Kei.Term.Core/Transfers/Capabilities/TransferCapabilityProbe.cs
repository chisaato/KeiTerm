namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Settings;

// 用当前配置顺序逐项探测。exec 被拒绝或中途失败时返回 Unknown 和空列表，不把异常抛给传输队列。
public static class TransferCapabilityProbe
{
    public static async Task<TransferProbeResult> ProbeAsync(
        IRemoteShell shell,
        FileTransferSettings settings,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(settings);
        TimeProvider clock = time ?? TimeProvider.System;
        PosixToolset posix = PosixToolset.Instance;

        RemoteCommandResult uname;
        try
        {
            uname = await shell.RunAsync(posix.PlatformProbeCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TransferProbeResult.Unavailable(clock);
        }

        if (uname.ExitStatus != 0)
        {
            return TransferProbeResult.Unavailable(clock);
        }

        RemotePlatform platform = posix.ParsePlatform(uname.Output);
        IRemoteToolset? tools = RemoteToolsets.For(platform);
        if (tools == null)
        {
            return TransferProbeResult.Unavailable(clock);
        }

        try
        {
            List<TransferHashAlgorithm> hashes = [];
            foreach (TransferHashAlgorithm algorithm in settings.EffectiveHashAlgorithmOrder)
            {
                if (await IsAvailableAsync(shell, tools.HashProbeCommand(algorithm), cancellationToken).ConfigureAwait(false))
                {
                    hashes.Add(algorithm);
                }
            }

            List<TransferArchiveFormat> archives = [];
            IRemoteToolset? archiveTools = RemoteToolsets.ForArchives(platform);
            foreach (TransferArchiveFormat format in archiveTools == null ? [] : settings.EffectiveArchiveFormatOrder)
            {
                if (await IsAvailableAsync(shell, archiveTools!.ArchiveProbeCommand(format), cancellationToken).ConfigureAwait(false))
                {
                    archives.Add(format);
                }
            }

            return new TransferProbeResult(platform, hashes, archives, clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TransferProbeResult.Unavailable(clock);
        }
    }

    private static async Task<bool> IsAvailableAsync(IRemoteShell shell, string command, CancellationToken cancellationToken)
    {
        RemoteCommandResult result = await shell.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return result.ExitStatus == 0 && string.IsNullOrEmpty(result.ExitSignal);
    }
}
