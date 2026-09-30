namespace Kei.Term.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;

// 一个可批量修改的会话属性：只描述"怎么读、怎么写"，不含任何界面信息。
// 新增批量字段 = 在 SessionBatchFields 登记一项 + 在 App 层为它配标签与编辑器。
public abstract class SessionBatchField
{
    protected SessionBatchField(string key) => Key = key;

    // 稳定标识（界面定义按此关联；不要随显示文案变化）
    public string Key { get; }

    public abstract object? Read(SessionNode node);

    public abstract void Write(SessionNode node, object? value);
}

public sealed class SessionBatchField<T> : SessionBatchField
{
    private readonly Func<SessionNode, T> _read;
    private readonly Action<SessionNode, T> _write;

    public SessionBatchField(string key, Func<SessionNode, T> read, Action<SessionNode, T> write)
        : base(key)
    {
        _read = read;
        _write = write;
    }

    public override object? Read(SessionNode node) => _read(node);

    public override void Write(SessionNode node, object? value) => _write(node, (T)value!);
}

// 已登记的批量字段。值为 null 表示「继承全局设置 / 未绑定」
public static class SessionBatchFields
{
    public static readonly SessionBatchField<Guid?> Identity = new(
        "identity",
        n => n.IdentityId,
        (n, v) => n.IdentityId = v);

    // 空白视为未填写：连接时回退全局默认用户名
    public static readonly SessionBatchField<string?> Username = new(
        "username",
        n => string.IsNullOrWhiteSpace(n.Username) ? null : n.Username,
        (n, v) => n.Username = string.IsNullOrWhiteSpace(v) ? null : v.Trim());

    public static readonly SessionBatchField<bool?> FollowRemoteTitle = new(
        "follow-remote-title",
        n => n.Overrides.FollowRemoteTitle,
        (n, v) => n.Overrides.FollowRemoteTitle = v);

    public static readonly SessionBatchField<CwdFollowMode?> CwdFollow = new(
        "cwd-follow",
        n => n.Overrides.CwdFollow,
        (n, v) => n.Overrides.CwdFollow = v);

    public static readonly SessionBatchField<int?> ConnectTimeoutSeconds = new(
        "connect-timeout",
        n => n.Overrides.ConnectTimeoutSeconds,
        (n, v) => n.Overrides.ConnectTimeoutSeconds = v);

    public static IReadOnlyList<SessionBatchField> All { get; } = [Identity, Username, FollowRemoteTitle, CwdFollow, ConnectTimeoutSeconds];
}

public sealed record SessionBatchChange(SessionBatchField Field, object? Value);

// 选中会话上某字段的现状：全部相同则给出该值，否则为混合
public sealed record SessionBatchSummary(bool IsMixed, object? CommonValue, int Count);

public static class SessionBatchEditor
{
    public static SessionBatchSummary Summarize(SessionBatchField field, IReadOnlyCollection<SessionNode> sessions)
    {
        if (sessions.Count == 0)
        {
            return new SessionBatchSummary(false, null, 0);
        }

        List<object?> distinct = sessions.Select(field.Read).Distinct().ToList();
        return distinct.Count == 1
            ? new SessionBatchSummary(false, distinct[0], sessions.Count)
            : new SessionBatchSummary(true, null, sessions.Count);
    }

    // 把修改写入会话，返回实际发生变化的会话（值本来就相同的不计入，避免无意义的保存与 updated_at 变动）
    public static IReadOnlyList<SessionNode> Apply(IEnumerable<SessionNode> sessions, IReadOnlyCollection<SessionBatchChange> changes)
    {
        var changed = new List<SessionNode>();
        foreach (SessionNode session in sessions)
        {
            bool touched = false;
            foreach (SessionBatchChange change in changes)
            {
                object? before = change.Field.Read(session);
                change.Field.Write(session, change.Value);
                touched |= !Equals(before, change.Field.Read(session));
            }

            if (touched)
            {
                changed.Add(session);
            }
        }

        return changed;
    }
}
