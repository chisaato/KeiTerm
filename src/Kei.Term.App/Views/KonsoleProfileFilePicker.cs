using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views;

internal static class KonsoleProfileFilePicker
{
    public static async Task<string?> PickAsync(Window owner)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("Settings.Appearance.ImportKonsoleFont"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Konsole profile (*.profile)") { Patterns = ["*.profile"] }]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
