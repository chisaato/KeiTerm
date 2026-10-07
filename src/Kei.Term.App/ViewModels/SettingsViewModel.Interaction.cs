using Kei.Term.App.Services;

namespace Kei.Term.App.ViewModels;

public partial class SettingsViewModel
{
    public void SetInteraction(IInteractionService interaction)
    {
        _fileTransfer.Interaction = interaction;
        if (ProxyPage != null) ProxyPage.Interaction = interaction;
    }
}
