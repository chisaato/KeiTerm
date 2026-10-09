using Kei.Term.App.Services;

namespace Kei.Term.App.ViewModels;

public partial class SettingsViewModel
{
    private IInteractionService _interaction = NullInteractionService.Instance;

    public void SetInteraction(IInteractionService interaction)
    {
        System.ArgumentNullException.ThrowIfNull(interaction);
        _interaction = interaction;
        _fileTransfer.Interaction = interaction;
        if (ProxyPage != null) ProxyPage.Interaction = interaction;
    }
}
