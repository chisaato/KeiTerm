namespace Kei.Term.App.Services.ContextMenus;

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;

public interface ISystemContextMenuPresenter
{
    bool IsSupported(TopLevel topLevel);
    bool TryShow(Control owner, Point position, ContextMenuSnapshot snapshot, out int selectedId);
}

// 在原菜单的 Opening 处理器之后接管显示，远端文件菜单的动态构建和取消逻辑仍然生效。
public sealed class SystemContextMenuController : IDisposable
{
    private readonly TopLevel _root;
    private readonly Func<bool> _isEnabled;
    private readonly ISystemContextMenuPresenter _presenter;
    private readonly ILogger? _logger;
    private ContextMenu? _pendingMenu;
    private CancelEventHandler? _pendingHandler;

    public SystemContextMenuController(TopLevel root, Func<bool> isEnabled,
        ISystemContextMenuPresenter? presenter = null, ILogger? logger = null)
    {
        _root = root;
        _isEnabled = isEnabled;
        _presenter = presenter ?? new SystemContextMenuPresenter();
        _logger = logger;
        root.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs request)
    {
        ClearPendingHandler();
        if (request.Handled || !_isEnabled() || !_presenter.IsSupported(_root))
        {
            return;
        }

        Visual? current = request.Source as Visual;
        while (current != null && current is not Control { ContextMenu: not null })
        {
            current = current.GetVisualParent();
        }
        if (current is not Control { ContextMenu: { } menu } owner)
        {
            return;
        }

        Point position = request.TryGetPosition(owner, out Point pointer)
            ? pointer : new Point(0, owner.Bounds.Height);
        _pendingMenu = menu;
        _pendingHandler = (_, opening) =>
        {
            ClearPendingHandler();
            if (opening.Cancel)
            {
                return;
            }

            // ContextMenu 通常在 Popup 创建时才获得逻辑父级。在原生显示期间借用宿主，
            // 使编译绑定（包括 $parent[Window]）和 CanExecute 得到同样的数据上下文。
            var logicalParent = menu.Parent;
            ISetLogicalParent parentSetter = menu;
            parentSetter.SetParent(null);
            parentSetter.SetParent(owner);
            try
            {
                ContextMenuSnapshot? snapshot = ContextMenuSnapshot.Create(menu);
                if (snapshot != null && _presenter.TryShow(owner, position, snapshot, out int id))
                {
                    opening.Cancel = true;
                    request.Handled = true;
                    // 原生菜单的跟踪循环已经退出，才执行可能打开弹窗的应用命令。
                    snapshot.Invoke(id);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "系统上下文菜单不可用，回退应用菜单");
            }
            finally
            {
                parentSetter.SetParent(null);
                parentSetter.SetParent(logicalParent);
            }
        };
        menu.Opening += _pendingHandler;
    }

    private void ClearPendingHandler()
    {
        if (_pendingMenu != null && _pendingHandler != null)
        {
            _pendingMenu.Opening -= _pendingHandler;
        }
        _pendingMenu = null;
        _pendingHandler = null;
    }

    public void Dispose()
    {
        ClearPendingHandler();
        _root.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested);
    }
}
