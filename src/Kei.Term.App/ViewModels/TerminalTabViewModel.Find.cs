using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.App.ViewModels;

// 当前标签的查找栏。只调用 TerminalControl 已有搜索 API，不扫屏、不回写 ANSI。
public partial class TerminalTabViewModel
{
    public string FindShortcutToolTip => $"{Strings.Get("Terminal.Find.Menu")} ({AppShortcuts.Label(AppShortcuts.Find)})";

    [ObservableProperty]
    private bool _isFindBarOpen;

    [ObservableProperty]
    private string _findQuery = string.Empty;

    [ObservableProperty]
    private string _findStatus = string.Empty;

    [RelayCommand]
    public void OpenFind()
    {
        IsFindBarOpen = true;
    }

    [RelayCommand]
    public void CloseFind()
    {
        IsFindBarOpen = false;
        FindStatus = string.Empty;
        if (_terminal is not TerminalControl terminal)
        {
            return;
        }

        // EndSearch 只结束搜索高亮，不清选区、不清回滚、不断开会话
        terminal.EndSearch();
        terminal.Focus();
    }

    [RelayCommand]
    public void FindNext() => MoveFind(next: true);

    [RelayCommand]
    public void FindPrevious() => MoveFind(next: false);

    private void MoveFind(bool next)
    {
        if (_terminal is not TerminalControl terminal)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FindQuery))
        {
            terminal.EndSearch();
            FindStatus = string.Empty;
            return;
        }

        string query = FindQuery;
        bool sameNeedle = string.Equals(terminal.SearchNeedle, query, StringComparison.Ordinal);
        if (!sameNeedle)
        {
            terminal.StartSearch(query);
        }

        if (sameNeedle)
        {
            if (next)
            {
                terminal.SelectNextSearchMatch();
            }
            else
            {
                terminal.SelectPreviousSearchMatch();
            }
        }
        else if (!next)
        {
            // 第一次查找会落在初始匹配上；上一个再往前挪一格
            terminal.SelectPreviousSearchMatch();
        }

        FindStatus = terminal.SearchTotal == 0
            ? Strings.Get("Terminal.Find.NoMatch")
            : string.Empty;
    }
}
