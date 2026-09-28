namespace Kei.Term.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Storage;

public class ExternalEditorTests
{
    [Fact]
    public void PatternMatching_ShouldMatchCorrectly()
    {
        Assert.True(FileAssociationResolver.IsPatternMatch("test.py", "*.py"));
        Assert.True(FileAssociationResolver.IsPatternMatch("test.py", ".py"));
        Assert.True(FileAssociationResolver.IsPatternMatch("config.json", "*.json"));
        Assert.True(FileAssociationResolver.IsPatternMatch("Dockerfile", "Dockerfile"));
        Assert.True(FileAssociationResolver.IsPatternMatch("access.log.1", "*.log.*"));

        Assert.False(FileAssociationResolver.IsPatternMatch("test.py", "*.js"));
        Assert.False(FileAssociationResolver.IsPatternMatch("test.txt", "*.doc"));
    }

    [Fact]
    public void ResolveEditorId_ShouldRespectPriority()
    {
        var editorA = Guid.NewGuid();
        var editorB = Guid.NewGuid();

        var rules = new List<FileAssociationRule>
        {
            new() { Pattern = "*.log", EditorId = editorA, Priority = 1 },
            new() { Pattern = "error.log", EditorId = editorB, Priority = 10 }
        };

        // error.log 既匹配 *.log 又匹配 error.log，但后者优先级更高
        var resolved = FileAssociationResolver.ResolveEditorId("error.log", rules);
        Assert.Equal(editorB, resolved);

        // info.log 仅匹配 *.log
        var resolvedGeneral = FileAssociationResolver.ResolveEditorId("info.log", rules);
        Assert.Equal(editorA, resolvedGeneral);
    }

    [Fact]
    public void ExternalEditor_GetEffectivePath_ShouldResolveCurrentOsOrAny()
    {
        var editor = new ExternalEditor
        {
            Name = "VS Code",
            Paths =
            [
                new ExternalEditorPath(Guid.NewGuid(), Guid.NewGuid(), "any", "code"),
                new ExternalEditorPath(Guid.NewGuid(), Guid.NewGuid(), "windows", @"C:\Tools\code.cmd"),
                new ExternalEditorPath(Guid.NewGuid(), Guid.NewGuid(), "linux", "/usr/bin/code")
            ]
        };

        var linuxPath = editor.GetEffectivePath("linux");
        Assert.Equal("/usr/bin/code", linuxPath);

        var winPath = editor.GetEffectivePath("windows");
        Assert.Equal(@"C:\Tools\code.cmd", winPath);

        // 安卓没有特定 path，回退到 any
        var androidPath = editor.GetEffectivePath("android");
        Assert.Equal("code", androidPath);
    }

    [Fact]
    public async Task SqliteExternalEditorRepository_CrudAndCascadeDelete_ShouldWork()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_editor_test_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        var repo = new SqliteExternalEditorRepository(connStr);

        try
        {
            await repo.InitializeAsync();

            var editorId = Guid.NewGuid();
            var editor = new ExternalEditor
            {
                Id = editorId,
                Name = "Sublime Text",
                ArgumentsTemplate = "\"{path}\"",
                IsDefault = true,
                Paths =
                [
                    new ExternalEditorPath(Guid.NewGuid(), editorId, "linux", "/usr/bin/subl"),
                    new ExternalEditorPath(Guid.NewGuid(), editorId, "any", "subl")
                ]
            };

            await repo.SaveEditorAsync(editor);

            var loaded = await repo.GetEditorByIdAsync(editorId);
            Assert.NotNull(loaded);
            Assert.Equal("Sublime Text", loaded.Name);
            Assert.True(loaded.IsDefault);
            Assert.Equal(2, loaded.Paths.Count);

            // 添加关联规则
            var rule = new FileAssociationRule
            {
                Id = Guid.NewGuid(),
                Pattern = "*.txt;*.md",
                EditorId = editorId,
                Priority = 5
            };
            await repo.SaveAssociationAsync(rule);

            var associations = await repo.GetAllAssociationsAsync();
            Assert.Single(associations);
            Assert.Equal("*.txt;*.md", associations[0].Pattern);

            // 删除外部编辑器，验证外键级联删除（路径和关联规则自动级联清理）
            await repo.DeleteEditorAsync(editorId);

            var associationsAfterDelete = await repo.GetAllAssociationsAsync();
            Assert.Empty(associationsAfterDelete);

            var loadedAfterDelete = await repo.GetEditorByIdAsync(editorId);
            Assert.Null(loadedAfterDelete);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
