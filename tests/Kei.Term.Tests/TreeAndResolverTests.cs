using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Storage;
using Xunit;

namespace Kei.Term.Tests;

public class TreeAndResolverTests
{
    [Theory]
    [InlineData("")]
    [InlineData("xterm-256color")]
    public async Task SqliteTreeRepository_TerminalType_PreservesGlobalInheritanceAndExplicitChoice(string terminalType)
    {
        string path = Path.Combine(Path.GetTempPath(), $"keiterm_term_{Guid.NewGuid():N}.db");
        try
        {
            SqliteTreeRepository repository = new($"Data Source={path}");
            await repository.InitializeAsync();
            SessionNode session = new() { Name = "server", Host = "server", TerminalType = terminalType };
            await repository.SaveNodeAsync(session);

            SessionNode loaded = Assert.IsType<SessionNode>(await repository.GetNodeByIdAsync(session.Id));
            Assert.Equal(terminalType, loaded.TerminalType);
            Kei.Term.Core.Settings.AppSettings settings = new() { DefaultTerminalType = "vt100" };
            Assert.Equal(string.IsNullOrEmpty(terminalType) ? "vt100" : terminalType,
                Kei.Term.Core.Services.SessionConfigBuilder.Build(loaded, settings).TerminalType);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task SqliteTreeRepository_CrudAndCascadeOperations()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDbPath}";

        try
        {
            var repo = new SqliteTreeRepository(connStr);
            await repo.InitializeAsync();

            // 文件夹为纯分类容器，无默认配置
            var folder = new FolderNode
            {
                Name = "Test Group"
            };
            await repo.SaveNodeAsync(folder);

            var session = new SessionNode
            {
                ParentId = folder.Id,
                Name = "Server-1",
                Host = "10.0.0.1"
            };
            await repo.SaveNodeAsync(session);

            var all = await repo.GetAllNodesAsync();
            Assert.Equal(2, all.Count);

            // 修改并重命名
            session.Name = "Server-1-Renamed";
            session.Port = 2222;
            await repo.SaveNodeAsync(session);

            var updatedSession = await repo.GetNodeByIdAsync(session.Id) as SessionNode;
            Assert.NotNull(updatedSession);
            Assert.Equal("Server-1-Renamed", updatedSession.Name);
            Assert.Equal(2222, updatedSession.Port);

            // 级联删除
            await repo.DeleteNodeAsync(folder.Id);
            var remaining = await repo.GetAllNodesAsync();
            Assert.Empty(remaining);
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
    }

    [Fact]
    public async Task SqliteTreeRepository_FolderExpandedPersistence()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_exp_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDbPath}";

        try
        {
            var repo = new SqliteTreeRepository(connStr);
            await repo.InitializeAsync();

            var folder = new FolderNode
            {
                Name = "Dev Servers",
                IsExpanded = true
            };
            await repo.SaveNodeAsync(folder);

            var loaded = await repo.GetAllNodesAsync();
            var loadedFolder = Assert.Single(loaded) as FolderNode;
            Assert.NotNull(loadedFolder);
            Assert.True(loadedFolder.IsExpanded);

            // 更新折叠状态并复查持久化
            await repo.UpdateFolderExpandedAsync(folder.Id, false);
            var reloaded = await repo.GetAllNodesAsync();
            var reloadedFolder = Assert.Single(reloaded) as FolderNode;
            Assert.NotNull(reloadedFolder);
            Assert.False(reloadedFolder.IsExpanded);
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
    }

    [Fact]
    public async Task SqliteTreeRepository_OrdersFoldersBeforeSessions()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_order_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDbPath}";

        try
        {
            var repo = new SqliteTreeRepository(connStr);
            await repo.InitializeAsync();

            // 插入若干顺序交错的会话与文件夹（名字为中文和英文字母）
            await repo.SaveNodeAsync(new SessionNode { Name = "a-session", Host = "1.1.1.1" });
            await repo.SaveNodeAsync(new FolderNode { Name = "z-folder" });
            await repo.SaveNodeAsync(new SessionNode { Name = "b-session", Host = "1.1.1.2" });
            await repo.SaveNodeAsync(new FolderNode { Name = "佛山家中" });

            var loaded = await repo.GetAllNodesAsync();
            Assert.Equal(4, loaded.Count);

            // 前 2 个必须是文件夹，后 2 个必须是会话
            Assert.Equal(NodeType.Folder, loaded[0].NodeType);
            Assert.Equal(NodeType.Folder, loaded[1].NodeType);
            Assert.Equal(NodeType.Session, loaded[2].NodeType);
            Assert.Equal(NodeType.Session, loaded[3].NodeType);

            // 文件夹内部按名称排序
            Assert.Equal("z-folder", loaded[0].Name);
            Assert.Equal("佛山家中", loaded[1].Name);

            // 会话内部按名称排序
            Assert.Equal("a-session", loaded[2].Name);
            Assert.Equal("b-session", loaded[3].Name);
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
    }
}
