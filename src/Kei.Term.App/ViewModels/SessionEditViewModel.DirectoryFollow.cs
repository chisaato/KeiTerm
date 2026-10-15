using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

public partial class SessionEditViewModel
{
    public IReadOnlyList<InheritableOption<CwdFollowMode>> CwdFollowOptions { get; private set; } = [];
    public IReadOnlyList<InheritableOption<TerminalMultiplexerMode>> MultiplexerOptions { get; private set; } = [];

    [ObservableProperty]
    private InheritableOption<CwdFollowMode>? _selectedCwdFollow;

    [ObservableProperty]
    private InheritableOption<TerminalMultiplexerMode>? _selectedMultiplexer;

    private void InitializeDirectoryFollowPreferences(SessionOverrides overrides, AppSettings global)
    {
        CwdFollowOptions = SessionBehaviorOptions.InheritableCwdFollow(global.CwdFollowMode);
        MultiplexerOptions = SessionBehaviorOptions.InheritableMultiplexerMode(global.TerminalMultiplexerMode);
        SelectedCwdFollow = CwdFollowOptions.FirstOrDefault(option => option.Value == overrides.CwdFollow) ?? CwdFollowOptions[0];
        SelectedMultiplexer = MultiplexerOptions.FirstOrDefault(option => option.Value == overrides.MultiplexerMode) ?? MultiplexerOptions[0];
    }

    private void ApplyDirectoryFollowPreferences(SessionOverrides overrides)
    {
        // null 明确保留“继承全局”语义，之后修改默认值时会话无需再次编辑。
        overrides.CwdFollow = SelectedCwdFollow?.Value;
        overrides.MultiplexerMode = SelectedMultiplexer?.Value;
    }
}
