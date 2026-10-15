namespace Kei.Term.Core.Models;

// 终端复用器目录信号的兼容策略；Byobu 按实际使用的后端选择。
public enum TerminalMultiplexerMode
{
    Off = 0,
    Auto = 1,
    Tmux = 2,
    Screen = 3
}
