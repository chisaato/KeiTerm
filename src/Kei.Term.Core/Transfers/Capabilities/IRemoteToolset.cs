namespace Kei.Term.Core.Transfers;

// 把算法/格式翻译成某个平台的命令，并解析命令输出。设置和探测结果只记算法与格式，不记命令。
public interface IRemoteToolset
{
    string PlatformProbeCommand { get; }

    RemotePlatform ParsePlatform(string output);

    string HashProbeCommand(TransferHashAlgorithm algorithm);

    // 对 path 计算哈希。命令自己在 sha256sum / shasum（以及 md5 的对应工具）之间选择，探测结果不记二进制名。
    string HashCommand(TransferHashAlgorithm algorithm, string path);

    bool TryParseHash(TransferHashAlgorithm algorithm, string? output, out string hex);

    string ArchiveProbeCommand(TransferArchiveFormat format);

    // 把 sourceDirectory 这个目录本身打进归档（顶层条目是目录名）。任一环节失败时命令以非零退出。
    string PackCommand(TransferArchiveFormat format, string sourceDirectory, string archivePath);

    // manifestPath 是 NUL 分隔的 SourceTreeReader 筛选结果（含根目录 .），只打包列出的条目。
    string PackTreeCommand(TransferArchiveFormat format, string sourceDirectory, string archivePath, string manifestPath);

    // 把归档解到 destinationDirectory。压缩环节和解包环节各自检查退出码，不依赖 pipefail。
    string UnpackCommand(TransferArchiveFormat format, string archivePath, string destinationDirectory);

    string CreateTemporaryDirectoryCommand(string? parentDirectory = null);
    string DeleteTemporaryDirectoryCommand(string directory);
    string PublishDirectoryCommand(string stagingDirectory, string destinationDirectory, bool rename);
}
