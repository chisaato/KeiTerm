using System.Threading.Tasks;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.Tests;

// 查找：TerminalControl 已有搜索 API。下一处匹配必须换到另一个匹配（SearchSelected）。
public class TerminalFindTests
{
    [Fact]
    public Task SelectNextSearchMatch_MovesSelectedMatch() => HeadlessAvalonia.RunAsync(() =>
    {
        TerminalControl control = new();
        control.WriteOutput("needle alpha needle\nbeta needle"u8);
        control.StartSearch("needle");

        Assert.True(control.SearchTotal >= 2, $"SearchTotal={control.SearchTotal}");
        int first = control.SearchSelected;

        bool moved = control.SelectNextSearchMatch();

        Assert.True(moved);
        Assert.NotEqual(first, control.SearchSelected);
    });
}
