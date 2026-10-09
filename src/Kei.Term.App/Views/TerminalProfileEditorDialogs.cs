using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.Views;

internal static class TerminalProfileEditorDialogs
{
    public static async Task<TerminalProfile?> EditAsync(
        Window owner, IInteractionService interaction, TerminalProfile? source,
        TerminalFontSnapshot font, bool importJson = false)
    {
        if (importJson)
        {
            source = BuiltInPresets.GetDefaultTerminalProfile().DeepCopy();
            source.Id = Guid.NewGuid().ToString();
            source.Name = string.Empty;
            source.IsBuiltIn = false;
        }

        TerminalProfileEditViewModel draft = new(source, font);
        bool confirmed = await new TerminalProfileEditWindow(draft, interaction, importJson)
            .ShowDialog<bool>(FindDialogOwner(owner));
        return confirmed && draft.IsConfirmed ? draft.ResultProfile : null;
    }

    public static Task<TerminalProfile?> EditJsonAsync(Window owner, TerminalProfile? source)
    {
        TerminalThemeJsonImportViewModel draft = source == null
            ? new() : TerminalThemeJsonImportViewModel.FromProfile(source);
        return new TerminalThemeJsonImportWindow(draft)
            .ShowDialog<TerminalProfile?>(FindDialogOwner(owner));
    }

    // 独立入口与编辑页入口共用同一套弹窗，JSON 子窗口始终属于正在编辑的配色窗口。
    private static Window FindDialogOwner(Window owner)
    {
        Window current = owner;
        while (current.OwnedWindows.LastOrDefault(window => window.IsVisible) is { } child)
            current = child;
        return current;
    }
}
