namespace Kei.Term.App.Terminals;

using System;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;

// VT 处理器回调的后绑定容器：控件构造时处理器工厂已确定，具体去向在控件建好后再指定
public sealed class VtCallbackHooks
{
    // 终端对远端查询（DA 设备属性、DSR 光标位置、颜色查询等）的应答，须原样发回远端
    public Action<byte[]>? Response { get; set; }

    // BEL
    public Action? Bell { get; set; }

    // OSC 0 / OSC 2 标题
    public Action<string>? Title { get; set; }
}

// RoyalTerminal 只在控件自带传输层（StartSessionAsync）时给 VT 处理器挂应答 / 响铃 / 标题回调；
// KeiTerm 经 AttachEndpoint 接入自有 SSH 会话，三个回调因此一直为空：
// 远端的终端查询得不到应答（fish 4 等启动时等待 DA 应答的程序会卡顿）、BEL 与远端标题被丢弃。
// 这里包装默认处理器工厂，在每个处理器创建时（含切换 VT 实现后的重建）挂上回调。
internal sealed class HookingVtProcessorFactory : IVtProcessorFactory
{
    private readonly IVtProcessorFactory _inner;
    private readonly VtCallbackHooks _hooks;

    public HookingVtProcessorFactory(IVtProcessorFactory inner, VtCallbackHooks hooks)
    {
        _inner = inner;
        _hooks = hooks;
    }

    public IVtProcessor Create(TerminalScreen screen, VtProcessorPreference preference)
    {
        IVtProcessor processor = _inner.Create(screen, preference);
        processor.ResponseCallback = data => _hooks.Response?.Invoke(data);
        processor.BellCallback = () => _hooks.Bell?.Invoke();
        processor.TitleCallback = title => _hooks.Title?.Invoke(title);
        return processor;
    }
}

public static class TerminalControlFactory
{
    // 默认处理器工厂带 Kitty 图形解码等内部配置，无法逐项复刻：从一个默认构造的控件上取用（UI 线程、仅一次）。
    // 工厂本身无会话状态，可被多个控件共享。
    private static TerminalControl? _prototype;

    public static TerminalControl Create(VtCallbackHooks hooks)
    {
        TerminalControl prototype = _prototype ??= new TerminalControl();
        return new TerminalControl(
            new TerminalSessionService(),
            new DefaultTerminalInputAdapter(),
            new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(),
            new HookingVtProcessorFactory(prototype.VtProcessorFactory, hooks),
            prototype.PtyFactory,
            prototype.SshCredentialProvider,
            prototype.SshHostKeyValidator,
            transportFactory: null);
    }
}
