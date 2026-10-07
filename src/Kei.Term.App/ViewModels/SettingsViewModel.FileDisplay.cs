using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Models;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

public partial class SettingsViewModel
{
    private void LoadFileDisplaySettings(AppSettings settings)
        => _fileTransfer.SetSizeDisplayMode(settings.FileTransfer.SizeDisplayMode);

    private void ApplyFileDisplaySettings(AppSettings settings)
        => settings.FileTransfer.SizeDisplayMode = _fileTransfer.SelectedSizeDisplay.Mode;

    private void NotifyFileDisplaySettings()
        => WeakReferenceMessenger.Default.Send(new FileSizeDisplayChangedMessage(_settingsService.Current.FileTransfer.SizeDisplayMode));
}
