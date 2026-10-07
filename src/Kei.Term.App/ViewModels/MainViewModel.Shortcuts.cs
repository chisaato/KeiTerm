namespace Kei.Term.App.ViewModels;

using Kei.Term.App.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Logging;
using Kei.Term.Core.Models;

// 平台快捷键的展示由与窗口绑定相同的定义生成。
public partial class MainViewModel
{
    public string QuickConnectShortcutLabel => AppShortcuts.Label(AppShortcuts.QuickConnect);
    public string NewSessionShortcutLabel => AppShortcuts.Label(AppShortcuts.NewSession);
    public string NewTabShortcutLabel => AppShortcuts.Label(AppShortcuts.NewTab);
    public string ConnectSavedSessionShortcutLabel => AppShortcuts.Label(AppShortcuts.ConnectSavedSession);
    public string CommandPaletteShortcutLabel => AppShortcuts.Label(AppShortcuts.CommandPalette(CurrentSettings.CommandPaletteShortcut));
    public string CommandPaletteToolTip => $"{Strings.Get("Menu.Tools.CommandPalette")} ({CommandPaletteShortcutLabel})";
    public string QuickConnectToolTip => $"{Strings.Get("Menu.File.QuickConnect")} ({QuickConnectShortcutLabel})";

    public void RefreshShortcuts()
    {
        OnPropertyChanged(nameof(CommandPaletteShortcutLabel));
        OnPropertyChanged(nameof(CommandPaletteToolTip));
    }

    public CommandPaletteViewModel CreateCommandPalette(bool savedSessionsOnly = false)
    {
        List<CommandPaletteItem> items = [];
        if (!savedSessionsOnly)
        {
            items.Add(new(Strings.Get("Menu.File.NewTab"), Strings.Get("NewTab.Description"), NewTabShortcutLabel, PaletteAction.NewTab));
            items.Add(new(Strings.Get("Menu.File.QuickConnect"), Strings.Get("NewTab.QuickConnectDescription"), QuickConnectShortcutLabel, PaletteAction.QuickConnect));
            items.Add(new(Strings.Get("Menu.File.ConnectSavedSession"), Strings.Get("NewTab.SavedSessionDescription"), ConnectSavedSessionShortcutLabel, PaletteAction.ConnectSavedSession));
            items.Add(new(Strings.Get("Menu.File.NewSession"), Strings.Get("CommandPalette.NewSessionDescription"), NewSessionShortcutLabel, PaletteAction.NewSession));
            items.Add(new(Strings.Get("Menu.Tools.Settings"), Strings.Get("CommandPalette.SettingsDescription"), "", PaletteAction.Settings));
            items.Add(new(Strings.Get("Menu.View.SessionManager"), "", "", PaletteAction.ToggleSessionManager));
            items.Add(new(Strings.Get("Menu.View.ComposeBar"), "", "", PaletteAction.ToggleComposeBar));
        }
        Dictionary<Guid, TreeNodeBase> nodes = _allNodesCache.ToDictionary(node => node.Id);
        foreach (SessionNode session in _allNodesCache.OfType<SessionNode>().OrderBy(session => session.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            List<string> path = [];
            Guid? parent = session.ParentId;
            HashSet<Guid> visited = [];
            while (parent is { } id && visited.Add(id) && nodes.TryGetValue(id, out TreeNodeBase? folder))
            {
                path.Insert(0, folder.Name);
                parent = folder.ParentId;
            }
            string user = string.IsNullOrWhiteSpace(session.Username) ? "" : $"{session.Username}@";
            string endpoint = $"{user}{session.Host}:{session.Port ?? CurrentSettings.DefaultPort}";
            string description = path.Count > 0 ? $"{string.Join(" / ", path)} · {endpoint}" : endpoint;
            items.Add(new(session.Name, description, "", PaletteAction.ConnectSession, session.Id));
        }
        return new CommandPaletteViewModel(items, savedSessionsOnly);
    }

    [RelayCommand]
    private Task ConnectSavedSessionAsync() => Safe.RunAsync(_logger, "选择已保存会话", async () =>
    {
        await RunConnectionFromWorkspaceAsync(async () =>
        {
            CommandPaletteItem? selection = await Interaction.ShowCommandPaletteAsync(CreateCommandPalette(savedSessionsOnly: true));
            if (selection is { Action: PaletteAction.ConnectSession, SessionId: { } id })
                await OpenSessionCommand.ExecuteAsync(_allNodesCache.OfType<SessionNode>().FirstOrDefault(session => session.Id == id));
        });
    });

    [RelayCommand]
    private Task OpenCommandPaletteAsync() => Safe.RunAsync(_logger, "打开快捷面板", async () =>
    {
        CommandPaletteItem? selection = await Interaction.ShowCommandPaletteAsync(CreateCommandPalette());
        if (selection != null) await ExecutePaletteItemAsync(selection);
    });

    public Task ExecutePaletteItemAsync(CommandPaletteItem item) => item.Action switch
    {
        PaletteAction.NewTab => ExecuteImmediate(NewTabCommand),
        PaletteAction.QuickConnect => QuickConnectCommand.ExecuteAsync(null),
        PaletteAction.ConnectSavedSession => ConnectSavedSessionCommand.ExecuteAsync(null),
        PaletteAction.NewSession => CreateSessionCommand.ExecuteAsync(null),
        PaletteAction.Settings => OpenSettingsCommand.ExecuteAsync(null),
        PaletteAction.ToggleSessionManager => ExecuteImmediate(ToggleSessionManagerCommand),
        PaletteAction.ToggleComposeBar => ExecuteImmediate(ToggleComposeBarCommand),
        PaletteAction.ConnectSession => OpenSessionCommand.ExecuteAsync(_allNodesCache.OfType<SessionNode>().FirstOrDefault(session => session.Id == item.SessionId)),
        _ => Task.CompletedTask
    };

    private static Task ExecuteImmediate(IRelayCommand command)
    {
        command.Execute(null);
        return Task.CompletedTask;
    }
}
