namespace Kei.Term.App.Terminals;

using Avalonia.Input;
using Kei.Term.App.Helpers;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;

// RoyalTerminal 控件的键盘隧道处理器以 handledEventsToo:true 注册，且对「已 Handled」的事件
// 仍调用输入适配器（见其 OnKeyDownTunnel）。于是当应用的窗口隧道处理器先消费了快捷键
// （Ctrl+W 关闭标签、Ctrl+数字/± 切换标签）并置 Handled 时，库仍会把同一按键交给适配器发送给远端。
// 这里在默认适配器外做一层窄包装：仅当事件已被更早的处理器消费、且属于应用自己拥有的手势时，
// 报告为已消费而不再发送；其余一切（普通字符、Enter、Tab/Shift+Tab、Ctrl+C/U 等未被应用消费的输入）
// 原样下传，避免误吞终端输入。
// 同时透传默认适配器实现的组合输入与重置接口，保证 IME 组合、输入状态重置等行为不丢失。
public sealed class AppShortcutAwareInputAdapter(ITerminalInputAdapter inner) :
    ITerminalInputAdapter,
    IResettableTerminalInputAdapter,
    ITerminalCompositionInputAdapter
{
    public bool HandleKeyDown(KeyEventArgs e, ITerminalSessionService sessionService, IVtProcessor? vtProcessor)
        => e.Handled && AppShortcuts.IsAppOwnedKey(e.Key, e.KeyModifiers) || inner.HandleKeyDown(e, sessionService, vtProcessor);

    public bool HandleKeyUp(KeyEventArgs e, ITerminalSessionService sessionService)
        => inner.HandleKeyUp(e, sessionService);

    public bool HandleTextInput(TextInputEventArgs e, ITerminalSessionService sessionService)
        => inner.HandleTextInput(e, sessionService);

    public void ResetInputState()
    {
        if (inner is IResettableTerminalInputAdapter resettable) resettable.ResetInputState();
    }

    public void SetComposing(bool composing)
    {
        if (inner is ITerminalCompositionInputAdapter composition) composition.SetComposing(composing);
    }
}
