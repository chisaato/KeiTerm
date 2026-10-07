namespace Kei.Term.App.Services.ContextMenus;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

internal static class MacOsContextMenu
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly MenuAction ActionCallback = OnAction;
    private static readonly Dictionary<nint, int> Selections = new();
    private static nint _targetClass;

    public static bool Show(TopLevel window, Control owner, Point position,
        IReadOnlyList<NativeContextMenuEntry> entries, out int selectedId)
    {
        selectedId = 0;
        Point? localPosition = owner.TranslatePoint(position, window);
        if (localPosition == null) return false;

        nint pool = Send(GetClass("NSAutoreleasePool"), Sel("new"));
        nint target = 0;
        nint menu = 0;
        try
        {
            target = Send(GetTargetClass(), Sel("new"));
            Selections.Add(target, 0);
            menu = Build(entries, target);
            nint view = Send(window.TryGetPlatformHandle()!.Handle, Sel("contentView"));
            Point point = localPosition.Value;
            bool flipped = Send(view, Sel("isFlipped")) != 0;
            NativePoint nativePoint = new(point.X, flipped ? point.Y : window.ClientSize.Height - point.Y);

            // 使用 NSMenu 自己的跟踪循环和系统外观，不在原生回调中执行应用命令。
            PopUp(menu, Sel("popUpMenuPositioningItem:atLocation:inView:"), 0, nativePoint, view);
            selectedId = Selections[target];
            return true;
        }
        finally
        {
            if (menu != 0) Send(menu, Sel("release"));
            if (target != 0)
            {
                Selections.Remove(target);
                Send(target, Sel("release"));
            }
            Send(pool, Sel("drain"));
        }
    }

    private static nint Build(IReadOnlyList<NativeContextMenuEntry> entries, nint target)
    {
        nint menu = Send(Send(GetClass("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), Text(string.Empty));
        try
        {
            SendInteger(menu, Sel("setAutoenablesItems:"), 0);
            foreach (NativeContextMenuEntry entry in entries)
            {
                if (entry.IsSeparator)
                {
                    Send(menu, Sel("addItem:"), Send(GetClass("NSMenuItem"), Sel("separatorItem")));
                    continue;
                }
                nint item = CreateItem(Send(GetClass("NSMenuItem"), Sel("alloc")),
                    Sel("initWithTitle:action:keyEquivalent:"), Text(entry.Label),
                    Sel("performKeiContextAction:"), Text(string.Empty));
                try
                {
                    Send(item, Sel("setTarget:"), target);
                    SendInteger(item, Sel("setTag:"), entry.Id);
                    SendInteger(item, Sel("setEnabled:"), entry.IsEnabled ? 1 : 0);
                    SendInteger(item, Sel("setState:"), entry.IsChecked ? 1 : 0);
                    if (entry.Children.Count > 0)
                    {
                        nint child = Build(entry.Children, target);
                        try { Send(item, Sel("setSubmenu:"), child); }
                        finally { Send(child, Sel("release")); }
                    }
                    Send(menu, Sel("addItem:"), item);
                }
                finally
                {
                    Send(item, Sel("release"));
                }
            }
            return menu;
        }
        catch
        {
            Send(menu, Sel("release"));
            throw;
        }
    }

    private static nint GetTargetClass()
    {
        if (_targetClass != 0) return _targetClass;
        // 类与回调保留到进程结束；每次弹出的 target 和菜单在 Show 的 finally 中释放。
        _targetClass = AllocateClass(GetClass("NSObject"), "KeiTermContextMenuTarget", 0);
        if (_targetClass == 0) throw new InvalidOperationException("无法创建系统菜单点击接收器");
        if (!AddMethod(_targetClass, Sel("performKeiContextAction:"),
                Marshal.GetFunctionPointerForDelegate(ActionCallback), "v@:@"))
        {
            DisposeClass(_targetClass);
            _targetClass = 0;
            throw new InvalidOperationException("无法注册系统菜单点击处理器");
        }
        RegisterClass(_targetClass);
        return _targetClass;
    }

    private static void OnAction(nint target, nint selector, nint item)
    {
        // 原生回调仅记录选择，防止异常或异步弹窗跨越 Objective-C 调用边界。
        if (Selections.ContainsKey(target))
        {
            Selections[target] = (int)Send(item, Sel("tag"));
        }
    }

    private static nint Text(string text) => CreateString(GetClass("NSString"), Sel("stringWithUTF8String:"), text);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(double x, double y)
    {
        public readonly double X = x;
        public readonly double Y = y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MenuAction(nint target, nint selector, nint item);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Sel(string name);
    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern nint AllocateClass(nint parent, string name, nuint extraBytes);
    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClass(nint cls);
    [DllImport(ObjC, EntryPoint = "objc_disposeClassPair")]
    private static extern void DisposeClass(nint cls);
    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddMethod(nint cls, nint selector, nint implementation, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint CreateString(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint CreateItem(nint receiver, nint selector, nint title, nint action, nint key);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool PopUp(nint receiver, nint selector, nint item, NativePoint point, nint view);
}
