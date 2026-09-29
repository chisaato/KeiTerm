using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels.BatchEdit;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;

namespace Kei.Term.Tests;

public class SessionBatchEditorTests
{
    [Fact]
    public void Apply_WritesValues_AndReportsOnlySessionsThatActuallyChanged()
    {
        var alreadyAlways = new SessionNode { Name = "a", Overrides = new SessionOverrides { CwdFollow = CwdFollowMode.Always } };
        var inheriting = new SessionNode { Name = "b" };

        IReadOnlyList<SessionNode> changed = SessionBatchEditor.Apply(
            [alreadyAlways, inheriting],
            [new SessionBatchChange(SessionBatchFields.CwdFollow, CwdFollowMode.Always)]);

        Assert.Equal([inheriting], changed);
        Assert.Equal(CwdFollowMode.Always, inheriting.Overrides.CwdFollow);
    }

    [Fact]
    public void Apply_NullMeansInheritOrUnbound()
    {
        var session = new SessionNode
        {
            IdentityId = Guid.NewGuid(),
            Overrides = new SessionOverrides { FollowRemoteTitle = false }
        };

        SessionBatchEditor.Apply(
            [session],
            [
                new SessionBatchChange(SessionBatchFields.Identity, null),
                new SessionBatchChange(SessionBatchFields.FollowRemoteTitle, null)
            ]);

        Assert.Null(session.IdentityId);
        Assert.Null(session.Overrides.FollowRemoteTitle);
    }

    [Theory]
    [InlineData("  admin  ", "admin")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Username_IsTrimmed_BlankMeansGlobalDefault(string? input, string? stored)
    {
        var session = new SessionNode { Username = "root" };

        SessionBatchEditor.Apply([session], [new SessionBatchChange(SessionBatchFields.Username, input)]);

        Assert.Equal(stored, session.Username);
    }

    [Fact]
    public void Summarize_DetectsMixedAndCommonValues()
    {
        var id = Guid.NewGuid();
        var same = new[] { new SessionNode { IdentityId = id }, new SessionNode { IdentityId = id } };
        var mixed = new[] { new SessionNode { IdentityId = id }, new SessionNode() };

        SessionBatchSummary common = SessionBatchEditor.Summarize(SessionBatchFields.Identity, same);
        SessionBatchSummary different = SessionBatchEditor.Summarize(SessionBatchFields.Identity, mixed);

        Assert.False(common.IsMixed);
        Assert.Equal(id, common.CommonValue);
        Assert.True(different.IsMixed);
    }

    [Fact]
    public void DescendantSessionIds_IncludesNestedFolders_AndSurvivesCycles()
    {
        var root = new FolderNode { Name = "root" };
        var sub = new FolderNode { Name = "sub", ParentId = root.Id };
        var a = new SessionNode { Name = "a", ParentId = root.Id };
        var b = new SessionNode { Name = "b", ParentId = sub.Id };
        var outside = new SessionNode { Name = "x" };
        // 损坏数据：root 的父指向子文件夹，形成环
        root.ParentId = sub.Id;

        IReadOnlyList<Guid> ids = TreeTraversal.DescendantSessionIds(root.Id, [root, sub, a, b, outside]);

        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), ids.OrderBy(x => x));
    }
}

public class BatchSessionEditViewModelTests
{
    private readonly FolderNode _prod = new() { Name = "生产" };
    private readonly FolderNode _db;
    private readonly SessionNode _web;
    private readonly SessionNode _dbMaster;
    private readonly SessionNode _test;
    private readonly Identity _ops = new() { Id = Guid.NewGuid(), Name = "ops-key" };
    private readonly List<IReadOnlyCollection<TreeNodeBase>> _saved = [];

    public BatchSessionEditViewModelTests()
    {
        _db = new FolderNode { Name = "数据库", ParentId = _prod.Id };
        _web = new SessionNode { Name = "web-1", Host = "10.0.0.1", ParentId = _prod.Id };
        _dbMaster = new SessionNode { Name = "db-master", Host = "10.0.1.1", ParentId = _db.Id };
        _test = new SessionNode { Name = "test-box", Host = "192.168.1.9" };
    }

    private BatchSessionEditViewModel Create(IEnumerable<Guid>? preselected = null, Func<IReadOnlyCollection<TreeNodeBase>, Task>? save = null)
        => new(
            [_prod, _db, _web, _dbMaster, _test],
            preselected ?? [],
            new BatchEditContext([_ops], new AppSettings()),
            save ?? (nodes =>
            {
                _saved.Add(nodes);
                return Task.CompletedTask;
            }));

    private static BatchFieldEditorViewModel Field(BatchSessionEditViewModel vm, SessionBatchField field)
        => vm.Fields.Single(f => f.Definition.Field == field);

    [Fact]
    public void Preselection_AndFolderSelection_IncludeNestedSessions()
    {
        var vm = Create(preselected: [_test.Id]);
        Assert.Equal([_test], vm.Sessions.Where(s => s.IsSelected).Select(s => s.Node));

        vm.SelectFolderCommand.Execute(vm.Folders.Single(f => f.Path == "生产"));

        Assert.Equal(3, vm.SelectedCount);
        Assert.Contains(vm.Folders, f => f.Path == "生产 / 数据库");
    }

    [Fact]
    public void SelectAllVisible_OnlyTouchesFilteredSessions()
    {
        var vm = Create();
        vm.FilterText = "db";

        vm.SelectAllVisibleCommand.Execute(null);

        Assert.Equal([_dbMaster], vm.Sessions.Where(s => s.IsSelected).Select(s => s.Node));
    }

    [Fact]
    public async Task Apply_WritesOnlyEnabledFields_ToSelectedSessions()
    {
        _web.Username = "root";
        _test.Username = "keep-me";
        var vm = Create(preselected: [_web.Id, _dbMaster.Id]);
        BatchFieldEditorViewModel identity = Field(vm, SessionBatchFields.Identity);
        identity.SelectedChoice = identity.Choices.Single(c => Equals(c.Value, _ops.Id));
        BatchFieldEditorViewModel username = Field(vm, SessionBatchFields.Username);
        username.Text = "admin";
        // 用户改了值又取消勾选：不应写入
        username.IsEnabled = false;
        bool closed = false;
        vm.RequestClose += () => closed = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(_ops.Id, _web.IdentityId);
        Assert.Equal(_ops.Id, _dbMaster.IdentityId);
        Assert.Null(_test.IdentityId);
        Assert.Equal("root", _web.Username);
        Assert.Equal("keep-me", _test.Username);
        Assert.Equal(2, vm.ChangedCount);
        Assert.Equal(new TreeNodeBase[] { _web, _dbMaster }.OrderBy(n => n.Name), Assert.Single(_saved).OrderBy(n => n.Name));
    }

    [Fact]
    public void EditingAValue_EnablesTheField_AndSummaryReflectsSelection()
    {
        _web.Overrides.CwdFollow = CwdFollowMode.Always;
        var vm = Create(preselected: [_web.Id, _test.Id]);
        BatchFieldEditorViewModel cwd = Field(vm, SessionBatchFields.CwdFollow);
        Assert.False(cwd.IsEnabled);
        Assert.Contains("不同", cwd.Summary);

        cwd.SelectedChoice = cwd.Choices.Last();

        Assert.True(cwd.IsEnabled);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveFailure_KeepsWindowOpen_AndReportsError()
    {
        var vm = Create(preselected: [_web.Id], save: _ => throw new InvalidOperationException("disk full"));
        Field(vm, SessionBatchFields.Username).Text = "admin";
        bool closed = false;
        vm.RequestClose += () => closed = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.True(vm.ApplyAttempted);
        Assert.Equal(0, vm.ChangedCount);
        Assert.Contains("disk full", vm.StatusText);
    }

    [Fact]
    public void NothingSelectedOrNoFieldEnabled_CannotApply()
    {
        var vm = Create();
        Field(vm, SessionBatchFields.Username).Text = "admin";
        Assert.False(vm.ApplyCommand.CanExecute(null));

        var withSelection = Create(preselected: [_web.Id]);
        Assert.False(withSelection.ApplyCommand.CanExecute(null));
    }
}
