using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.App.Helpers;
using TitleTemplateRenderer = Kei.Term.Core.Services.TabTitleTemplate;

namespace Kei.Term.App.ViewModels.Settings;

// 「终端」分类页：回滚缓冲与终端仿真
public partial class TerminalSettingsPage : ViewModelBase
{
    // 可向上回滚查看的最大行数
    [ObservableProperty]
    private int _scrollbackLines = 5000;

    // 新会话上报的 TERM 值
    [ObservableProperty]
    private string _defaultTerminalType = "xterm-256color";

    // 标签标题跟随远端（OSC 0/2）标题的全局默认；会话可覆盖
    [ObservableProperty]
    private bool _tabTitleFollowsRemote = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitlePreview))]
    [NotifyPropertyChangedFor(nameof(TitleTemplateError))]
    [NotifyPropertyChangedFor(nameof(HasTitleTemplateError))]
    private string _tabTitleTemplate = string.Empty;

    public string TitlePreview => Preview("/home/ops", "ops@server:~", "production · ops@server:~");
    public IReadOnlyList<TabTitleVariable> TitleVariables { get; } = TitleTemplateRenderer.Variables
        .Select(variable => new TabTitleVariable("{" + variable + "}", Strings.Get("Settings.TabBar.Variable." + variable))).ToArray();
    public string? TitleTemplateError => TitleTemplateRenderer.TryRender(TabTitleTemplate, PreviewContext("/home/ops", "ops@server:~"), out _, out string? error)
        ? null : Strings.Format("Settings.TabBar.TemplateError", error);
    public bool HasTitleTemplateError => TitleTemplateError != null;

    private string Preview(string path, string title, string fallback)
        => !string.IsNullOrWhiteSpace(TabTitleTemplate)
            && TitleTemplateRenderer.TryRender(TabTitleTemplate, PreviewContext(path, title), out string result, out _)
            && !string.IsNullOrWhiteSpace(result) ? result : fallback;

    private static TabTitleContext PreviewContext(string path, string title)
        => new("production", "server.example.com", "22", "ops", "server", path, "203.0.113.10", title);

    // 侧栏中的文件视图跟随终端目录的全局默认；会话可覆盖
    public IReadOnlyList<CwdFollowOption> CwdFollowModes => SessionBehaviorOptions.CwdFollowModes;

    [ObservableProperty]
    private CwdFollowOption? _selectedCwdFollow = SessionBehaviorOptions.CwdFollowModes[0];

    public void SetCwdFollow(CwdFollowMode mode)
        => SelectedCwdFollow = CwdFollowModes.FirstOrDefault(o => o.Mode == mode) ?? CwdFollowModes[0];

    public IReadOnlyList<TerminalMultiplexerOption> MultiplexerModes => SessionBehaviorOptions.MultiplexerModes;

    [ObservableProperty]
    private TerminalMultiplexerOption? _selectedMultiplexer = SessionBehaviorOptions.MultiplexerModes[1];

    public void SetMultiplexerMode(TerminalMultiplexerMode mode)
        => SelectedMultiplexer = MultiplexerModes.FirstOrDefault(option => option.Mode == mode)
            ?? MultiplexerModes.First(option => option.Mode == TerminalMultiplexerMode.Auto);
}

public sealed record TabTitleVariable(string Placeholder, string Description);
