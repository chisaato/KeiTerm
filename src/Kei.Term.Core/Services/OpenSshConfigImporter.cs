namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;

public sealed record SshConfigImportSummary(
    int SessionsImported,
    int SessionsSkipped,
    int IdentitiesCreated,
    IReadOnlyList<string> Warnings);

// ~/.ssh/config 导入：具体别名 → 会话；IdentityFile 组合 → 身份（文件私钥方法）；ProxyJump → 跳板链。
// 导入到根目录下固定文件夹，同名会话跳过，可重复执行（幂等）。
public sealed class OpenSshConfigImporter
{
    public const string FolderName = "SSH Config";
    private const string IdentityNamePrefix = "ssh_config: ";

    private readonly ITreeRepository _treeRepo;
    private readonly IIdentityRepository _identityRepo;

    public OpenSshConfigImporter(ITreeRepository treeRepo, IIdentityRepository identityRepo)
    {
        _treeRepo = treeRepo;
        _identityRepo = identityRepo;
    }

    // expandPath：宿主负责 ~ 与环境变量展开（Core 不触碰文件系统）
    public async Task<SshConfigImportSummary> ImportAsync(
        string content,
        Func<string, string> expandPath,
        Func<string, IEnumerable<string>>? includeResolver = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<SshConfigBlock> blocks = OpenSshConfigParser.Parse(content, includeResolver);
        List<string> warnings = [];

        IReadOnlyList<TreeNodeBase> existingNodes = await _treeRepo.GetAllNodesAsync(ct);
        FolderNode folder = existingNodes.OfType<FolderNode>()
                                .FirstOrDefault(f => f.ParentId == null && f.Name == FolderName)
                            ?? new FolderNode { Name = FolderName };
        bool folderIsNew = !existingNodes.Any(n => n.Id == folder.Id);

        // 文件夹内已有会话按名称索引：重复导入跳过，但仍可被新会话作为跳板引用
        Dictionary<string, SessionNode> byName = existingNodes.OfType<SessionNode>()
            .Where(s => s.ParentId == folder.Id)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Dictionary<string, Identity> identitiesByName = (await _identityRepo.GetAllAsync(ct))
            .GroupBy(i => i.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        List<SessionNode> created = [];
        List<Identity> newIdentities = [];
        HashSet<string> adHocNames = new(StringComparer.Ordinal);
        int skipped = 0;
        int sortOrder = byName.Count;

        SessionNode CreateSession(string name, SshConfigHostSettings settings)
        {
            var session = new SessionNode
            {
                ParentId = folder.Id,
                Name = name,
                Host = settings.HostName,
                Port = settings.Port,
                Username = settings.User,
                SortOrder = sortOrder++,
                IdentityId = ResolveIdentity(settings)?.Id
            };
            byName[name] = session;
            created.Add(session);
            return session;
        }

        Identity? ResolveIdentity(SshConfigHostSettings settings)
        {
            if (settings.IdentityFiles.Count == 0)
            {
                // 未声明 IdentityFile：不绑定身份，走全局默认身份 / Agent 兜底
                return null;
            }

            string name = IdentityNamePrefix
                          + string.Join(", ", settings.IdentityFiles)
                          + (settings.IdentitiesOnly ? " (IdentitiesOnly)" : string.Empty);
            if (identitiesByName.TryGetValue(name, out Identity? existing))
            {
                return existing;
            }

            List<AuthMethodEntry> methods = [];
            // OpenSSH 默认先试 Agent 中的密钥，IdentitiesOnly=yes 时只用显式声明的文件
            if (!settings.IdentitiesOnly)
            {
                methods.Add(new AgentMethod { SortOrder = 0 });
            }

            foreach (string file in settings.IdentityFiles)
            {
                methods.Add(new FilePrivateKeyMethod
                {
                    SortOrder = methods.Count,
                    KeyFilePath = expandPath(file),
                    PassphraseMode = PassphrasePersistence.AlwaysAsk
                });
            }

            var identity = new Identity
            {
                Name = name,
                Description = "从 OpenSSH 配置导入",
                Username = settings.User,
                Methods = methods,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            identitiesByName[name] = identity;
            newIdentities.Add(identity);
            return identity;
        }

        // 1. 具体别名 → 会话（同一 Host 行的多个别名只导入第一个，其余记入描述）
        List<(SessionNode Node, SshConfigHostSettings Settings)> imported = [];
        foreach (SshConfigBlock block in blocks)
        {
            List<string> aliases = block.ConcreteAliases.ToList();
            if (aliases.Count == 0)
            {
                continue;
            }

            string alias = aliases[0];
            if (byName.ContainsKey(alias))
            {
                skipped++;
                continue;
            }

            SshConfigHostSettings settings = OpenSshConfigParser.Resolve(blocks, alias);
            SessionNode node = CreateSession(alias, settings);
            if (aliases.Count > 1)
            {
                node.Description = "别名: " + string.Join(' ', aliases.Skip(1));
            }

            if (settings.HasUnsupportedProxyCommand)
            {
                warnings.Add($"{alias}: 暂不支持 ProxyCommand，已按直连导入");
            }

            imported.Add((node, settings));
        }

        // 2. ProxyJump a,b → 目标经 b、b 经 a；引用未声明的主机时按需创建临时跳板会话
        foreach ((SessionNode node, SshConfigHostSettings settings) in imported)
        {
            if (settings.ProxyJumps.Count == 0)
            {
                continue;
            }

            SessionNode? previous = null;
            foreach (string spec in settings.ProxyJumps)
            {
                SessionNode hop = ResolveJumpNode(spec);
                if (previous != null && adHocNames.Contains(hop.Name) && hop.JumpHostSessionId == null)
                {
                    hop.JumpHostSessionId = previous.Id;
                }
                else if (previous != null && hop.JumpHostSessionId != previous.Id)
                {
                    warnings.Add($"{node.Name}: 跳板 {hop.Name} 已有自身跳板配置，多级 ProxyJump 以其自身配置为准");
                }

                previous = hop;
            }

            node.JumpHostSessionId = previous!.Id;
        }

        SessionNode ResolveJumpNode(string spec)
        {
            (string? user, string host, int? port) = OpenSshConfigParser.ParseJumpSpec(spec);
            // 纯别名引用直接复用已导入/已存在的会话
            if (user == null && port == null && byName.TryGetValue(host, out SessionNode? known))
            {
                return known;
            }

            if (byName.TryGetValue(spec, out SessionNode? adHocExisting))
            {
                return adHocExisting;
            }

            // 跳板主机同样应用配置中的通配块（如 Host * 的 IdentityFile），再以 spec 中的 user/port 覆盖
            SshConfigHostSettings resolved = OpenSshConfigParser.Resolve(blocks, host);
            SessionNode adHoc = CreateSession(spec, resolved with
            {
                User = user ?? resolved.User,
                Port = port ?? resolved.Port
            });
            adHocNames.Add(spec);
            return adHoc;
        }

        if (created.Count == 0)
        {
            return new SshConfigImportSummary(0, skipped, 0, warnings);
        }

        foreach (Identity identity in newIdentities)
        {
            await _identityRepo.SaveAsync(identity, ct);
        }

        if (folderIsNew)
        {
            await _treeRepo.SaveNodeAsync(folder, ct);
        }

        foreach (SessionNode session in created)
        {
            await _treeRepo.SaveNodeAsync(session, ct);
        }

        return new SshConfigImportSummary(created.Count, skipped, newIdentities.Count, warnings);
    }
}
