using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.App.ViewModels;

public partial class SettingsViewModel
{
    private IInteractionService _quickUnlockInteraction = NullInteractionService.Instance;

    public void ConfigureQuickUnlock(VaultQuickUnlockService service)
        => _security.QuickUnlock = new VaultQuickUnlockSettingsViewModel(service, () => _quickUnlockInteraction);

    public Task RefreshQuickUnlockAsync() => _security.QuickUnlock?.RefreshAsync() ?? Task.CompletedTask;
}
