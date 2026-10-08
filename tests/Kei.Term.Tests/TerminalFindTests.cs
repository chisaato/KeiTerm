using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.Tests;

public class TerminalFindTests
{
    [MonospaceFact]
    public Task FindCommands_NavigateMatchesAndClose() => HeadlessAvalonia.RunAsync(async () =>
    {
        string family = InstalledMonospace.Require();
        TerminalTabViewModel tab = new("search", family, 14);
        Window window = new() { Width = 600, Height = 300, Content = tab.Terminal };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            InstalledMonospace.AssertUsableCellHeight(tab.Terminal, family);
            tab.Terminal.WriteOutput("needle alpha needle\r\nbeta needle"u8);
            HeadlessAvalonia.Pump();

            tab.OpenFindCommand.Execute(null);
            tab.FindQuery = "needle";
            Stopwatch started = Stopwatch.StartNew();
            tab.FindNextCommand.Execute(null);
            // StartSearch 先把 SearchTotal 置 0，匹配由后台回填；Pump 不等那条线程。
            HeadlessAvalonia.WaitUntil(
                () => tab.Terminal.SearchTotal == 3 && tab.Terminal.SearchSelected >= 0,
                message: $"搜索 needle 未回填，SearchTotal={tab.Terminal.SearchTotal}，SearchSelected={tab.Terminal.SearchSelected}");
            started.Stop();
            Assert.True(tab.IsFindBarOpen);
            int first = tab.Terminal.SearchSelected;
            tab.FindNextCommand.Execute(null);
            Assert.NotEqual(first, tab.Terminal.SearchSelected);
            tab.FindPreviousCommand.Execute(null);
            Assert.Equal(first, tab.Terminal.SearchSelected);

            tab.FindQuery = "missing";
            tab.FindNextCommand.Execute(null);
            // 没有 IsSearching。SearchTotal==0 在回填前也成立，不能当完成信号。
            // 有结果的查询已经证明回填路径；missing 再按该耗时泵一段，覆盖 24ms 定时刷新。
            int settleMs = (int)Math.Clamp(started.ElapsedMilliseconds * 3, 400, 1500);
            long settleStart = Stopwatch.GetTimestamp();
            HeadlessAvalonia.WaitUntil(
                () => Stopwatch.GetElapsedTime(settleStart).TotalMilliseconds >= settleMs
                    && string.Equals(tab.Terminal.SearchNeedle, "missing", StringComparison.Ordinal),
                timeoutMs: settleMs + 1000,
                message: $"missing 查询未进入可观察状态，needle={tab.Terminal.SearchNeedle}");
            Assert.NotEmpty(tab.FindStatus);
            Assert.Equal(0, tab.Terminal.SearchTotal);
            tab.CloseFindCommand.Execute(null);
            Assert.False(tab.IsFindBarOpen);
            Assert.Empty(tab.FindStatus);
            Assert.Equal(0, tab.Terminal.SearchTotal);
        }
        finally
        {
            window.Close();
            await tab.DisposeAsync();
        }
    });
}
