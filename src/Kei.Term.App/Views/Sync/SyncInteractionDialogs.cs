using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Sync;

namespace Kei.Term.App.Views.Sync;

/// <summary>
/// 同步相关统一弹窗，严格遵照 KeiTokens 设计系统与紧凑桌面交互规范：
/// 1. 默认安全/默认取消（Escape 退出、取消按钮处于默认焦点或明确分离）
/// 2. 口令遮蔽与显示切换（PasswordChar）
/// 3. 明确的危险警告文案（不承诺撤回已下载数据/撤销云凭据）
/// </summary>
public static class SyncInteractionDialogs
{
    public static Window CreateBaseDialog(string title, double width = 460)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("Kei.Bg.Panel"));
        window.Bind(Window.FontFamilyProperty, window.GetResourceObservable("Kei.Font.UI"));
        return window;
    }

    public static Button CreateButton(string content, bool isAccent = false, bool isDanger = false, bool isDefault = false, bool isCancel = false)
    {
        var btn = new Button
        {
            Content = content,
            MinWidth = 80,
            Height = 28,
            Padding = new Thickness(12, 4),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (isDanger) btn.Classes.Add("danger");
        else if (isAccent) btn.Classes.Add("accent");
        return btn;
    }
}
