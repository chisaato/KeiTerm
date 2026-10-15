using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Models;

namespace Kei.Term.App.ViewModels;

public partial class MainViewModel
{
    private void InitializeTitleTemplates()
        => WeakReferenceMessenger.Default.Register<MainViewModel, TabTitleTemplateChangedMessage>(this,
            static (recipient, message) =>
            {
                void Apply()
                {
                    foreach (TerminalTabViewModel tab in recipient.Tabs)
                        if (!tab.IsDisposed) tab.ApplyTitleTemplate(message.Template);
                }
                if (Dispatcher.UIThread.CheckAccess()) Apply();
                else Dispatcher.UIThread.Post(Apply);
            });
}
