namespace Kei.Term.App.Workspaces;

using System;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Kei.Term.App.ViewModels;

// 启动页和连接共享同一层 Dock 标签、分屏和关闭语义。
public abstract class WorkspaceDocument : Document
{
    protected WorkspaceDocument(ViewModelBase item, string title)
    {
        Item = item;
        Id = Guid.NewGuid().ToString("N");
        Title = title;
        Context = item;
        CanFloat = false;
        CanPin = false;
        CanClose = true;
        AllowedDockOperations = AllowedDropOperations = DockOperationMask.Fill
            | DockOperationMask.Left | DockOperationMask.Right
            | DockOperationMask.Top | DockOperationMask.Bottom;
    }

    public ViewModelBase Item { get; }

    public virtual void Detach() { }
}
