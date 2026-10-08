namespace Kei.Term.App.Services.ContextMenus;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

internal static class WindowsContextMenu
{
    public static bool Show(TopLevel window, PixelPoint position,
        IReadOnlyList<NativeContextMenuEntry> entries, out int selectedId)
    {
        selectedId = 0;
        nint handle = window.TryGetPlatformHandle()!.Handle;
        nint menu = Build(entries);
        try
        {
            // RETURNCOMMAND + NONOTIFY：由应用执行原 MenuItem 的命令，不发送 WM_COMMAND。
            uint flags = 0x0100 | 0x0080 | 0x0002;
            if (GetSystemMetrics(40) != 0)
            {
                flags |= 0x0008;
            }
            selectedId = TrackPopupMenu(menu, flags, position.X, position.Y, 0, handle, 0);
            return true;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static nint Build(IReadOnlyList<NativeContextMenuEntry> entries)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        try
        {
            foreach (NativeContextMenuEntry entry in entries)
            {
                uint flags = entry.IsSeparator ? 0x0800u : 0u;
                if (!entry.IsEnabled) flags |= 0x0001;
                if (entry.IsChecked) flags |= 0x0008;

                nint child = 0;
                bool appended = false;
                try
                {
                    nuint itemId = (nuint)entry.Id;
                    if (entry.Children.Count > 0)
                    {
                        child = Build(entry.Children);
                        flags |= 0x0010;
                        itemId = (nuint)child;
                    }
                    // 标题中的 & 是文字，不应被 Win32 自动解释成访问键。
                    appended = AppendMenu(menu, flags, itemId, entry.Label.Replace("&", "&&"));
                    if (!appended)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }
                finally
                {
                    // 成功挂接后子菜单归父菜单所有；失败时单独释放。
                    if (child != 0 && !appended) DestroyMenu(child);
                }
            }
            return menu;
        }
        catch
        {
            DestroyMenu(menu);
            throw;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
