using System.Collections.Generic;
using System.Linq;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public sealed record CwdFollowOption(CwdFollowMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

// 会话级「可继承」选项：Value 为 null 表示继承全局设置
public sealed record InheritableOption<T>(T? Value, string DisplayName) where T : struct
{
    public override string ToString() => DisplayName;
}

// 会话行为相关下拉项的唯一来源：设置页、会话编辑、批量修改共用，保证文案一致
public static class SessionBehaviorOptions
{
    public static IReadOnlyList<CwdFollowOption> CwdFollowModes { get; } =
    [
        new(CwdFollowMode.Off, Strings.Get("Behavior.CwdFollow.Off")),
        new(CwdFollowMode.OnceOnOpen, Strings.Get("Behavior.CwdFollow.OnceOnOpen")),
        new(CwdFollowMode.Always, Strings.Get("Behavior.CwdFollow.Always"))
    ];

    public static string Describe(CwdFollowMode mode)
        => CwdFollowModes.FirstOrDefault(o => o.Mode == mode)?.DisplayName ?? mode.ToString();

    public static string Describe(bool followRemoteTitle)
        => Strings.Get(followRemoteTitle ? "Behavior.TabTitle.Remote" : "Behavior.TabTitle.SessionName");

    // 「继承（当前全局：xxx）」+ 各显式值
    public static IReadOnlyList<InheritableOption<CwdFollowMode>> InheritableCwdFollow(CwdFollowMode globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", Describe(globalDefault))),
            .. CwdFollowModes.Select(o => new InheritableOption<CwdFollowMode>(o.Mode, o.DisplayName))
        ];

    public static IReadOnlyList<InheritableOption<bool>> InheritableTitleFollow(bool globalDefault)
        => [
            new(null, Strings.Format("Behavior.Inherit", Describe(globalDefault))),
            new(true, Describe(true)),
            new(false, Describe(false))
        ];
}
