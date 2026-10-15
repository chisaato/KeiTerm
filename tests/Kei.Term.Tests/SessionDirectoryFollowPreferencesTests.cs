using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Storage;

namespace Kei.Term.Tests;

public class SessionDirectoryFollowPreferencesTests
{
    [Theory]
    [InlineData(CwdFollowMode.Always, TerminalMultiplexerMode.Screen)]
    [InlineData(CwdFollowMode.Off, null)]
    [InlineData(null, TerminalMultiplexerMode.Tmux)]
    public Task SessionBehaviorControls_PersistOverridesAndRestoreGlobalInheritance(
        CwdFollowMode? explicitFollow, TerminalMultiplexerMode? explicitMultiplexer) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_session_follow_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SessionEditWindow? window = null;
        try
        {
            SqliteTreeRepository repository = new($"Data Source={Path.Combine(directory, "sessions.db")}");
            await repository.InitializeAsync();
            AppSettings defaults = new()
            {
                CwdFollowMode = explicitFollow == CwdFollowMode.Always ? CwdFollowMode.Off : CwdFollowMode.Always,
                TerminalMultiplexerMode = explicitMultiplexer == TerminalMultiplexerMode.Screen
                    ? TerminalMultiplexerMode.Tmux : TerminalMultiplexerMode.Screen
            };
            SessionNode session = new()
            {
                Name = "server",
                Host = "server.example"
            };
            await repository.SaveNodeAsync(session);
            SessionNode inherited = Assert.IsType<SessionNode>(await repository.GetNodeByIdAsync(session.Id));
            SessionEditViewModel draft = new(inherited, null, [], defaults);
            window = OpenBehaviorPage(draft);
            ComboBox follow = Find(window, "SessionDirectoryFollowModeComboBox");
            ComboBox multiplexer = Find(window, "SessionTerminalMultiplexerModeComboBox");
            Assert.True(follow.IsEffectivelyVisible);
            Assert.True(multiplexer.IsEffectivelyVisible);
            Assert.Null(Assert.IsType<InheritableOption<CwdFollowMode>>(follow.SelectedItem).Value);
            Assert.Null(Assert.IsType<InheritableOption<TerminalMultiplexerMode>>(multiplexer.SelectedItem).Value);
            follow.SelectedItem = draft.CwdFollowOptions.Single(option => option.Value == explicitFollow);
            multiplexer.SelectedItem = draft.MultiplexerOptions.Single(option => option.Value == explicitMultiplexer);
            await repository.SaveNodeAsync(draft.ApplyToModel(inherited));
            window.Close();

            SessionNode explicitSession = Assert.IsType<SessionNode>(await repository.GetNodeByIdAsync(session.Id));
            Assert.Equal(explicitFollow, explicitSession.Overrides.CwdFollow);
            Assert.Equal(explicitMultiplexer, explicitSession.Overrides.MultiplexerMode);
            ResolvedSessionConfig explicitConfig = SessionConfigBuilder.Build(explicitSession, defaults);
            Assert.Equal(explicitFollow ?? defaults.CwdFollowMode, explicitConfig.CwdFollow);
            Assert.Equal(explicitMultiplexer ?? defaults.TerminalMultiplexerMode, explicitConfig.MultiplexerMode);

            SessionEditViewModel reopened = new(explicitSession, null, [], defaults);
            window = OpenBehaviorPage(reopened);
            follow = Find(window, "SessionDirectoryFollowModeComboBox");
            multiplexer = Find(window, "SessionTerminalMultiplexerModeComboBox");
            Assert.Equal(explicitFollow, Assert.IsType<InheritableOption<CwdFollowMode>>(follow.SelectedItem).Value);
            Assert.Equal(explicitMultiplexer, Assert.IsType<InheritableOption<TerminalMultiplexerMode>>(multiplexer.SelectedItem).Value);
            follow.SelectedItem = reopened.CwdFollowOptions.Single(option => option.Value == null);
            multiplexer.SelectedItem = reopened.MultiplexerOptions.Single(option => option.Value == null);
            await repository.SaveNodeAsync(reopened.ApplyToModel(explicitSession));
            window.Close();

            SessionNode cleared = Assert.IsType<SessionNode>(await repository.GetNodeByIdAsync(session.Id));
            Assert.Null(cleared.Overrides.CwdFollow);
            Assert.Null(cleared.Overrides.MultiplexerMode);
            // 清除覆盖后，后续全局变更应直接影响解析结果，无需再次保存会话。
            defaults.CwdFollowMode = defaults.CwdFollowMode == CwdFollowMode.Off ? CwdFollowMode.Always : CwdFollowMode.Off;
            defaults.TerminalMultiplexerMode = defaults.TerminalMultiplexerMode == TerminalMultiplexerMode.Tmux
                ? TerminalMultiplexerMode.Screen : TerminalMultiplexerMode.Tmux;
            ResolvedSessionConfig changedDefaults = SessionConfigBuilder.Build(cleared, defaults);
            Assert.Equal(defaults.CwdFollowMode, changedDefaults.CwdFollow);
            Assert.Equal(defaults.TerminalMultiplexerMode, changedDefaults.MultiplexerMode);

        }
        finally
        {
            window?.Close();
            Directory.Delete(directory, recursive: true);
        }
    });

    private static SessionEditWindow OpenBehaviorPage(SessionEditViewModel model)
    {
        SessionEditWindow window = new(model);
        model.SelectedCategory = model.Categories.Single(category => Equals(category.Page, "Behavior"));
        window.Show();
        window.UpdateLayout();
        HeadlessAvalonia.Pump();
        return window;
    }

    private static ComboBox Find(SessionEditWindow window, string name)
        => window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == name);
}
