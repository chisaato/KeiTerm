namespace Kei.Term.Core.Services;

using Kei.Term.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;

public sealed record SecureCrtImportSummary(
    int TotalFilesScanned,
    int FoldersCreated,
    int SessionsImported,
    int IdentitiesCreated,
    IReadOnlyList<string> ImportedIdentityNames);

// SecureCRT 会话目录导入服务：纯业务、无 UI、高内聚且可单测
public class SecureCrtImporter
{
    private static readonly Regex ProtoRegex = new(@"S:""Protocol Name""=(.*)", RegexOptions.Compiled);
    private static readonly Regex HostRegex = new(@"S:""Hostname""=(.*)", RegexOptions.Compiled);
    private static readonly Regex PortRegex = new(@"D:""\[SSH2\] Port""=([0-9a-fA-F]+)", RegexOptions.Compiled);
    private static readonly Regex UserRegex = new(@"S:""Username""=(.*)", RegexOptions.Compiled);
    private static readonly Regex CredRegex = new(@"S:""Credential Title""=(.*)", RegexOptions.Compiled);
    private static readonly Regex EmulationRegex = new(@"S:""Emulation""=(.*)", RegexOptions.Compiled);

    private readonly ITreeRepository _treeRepo;
    private readonly IIdentityRepository _identityRepo;

    public SecureCrtImporter(ITreeRepository treeRepo, IIdentityRepository identityRepo)
    {
        _treeRepo = treeRepo;
        _identityRepo = identityRepo;
    }

    public async Task<SecureCrtImportSummary> ImportFromDirectoryAsync(string sessionsRootDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionsRootDirectory) || !Directory.Exists(sessionsRootDirectory))
        {
            throw new DirectoryNotFoundException($"未找到指定的 SecureCRT 会话目录: {sessionsRootDirectory}");
        }

        // 1. 读取库中现存的树节点与凭据集合，建立映射防止重复建目录与身份
        var existingNodes = await _treeRepo.GetAllNodesAsync(ct);
        var existingIdentities = await _identityRepo.GetAllAsync(ct);

        var identityMap = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in existingIdentities)
        {
            if (!string.IsNullOrWhiteSpace(id.Name))
            {
                identityMap[id.Name.Trim()] = id;
            }
        }

        // (parentId, name, nodeType) -> node
        var nodeMap = new Dictionary<(Guid?, string, NodeType), TreeNodeBase>();
        foreach (var node in existingNodes)
        {
            nodeMap[(node.ParentId, node.Name, node.NodeType)] = node;
        }

        // 2. 递归扫描目录，层级建立 folder 节点
        var folderPathToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        var allDirs = Directory.GetDirectories(sessionsRootDirectory, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(sessionsRootDirectory, d))
            .OrderBy(d => d.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
            .ToList();

        var foldersCreated = 0;
        foreach (var relDir in allDirs)
        {
            var parts = relDir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var folderName = parts[^1];
            var parentRel = parts.Length > 1 ? string.Join('/', parts[..^1]) : null;
            Guid? parentId = parentRel != null && folderPathToId.TryGetValue(parentRel, out var pid) ? pid : null;

            var key = (parentId, folderName, NodeType.Folder);
            if (nodeMap.TryGetValue(key, out var existingFolder))
            {
                folderPathToId[relDir.Replace('\\', '/')] = existingFolder.Id;
            }
            else
            {
                var newFolder = new FolderNode
                {
                    ParentId = parentId,
                    Name = folderName,
                    SortOrder = 0,
                    IsExpanded = true
                };
                await _treeRepo.SaveNodeAsync(newFolder, ct);
                nodeMap[key] = newFolder;
                folderPathToId[relDir.Replace('\\', '/')] = newFolder.Id;
                foldersCreated++;
            }
        }

        // 3. 扫描所有的 .ini 会话文件
        var iniFiles = Directory.GetFiles(sessionsRootDirectory, "*.ini", SearchOption.AllDirectories);
        var totalFiles = 0;
        var sessionsImported = 0;
        var newIdentities = new List<string>();

        foreach (var file in iniFiles)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "__FolderData__.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalFiles++;
            var text = await File.ReadAllTextAsync(file, ct);

            var mProto = ProtoRegex.Match(text);
            var proto = mProto.Success ? mProto.Groups[1].Value.Trim() : "SSH2";
            // 仅导入 SSH 类会话
            if (!proto.Equals("SSH2", StringComparison.OrdinalIgnoreCase) && !proto.Equals("SSH1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mHost = HostRegex.Match(text);
            var host = mHost.Success ? mHost.Groups[1].Value.Trim() : "";

            var mPort = PortRegex.Match(text);
            var port = 22;
            if (mPort.Success && int.TryParse(mPort.Groups[1].Value.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var parsedPort))
            {
                port = parsedPort;
            }

            var mUser = UserRegex.Match(text);
            var user = mUser.Success ? mUser.Groups[1].Value.Trim() : "";

            var mEmulation = EmulationRegex.Match(text);
            var termType = mEmulation.Success && mEmulation.Groups[1].Value.Contains("xterm", StringComparison.OrdinalIgnoreCase)
                ? "xterm-256color"
                : "xterm-256color";

            // 解析使用的 Credential Title
            var mCred = CredRegex.Match(text);
            var credTitle = mCred.Success ? mCred.Groups[1].Value.Trim() : "";

            Guid? assignedIdentityId = null;
            if (!string.IsNullOrWhiteSpace(credTitle))
            {
                if (!identityMap.TryGetValue(credTitle, out var identity))
                {
                    // 自动为用户创建同名空 profile，待用户后续进入身份管理器添加私钥材料或密码
                    identity = new Identity
                    {
                        Name = credTitle,
                        Description = "由 SecureCRT 会话配置自动导入",
                        Username = string.IsNullOrWhiteSpace(user) ? null : user,
                        Methods = [],
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await _identityRepo.SaveAsync(identity, ct);
                    identityMap[credTitle] = identity;
                    newIdentities.Add(credTitle);
                }
                assignedIdentityId = identity.Id;
                if (string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(identity.Username))
                {
                    user = identity.Username;
                }
            }

            // 计算该会话所属的父级目录
            var fileDir = Path.GetDirectoryName(file)!;
            var relDir = Path.GetRelativePath(sessionsRootDirectory, fileDir).Replace('\\', '/');
            Guid? parentId = relDir != "." && folderPathToId.TryGetValue(relDir, out var fId) ? fId : null;

            var sessionName = Path.GetFileNameWithoutExtension(file);

            var sessionKey = (parentId, sessionName, NodeType.Session);
            SessionNode sessionNode;
            if (nodeMap.TryGetValue(sessionKey, out var existingSession) && existingSession is SessionNode sn)
            {
                sessionNode = sn;
                sessionNode.Host = host;
                sessionNode.Port = port;
                sessionNode.Username = user;
                sessionNode.IdentityId = assignedIdentityId;
                sessionNode.TerminalType = termType;
            }
            else
            {
                sessionNode = new SessionNode
                {
                    ParentId = parentId,
                    Name = sessionName,
                    Host = host,
                    Port = port,
                    Username = user,
                    IdentityId = assignedIdentityId,
                    Protocol = SessionProtocols.Ssh,
                    TerminalType = termType,
                    SortOrder = 0
                };
                nodeMap[sessionKey] = sessionNode;
            }

            await _treeRepo.SaveNodeAsync(sessionNode, ct);
            sessionsImported++;
        }

        return new SecureCrtImportSummary(
            totalFiles,
            foldersCreated,
            sessionsImported,
            newIdentities.Count,
            newIdentities);
    }
}
