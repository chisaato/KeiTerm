using System.Linq;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class MainWindow
{
    private void QueueWorkspaceFocus(MainViewModel model)
    {
        ViewModelBase? active = model.ActiveWorkspaceTab;
        // Dock 的活动内容与回收视图在布局/Loaded 阶段重新挂载后，再转移焦点。
        Dispatcher.UIThread.Post(() =>
        {
            // 活动连接可能相同，但当前文档已是文件管理器；不能被延迟终端焦点切回去。
            if (!ReferenceEquals(DataContext, model) || !ReferenceEquals(model.ActiveWorkspaceTab, active)
                || IsCommandPaletteOpen || OwnedWindows.Any(window => window.IsVisible)) return;
            switch (active)
            {
                case TerminalTabViewModel terminal:
                    terminal.Terminal.Focus();
                    break;
                case FileManagerTabViewModel files:
                    foreach (FileManagerWorkspaceView view in this.GetVisualDescendants().OfType<FileManagerWorkspaceView>())
                        if (ReferenceEquals(view.DataContext, files)) view.FocusDefaultAction();
                    break;
                case NewTabViewModel starter:
                    foreach (NewTabView view in this.GetVisualDescendants().OfType<NewTabView>())
                        if (ReferenceEquals(view.DataContext, starter)) view.FocusDefaultAction();
                    break;
            }
        }, DispatcherPriority.Background);
    }
}
