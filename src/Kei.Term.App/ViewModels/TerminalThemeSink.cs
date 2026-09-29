namespace Kei.Term.App.ViewModels;

using System;
using RoyalTerminal.Avalonia.Controls;
using Kei.Term.App.Services;
using Kei.Term.Core.Models.Profiles;

// 配色应用抽象：默认走真实终端控件，纯状态测试可注入不触碰原生控件的实现
public interface ITerminalThemeSink
{
    void Apply(TerminalProfile profile);

    // Loaded 后 / 重新 ApplyTheme 后恢复被上游强制改写的选区 alpha；返回 false 表示 Renderer 尚未就绪
    bool TryReapplySelectionOverride(TerminalProfile profile);
}

// 默认实现：把方案注入真实 TerminalControl（不启动任何会话）。
// 通过访问器延迟获取控件：测试不访问 Terminal 时不会创建原生控件。
internal sealed class TerminalControlThemeSink : ITerminalThemeSink
{
    private readonly Func<TerminalControl> _terminalAccessor;

    public TerminalControlThemeSink(Func<TerminalControl> terminalAccessor) => _terminalAccessor = terminalAccessor;

    public void Apply(TerminalProfile profile) => TerminalThemeAdapter.Apply(_terminalAccessor(), profile);

    public bool TryReapplySelectionOverride(TerminalProfile profile)
    {
        var terminal = _terminalAccessor();
        if (!TerminalThemeAdapter.TryReapplySelectionOverride(terminal, profile))
        {
            return false;
        }

        // 恢复成功后再请求重绘，避免闪一帧 0x80
        terminal.InvalidateTerminal();
        return true;
    }
}
