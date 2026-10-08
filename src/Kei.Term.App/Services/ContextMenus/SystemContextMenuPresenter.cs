namespace Kei.Term.App.Services.ContextMenus;

using System;
using Avalonia;
using Avalonia.Controls;

public sealed class SystemContextMenuPresenter : ISystemContextMenuPresenter
{
    public bool IsSupported(TopLevel topLevel)
    {
        string? descriptor = topLevel.TryGetPlatformHandle()?.HandleDescriptor;
        return OperatingSystem.IsMacOS() && descriptor == "NSWindow"
            || OperatingSystem.IsWindows() && descriptor == "HWND";
    }

    public bool TryShow(Control owner, Point position, ContextMenuSnapshot snapshot, out int selectedId)
    {
        selectedId = 0;
        TopLevel? topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel == null || !IsSupported(topLevel))
        {
            return false;
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacOsContextMenu.Show(topLevel, owner, position, snapshot.Entries, out selectedId);
        }
        return WindowsContextMenu.Show(topLevel, owner.PointToScreen(position), snapshot.Entries, out selectedId);
    }
}
