using Avalonia.Input;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.App.Views;

public partial class SettingsWindow
{
    private void CommandPaletteShortcutRecorder_KeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is SettingsViewModel vm && vm.SelectedCategory.Page is GeneralSettingsPage { IsRecordingCommandPaletteShortcut: true } page)
        {
            page.RecordCommandPaletteShortcut(args.Key, args.KeyModifiers, args.PhysicalKey);
            args.Handled = true;
        }
    }
}
