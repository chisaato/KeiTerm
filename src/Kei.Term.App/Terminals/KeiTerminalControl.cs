namespace Kei.Term.App.Terminals;

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;

// 扩展 TerminalControl：支持关闭远端鼠标上报（默认直接本地选区），并用 Shift 临时翻转行为
public sealed class KeiTerminalControl : TerminalControl
{
    private bool _localSelectionDragActive;

    public KeiTerminalControl(
        ITerminalSessionService terminalSessionService,
        ITerminalInputAdapter terminalInputAdapter,
        ITerminalSelectionService terminalSelectionService,
        ITerminalScrollService terminalScrollService,
        IVtProcessorFactory vtProcessorFactory,
        IPtyFactory ptyFactory,
        ISshCredentialProvider sshCredentialProvider,
        ISshHostKeyValidator sshHostKeyValidator,
        ITerminalTransportFactory? transportFactory)
        : base(
            terminalSessionService,
            terminalInputAdapter,
            terminalSelectionService,
            terminalScrollService,
            vtProcessorFactory,
            ptyFactory,
            sshCredentialProvider,
            sshHostKeyValidator,
            transportFactory)
    {
    }

    // 是否启用远端鼠标上报：true 时按默认行为（Shift 临时本地选区）；false 时默认本地选区（Shift 临时上报给远端）
    public bool IsMouseReportingEnabled { get; set; } = true;

    internal Func<bool>? IsRemoteMouseReportingActive { get; init; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (IsMouseReportingEnabled)
        {
            _localSelectionDragActive = false;
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Disabled;
            // 远端开启鼠标上报时，Shift 用于绕过远端捕获并开始新选区，避免被基类当作 Shift+点击扩展旧选区
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && HasSelection
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && IsRemoteMouseReportingActive?.Invoke() == true)
            {
                ClearSelection();
            }

            base.OnPointerPressed(e);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _localSelectionDragActive = false;
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Always;
            base.OnPointerPressed(e);
            return;
        }

        PointerPoint currentPoint = e.GetCurrentPoint(this);
        bool isLeft = currentPoint.Properties.IsLeftButtonPressed;
        _localSelectionDragActive = isLeft;
        MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Never;

        // 注入 Shift 绕过远端鼠标捕获时，先清除旧选区，避免被基类当作 Shift+点击扩展选区
        if (isLeft && HasSelection)
        {
            ClearSelection();
        }

        (Visual root, Point rootPos) = ResolveRootPosition(e);
        PointerPressedEventArgs synthetic = new(
            e.Source,
            e.Pointer,
            root,
            rootPos,
            e.Timestamp,
            currentPoint.Properties,
            e.KeyModifiers | KeyModifiers.Shift,
            e.ClickCount);
        base.OnPointerPressed(synthetic);
        e.Handled = synthetic.Handled;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (IsMouseReportingEnabled)
        {
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Disabled;
            base.OnPointerMoved(e);
            return;
        }

        if (!_localSelectionDragActive && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Always;
            base.OnPointerMoved(e);
            return;
        }

        MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Never;
        (Visual root, Point rootPos) = ResolveRootPosition(e);
        PointerEventArgs synthetic = new(
            PointerMovedEvent,
            e.Source,
            e.Pointer,
            root,
            rootPos,
            e.Timestamp,
            e.GetCurrentPoint(this).Properties,
            e.KeyModifiers | KeyModifiers.Shift);
        base.OnPointerMoved(synthetic);
        e.Handled = synthetic.Handled;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        bool wasLocalDrag = _localSelectionDragActive;
        _localSelectionDragActive = false;

        if (IsMouseReportingEnabled)
        {
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Disabled;
            base.OnPointerReleased(e);
            return;
        }

        if (!wasLocalDrag && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Always;
            base.OnPointerReleased(e);
            return;
        }

        MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Never;
        (Visual root, Point rootPos) = ResolveRootPosition(e);
        PointerReleasedEventArgs synthetic = new(
            e.Source,
            e.Pointer,
            root,
            rootPos,
            e.Timestamp,
            e.GetCurrentPoint(this).Properties,
            e.KeyModifiers | KeyModifiers.Shift,
            e.InitialPressMouseButton);
        base.OnPointerReleased(synthetic);
        e.Handled = synthetic.Handled;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _localSelectionDragActive = false;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (IsMouseReportingEnabled)
        {
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Disabled;
            base.OnPointerWheelChanged(e);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Always;
            base.OnPointerWheelChanged(e);
            return;
        }

        MouseShiftCapturePolicy = TerminalMouseShiftCapturePolicy.Never;
        (Visual root, Point rootPos) = ResolveRootPosition(e);
        PointerWheelEventArgs synthetic = new(
            e.Source,
            e.Pointer,
            root,
            rootPos,
            e.Timestamp,
            e.GetCurrentPoint(this).Properties,
            e.KeyModifiers | KeyModifiers.Shift,
            e.Delta);
        base.OnPointerWheelChanged(synthetic);
        e.Handled = synthetic.Handled;
    }

    private (Visual Root, Point Position) ResolveRootPosition(PointerEventArgs e)
    {
        Visual root = TopLevel.GetTopLevel(this) as Visual ?? this;
        return (root, e.GetPosition(root));
    }
}
