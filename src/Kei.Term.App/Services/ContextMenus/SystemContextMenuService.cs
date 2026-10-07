namespace Kei.Term.App.Services.ContextMenus;

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

// 应用级开关覆盖主窗口和设置/编辑弹窗；窗口关闭时解除路由监听。
public sealed class SystemContextMenuService : IDisposable
{
    private readonly Dictionary<Window, SystemContextMenuController> _controllers = new();
    private readonly IDisposable _opened;
    private readonly IDisposable _closed;

    public SystemContextMenuService(Func<bool> isEnabled, ISystemContextMenuPresenter? presenter = null, ILogger? logger = null)
    {
        _opened = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (!_controllers.ContainsKey(window))
            {
                _controllers.Add(window, new SystemContextMenuController(window, isEnabled, presenter, logger));
            }
        });
        _closed = Window.WindowClosedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (_controllers.Remove(window, out SystemContextMenuController? controller))
            {
                controller.Dispose();
            }
        });
    }

    public void Dispose()
    {
        _opened.Dispose();
        _closed.Dispose();
        foreach (SystemContextMenuController controller in _controllers.Values)
        {
            controller.Dispose();
        }
        _controllers.Clear();
    }
}
