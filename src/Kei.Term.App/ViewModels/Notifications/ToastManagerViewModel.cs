using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Models;

namespace Kei.Term.App.ViewModels.Notifications;

// Toast 队列管理器：负责入队、上限控制（最多 4 条）、去重、倒计时与线程调度
public partial class ToastManagerViewModel : ObservableObject
{
    private const int MaxVisibleToasts = 4;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Task>? _copyToClipboard;
    private readonly Action<Action> _dispatchToUi;
    private readonly object _lock = new();

    // 当前在屏幕上展示的 Toast 列表（最新生成的排在最下方或直接展示，UI 容器统一从下往上堆叠）
    public ObservableCollection<ToastItemViewModel> ActiveToasts { get; } = [];

    public ToastManagerViewModel(
        TimeProvider? timeProvider = null,
        Func<string, Task>? copyToClipboard = null,
        Action<Action>? dispatchToUi = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _copyToClipboard = copyToClipboard;
        // 调度器：生产环境默认投递到 UI 线程，单元测试可直接就地执行
        _dispatchToUi = dispatchToUi ?? (action =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(action);
            }
        });
    }

    // 弹出 Toast 通知
    public ToastItemViewModel Show(string title, string message, NotificationLevel level)
    {
        ToastItemViewModel item = null!;
        _dispatchToUi(() =>
        {
            lock (_lock)
            {
                // 去重：若当前列表中已存在完全相同的 title、message 与 level，则直接刷新原 Toast（避免重复堆叠）
                ToastItemViewModel? existing = ActiveToasts.FirstOrDefault(t =>
                    string.Equals(t.Title, title, StringComparison.Ordinal)
                    && string.Equals(t.Message, message, StringComparison.Ordinal)
                    && t.Level == level);

                if (existing != null)
                {
                    existing.ResumeDismiss();
                    item = existing;
                    return;
                }

                item = new ToastItemViewModel(
                    title,
                    message,
                    level,
                    Dismiss,
                    _copyToClipboard,
                    _timeProvider);

                // 上限控制：若已达最大可见数，从最老的一端（队头）淘汰
                while (ActiveToasts.Count >= MaxVisibleToasts)
                {
                    ToastItemViewModel oldest = ActiveToasts[0];
                    ActiveToasts.RemoveAt(0);
                    oldest.Dismiss();
                }

                ActiveToasts.Add(item);
            }
        });

        return item;
    }

    // 手动或倒计时触发的移除
    public void Dismiss(ToastItemViewModel item)
    {
        _dispatchToUi(() =>
        {
            lock (_lock)
            {
                ActiveToasts.Remove(item);
            }
        });
    }

    // 清空所有
    public void Clear()
    {
        _dispatchToUi(() =>
        {
            lock (_lock)
            {
                foreach (ToastItemViewModel toast in ActiveToasts.ToList())
                {
                    toast.Dismiss();
                }
                ActiveToasts.Clear();
            }
        });
    }
}
