using System;
using System.Linq;
using Avalonia.Threading;
using Kei.Term.App.Models;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

// 所有终端共享一个字体快照入口；草稿连续变化只在下一轮渲染前应用最后一份。
public partial class MainViewModel
{
    private TerminalFontSnapshot? _pendingTerminalFont;
    private bool _terminalFontRefreshQueued;

    private void OnTerminalFontSnapshotChanged(TerminalFontSnapshot snapshot)
    {
        _uiDispatch(() =>
        {
            if (_disposed != 0) return;
            _pendingTerminalFont = snapshot;
            if (_terminalFontRefreshQueued) return;
            _terminalFontRefreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _terminalFontRefreshQueued = false;
                TerminalFontSnapshot? latest = _pendingTerminalFont;
                _pendingTerminalFont = null;
                if (_disposed == 0 && latest != null) ApplyTerminalFontSnapshot(latest);
            }, DispatcherPriority.Render);
        });
    }

    private void ApplyTerminalFontSnapshot(TerminalFontSnapshot snapshot)
    {
        foreach (TerminalTabViewModel tab in Tabs.ToList())
        {
            // 惰性控件只保存快照，不因预览强制创建控件。
            if (!tab.IsDisposed) tab.ApplyFontSnapshot(snapshot);
        }
    }

    private static TerminalFontSnapshot BuildAppliedFontSnapshot(AppSettings settings)
    {
        settings.MigrateFallbackFonts();
        return new TerminalFontSnapshot(settings.FontFamily, settings.TerminalFallbackFonts,
            settings.FontSize, settings.CursorBlink, settings.TerminalFontWeight);
    }

    public void ApplyGlobalFontZoom(int steps)
    {
        if (_disposed != 0 || steps == 0) return;
        AppSettings settings = _settingsService.Current;
        double target = TerminalFontZoom.Step(settings.FontSize, steps);
        if (target == settings.FontSize) return;
        settings.FontSize = target;
        TerminalFontSnapshot current = _profileManager?.WorkingTerminalFontSnapshot ?? BuildAppliedFontSnapshot(settings);
        TerminalFontSnapshot snapshot = new(current.PrimaryFontFamily, current.FallbackFonts, target,
            current.CursorBlink, current.FontWeight);
        // 缩放保留其他外观草稿，并使尚未执行的预览不能覆盖新的字号。
        _pendingTerminalFont = null;
        ApplyTerminalFontSnapshot(snapshot);
        _profileManager?.NotifyTerminalFontSnapshotChanged(snapshot);
        _ = _settingsService.CommitFontSizeAsync(target);
    }

    private void OnTabFontZoomRequested(int steps) => ApplyGlobalFontZoom(steps);
}
