using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia.Threading;
using Kei.Term.App.Models;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

public partial class TerminalTabViewModel
{
    [ObservableProperty]
    private double _fileManagerSidebarRatio = FileManagerSidebarSizing.DefaultRatio;

    private void RegisterSidebarSizingMessages()
    {
        WeakReferenceMessenger.Default.Register<TerminalTabViewModel, FileManagerSidebarRatioChangedMessage>(this, static (recipient, message) =>
        {
            void Apply() => recipient.FileManagerSidebarRatio = FileManagerSidebarSizing.ClampDefaultRatio(message.Ratio);
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
        });
    }
}
