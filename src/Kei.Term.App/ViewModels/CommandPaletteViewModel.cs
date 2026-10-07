using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.ViewModels;

public enum PaletteAction { NewTab, QuickConnect, ConnectSavedSession, NewSession, Settings, ToggleSessionManager, ToggleComposeBar, ConnectSession }

public sealed record CommandPaletteItem(string Title, string Description, string ShortcutLabel,
    PaletteAction Action, Guid? SessionId = null)
{
    public bool IsEnabled => true;
}

// 面板只返回用户选择；调用方在窗口关闭后执行，避免模态弹窗相互嵌套。
public partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly IReadOnlyList<CommandPaletteItem> _items;

    public CommandPaletteViewModel(IReadOnlyList<CommandPaletteItem> items, bool savedSessionsOnly = false)
    {
        _items = items;
        Title = Strings.Get(savedSessionsOnly ? "Menu.File.ConnectSavedSession" : "Menu.Tools.CommandPalette");
        EmptyMessage = Strings.Get(savedSessionsOnly && items.Count == 0 ? "CommandPalette.NoSavedSessions" : "CommandPalette.NoResults");
        _query = savedSessionsOnly ? string.Empty : ">";
        ApplyFilter();
    }

    public string Title { get; }
    public string EmptyMessage { get; }
    public bool HasResults => Results.Count > 0;
    public bool IsConfirmed { get; private set; }
    public CommandPaletteItem? Result { get; private set; }
    public event Action? RequestClose;

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private IReadOnlyList<CommandPaletteItem> _results = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteSelectedCommand))]
    private CommandPaletteItem? _selectedResult;

    partial void OnQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = Query.TrimStart();
        bool commandsOnly = query.StartsWith('>');
        if (commandsOnly) query = query[1..];
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Results = _items.Where(item => (!commandsOnly || item.Action != PaletteAction.ConnectSession) && words.All(word =>
            item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        int index = SelectedResult == null ? 0 : Results.ToList().IndexOf(SelectedResult);
        SelectedResult = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    private bool CanExecuteSelected() => SelectedResult != null && Results.Contains(SelectedResult);

    [RelayCommand(CanExecute = nameof(CanExecuteSelected))]
    private void ExecuteSelected()
    {
        if (SelectedResult == null || !Results.Contains(SelectedResult)) return;
        Result = SelectedResult;
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
