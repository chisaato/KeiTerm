using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels.BatchEdit;

public enum BatchEditorKind
{
    // 下拉选择（含「继承 / 未绑定」等 null 选项）
    Choice,
    // 单行文本；留空表示 null
    Text
}

public sealed record BatchChoice(object? Value, string Label)
{
    public override string ToString() => Label;
}

// 生成下拉项、描述当前值时需要的上下文
public sealed record BatchEditContext(IReadOnlyList<Identity> Identities, AppSettings Settings);

// 批量字段的界面定义：Core 字段 + 标签 + 编辑器形态。新增批量字段只需在 All 中追加一项
public sealed class BatchFieldDefinition
{
    public required SessionBatchField Field { get; init; }

    public required string Label { get; init; }

    public string? Hint { get; init; }

    public BatchEditorKind Kind { get; init; } = BatchEditorKind.Choice;

    // Choice 形态的可选值（首项通常为「继承 / 未绑定」）
    public Func<BatchEditContext, IReadOnlyList<BatchChoice>>? Choices { get; init; }

    // Text 形态的占位提示
    public string? Placeholder { get; init; }

    // Text 形态下 null 值的描述（如「未填写，使用全局默认」）
    public string? EmptyText { get; init; }
}

public static class BatchFieldDefinitions
{
    public static IReadOnlyList<BatchFieldDefinition> All { get; } =
    [
        new()
        {
            Field = SessionBatchFields.Identity,
            Label = Strings.Get("BatchEdit.Field.Identity"),
            Choices = ctx =>
            [
                new BatchChoice(null, Strings.Get("SessionEdit.IdentityNone")),
                .. ctx.Identities.Select(i => new BatchChoice((Guid?)i.Id, i.Name))
            ]
        },
        new()
        {
            Field = SessionBatchFields.Username,
            Label = Strings.Get("BatchEdit.Field.Username"),
            Kind = BatchEditorKind.Text,
            Placeholder = Strings.Get("BatchEdit.Field.Username.Placeholder"),
            EmptyText = Strings.Get("BatchEdit.Field.Username.Empty")
        },
        new()
        {
            Field = SessionBatchFields.FollowRemoteTitle,
            Label = Strings.Get("BatchEdit.Field.TabTitle"),
            Choices = ctx => SessionBehaviorOptions.InheritableTitleFollow(ctx.Settings.TabTitleFollowsRemote)
                .Select(o => new BatchChoice(o.Value, o.DisplayName))
                .ToList()
        },
        new()
        {
            Field = SessionBatchFields.CwdFollow,
            Label = Strings.Get("BatchEdit.Field.CwdFollow"),
            Hint = Strings.Get("Settings.Terminal.CwdFollowTip"),
            Choices = ctx => SessionBehaviorOptions.InheritableCwdFollow(ctx.Settings.CwdFollowMode)
                .Select(o => new BatchChoice(o.Value, o.DisplayName))
                .ToList()
        },
        new()
        {
            Field = SessionBatchFields.ConnectTimeoutSeconds,
            Label = Strings.Get("Settings.Ssh.ConnectTimeout"),
            Choices = ctx => SessionBehaviorOptions.InheritableConnectTimeout(ctx.Settings.ConnectTimeoutSeconds)
                .Select(o => new BatchChoice(o.Value, o.DisplayName))
                .ToList()
        }
    ];
}
