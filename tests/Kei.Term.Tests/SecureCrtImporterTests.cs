namespace Kei.Term.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Storage;
using Xunit;

public class SecureCrtImporterTests
{
    [Fact]
    public async Task ImportFromDirectory_BuildsTreeAndCreatesPlaceholderIdentities()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"keiterm_scrt_test_{Guid.NewGuid():N}");
        var sessionsDir = Path.Combine(tempRoot, "Sessions");
        var subDir = Path.Combine(sessionsDir, "Prod", "Beijing");
        Directory.CreateDirectory(subDir);

        var tempDbPath = Path.Combine(tempRoot, "test.db");
        var connStr = $"Data Source={tempDbPath}";

        try
        {
            // 创建会话 1：使用凭据 devops-key，无单独 username
            var ini1 = @"
S:""Protocol Name""=SSH2
S:""Hostname""=192.168.1.100
D:""[SSH2] Port""=00000016
S:""Username""=
S:""Credential Title""=devops-key
";
            await File.WriteAllTextAsync(Path.Combine(subDir, "web-server-1.ini"), ini1);

            // 创建会话 2：使用凭据 devops-key，带 username=admin
            var ini2 = @"
S:""Protocol Name""=SSH2
S:""Hostname""=192.168.1.101
D:""[SSH2] Port""=000008ae
S:""Username""=admin
S:""Credential Title""=devops-key
";
            await File.WriteAllTextAsync(Path.Combine(subDir, "web-server-2.ini"), ini2);

            // 创建会话 3：根目录下会话，使用新凭据 root-master
            var ini3 = @"
S:""Protocol Name""=SSH2
S:""Hostname""=10.0.0.1
D:""[SSH2] Port""=00000016
S:""Username""=root
S:""Credential Title""=root-master
";
            await File.WriteAllTextAsync(Path.Combine(sessionsDir, "gateway.ini"), ini3);

            // 创建会话 4：无特定凭据且为 Serial 协议的会话，测试全量导入不被跳过
            var ini4 = @"
S:""Protocol Name""=Serial
S:""Hostname""=
S:""Username""=
";
            await File.WriteAllTextAsync(Path.Combine(subDir, "switch-serial.ini"), ini4);

            // 忽略文件
            await File.WriteAllTextAsync(Path.Combine(sessionsDir, "Default.ini"), "S:\"Hostname\"=default");
            await File.WriteAllTextAsync(Path.Combine(subDir, "__FolderData__.ini"), "D:\"Expanded\"=00000001");

            var treeRepo = new SqliteTreeRepository(connStr);
            var idRepo = new SqliteIdentityRepository(connStr);
            await treeRepo.InitializeAsync();
            await idRepo.InitializeAsync();

            var importer = new SecureCrtImporter(treeRepo, idRepo);
            var summary = await importer.ImportFromDirectoryAsync(sessionsDir);

            Assert.Equal(4, summary.TotalFilesScanned);
            Assert.Equal(2, summary.FoldersCreated); // Prod, Beijing
            Assert.Equal(4, summary.SessionsImported);
            Assert.Equal(2, summary.IdentitiesCreated); // devops-key, root-master

            // 验证生成的凭据
            var identities = await idRepo.GetAllAsync();
            Assert.Equal(2, identities.Count);
            Assert.Contains(identities, i => i.Name == "devops-key");
            Assert.Contains(identities, i => i.Name == "root-master");

            var devopsId = identities.First(i => i.Name == "devops-key");
            Assert.Empty(devopsId.Methods); // 空 profile，等待用户添加私钥

            // 验证树节点与层级
            var nodes = await treeRepo.GetAllNodesAsync();
            var prodFolder = Assert.Single(nodes.OfType<FolderNode>(), f => f.Name == "Prod");
            var beijingFolder = Assert.Single(nodes.OfType<FolderNode>(), f => f.Name == "Beijing");
            Assert.Null(prodFolder.ParentId);
            Assert.Equal(prodFolder.Id, beijingFolder.ParentId);

            var s1 = Assert.Single(nodes.OfType<SessionNode>(), s => s.Name == "web-server-1");
            var s2 = Assert.Single(nodes.OfType<SessionNode>(), s => s.Name == "web-server-2");
            var s3 = Assert.Single(nodes.OfType<SessionNode>(), s => s.Name == "gateway");

            Assert.Equal(beijingFolder.Id, s1.ParentId);
            Assert.Equal(beijingFolder.Id, s2.ParentId);
            Assert.Null(s3.ParentId);

            Assert.Equal("192.168.1.100", s1.Host);
            Assert.Equal(22, s1.Port);
            Assert.Equal(devopsId.Id, s1.IdentityId);

            Assert.Equal("192.168.1.101", s2.Host);
            Assert.Equal(2222, s2.Port); // 0x08ae = 2222
            Assert.Equal("admin", s2.Username);
            Assert.Equal(devopsId.Id, s2.IdentityId);

            var s4 = Assert.Single(nodes.OfType<SessionNode>(), s => s.Name == "switch-serial");
            Assert.Equal(beijingFolder.Id, s4.ParentId);
            Assert.Equal(SessionProtocols.Serial, s4.Protocol);
            Assert.Null(s4.IdentityId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }
}
