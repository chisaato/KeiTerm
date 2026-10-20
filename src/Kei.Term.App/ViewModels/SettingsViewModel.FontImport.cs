using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.Core.Fonts;

namespace Kei.Term.App.ViewModels;

public partial class SettingsViewModel
{
    [RelayCommand]
    private async Task ImportKonsoleFontAsync()
    {
        if (IsBusy) return;
        try
        {
            string? path = await _interaction.PickKonsoleProfileFileAsync();
            if (path is null) return;
            string text = await File.ReadAllTextAsync(path);
            if (!FontFaceNaming.TryParseKonsoleProfile(text, out KonsoleFont? font, out string error))
                throw new FormatException(error);
            (string family, int resolvedWeight) = SystemFontScanner.ResolveLegacyFamily(font!.Family);
            // 只有旧式别名字族才采用字体文件的字重；现代族保留 QFont 指定的字重。
            bool namedWeight = !string.Equals(family, font.Family, StringComparison.OrdinalIgnoreCase)
                && font.Family.EndsWith(" " + FontFaceNaming.WeightName(resolvedWeight), StringComparison.OrdinalIgnoreCase);
            int weight = namedWeight ? resolvedWeight : font.Weight;
            _appearance.FontFamily = family;
            _appearance.FontWeight = weight;
            _appearance.FontSize = font.PointSize;
            await _interaction.NotifyAsync(Strings.Get("Settings.Appearance.ImportKonsoleFont"),
                string.Format(Strings.Get("Settings.Appearance.ImportKonsoleFontSuccess"), _appearance.FontSummary),
                NotificationLevel.Success);
        }
        catch (Exception ex)
        {
            await _interaction.NotifyAsync(Strings.Get("Settings.Appearance.ImportKonsoleFont"),
                string.Format(Strings.Get("Settings.Appearance.ImportKonsoleFontError"), ex.Message),
                NotificationLevel.Error);
        }
    }
}
