namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoyalTerminal.Avalonia.Controls;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Ssh.Abstractions;

// 终端控件：惰性创建（挂 VT 回调）、字体与配色注入、网格贴底、尺寸同步与复制粘贴清屏
public partial class TerminalTabViewModel
{
    // 终端区底色：网格贴底后顶部余量与滚动条轨道用配色背景填充，避免露出窗口底色
    [ObservableProperty]
    private IBrush _terminalBackground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.Black);

    // 惰性创建：测试只验证状态/主题注入时不会构造原生 TerminalControl
    private readonly Func<TerminalControl> _terminalFactory;
    private TerminalControl? _terminal;
    private TerminalFontSnapshot _fontSnapshot;
    private readonly int _scrollbackLines;

    // Ctrl+滚轮缩放的余量累积器：触控板的小数 delta 需凑满一格才步进
    private readonly WheelNotchAccumulator _zoomAccumulator = new();

    // 请求缩放字号（正数放大、负数缩小）；由主 VM 统一写回全局设置，标签自身不碰设置
    public event Action<int>? FontZoomRequested;

    public TerminalControl Terminal
    {
        get
        {
            if (_terminal == null)
            {
                var terminal = _terminalFactory();
                terminal.ScrollbackLimit = _scrollbackLines;
                ApplyFontToTerminal(terminal, _fontSnapshot);
                // 隧道阶段拦截 Ctrl+滚轮（含已处理事件），避免滚轮被当作终端滚动或发给远端鼠标跟踪
                terminal.AddHandler(
                    InputElement.PointerWheelChangedEvent,
                    OnTerminalPointerWheelChanged,
                    RoutingStrategies.Tunnel,
                    handledEventsToo: true);
                terminal.AddHandler(InputElement.KeyDownEvent, OnTerminalKeyDown, RoutingStrategies.Tunnel);
                terminal.Loaded += OnTerminalLoaded;
                terminal.SizeChanged += OnTerminalSizeChanged;
                terminal.TerminalResized += OnTerminalGridResized;
                _terminal = terminal;
            }

            return _terminal;
        }
    }

    // 失败或断开状态下，终端内的普通回车重用当前标签重新连接。
    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None
            || IsDisposed || Config == null
            || State is not (ConnectionState.Error or ConnectionState.Disconnected))
        {
            return;
        }

        // 在终端自身处理 Enter 之前截获；连接正常时仍把回车交给远端 shell。
        e.Handled = true;
        if (!IsReconnectPending) RequestReconnectCommand.Execute(null);
    }

    // Ctrl + 垂直滚轮缩放：普通滚轮放行给终端滚动，不标记 Handled
    private void OnTerminalPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
        {
            return;
        }

        int notches = _zoomAccumulator.Accumulate(e.Delta.Y);
        if (notches != 0)
        {
            FontZoomRequested?.Invoke(notches);
        }

        // 命中 Ctrl 缩放即拦截，避免滚轮继续滚动或作为鼠标事件发给远端
        e.Handled = true;
    }

    // VT 处理器回调：应答立即发回远端；响铃与标题投递回 UI 线程，避免在 VT 解析过程中重入改界面状态
    private TerminalControl CreateHookedTerminal()
    {
        var hooks = new VtCallbackHooks
        {
            Response = data => _ = _session?.SendInputAsync(data),
            Bell = () => Dispatcher.UIThread.Post(OnRemoteBell),
            Title = title => Dispatcher.UIThread.Post(() => ApplyRemoteTitle(title))
        };
        TerminalControl terminal = TerminalControlFactory.Create(hooks);
        terminal.FontLinearMetrics = true;
        terminal.FontSubpixelPositioning = true;
        TerminalKeywordHighlight.Apply(terminal);
        return terminal;
    }

    // 更新当前生效方案并注入目标（EffectiveProfileId 同步更新）
    public void ApplyTerminalProfile(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CurrentProfile = profile;
        EffectiveProfileId = profile.Id;
        TerminalBackground = ParseBrush(profile.Background);
        _themeSink.Apply(profile);
    }

    // 不可变画刷不是 AvaloniaObject，不绑定 UI 线程：纯状态测试可在任意线程构造标签
    private static IBrush ParseBrush(string? hex)
        => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.TryParse(hex, out Color color) ? color : Colors.Black);

    // 当前生效的字体快照（含字号）：控件尚未创建时也有效；供测试与诊断读取
    public TerminalFontSnapshot CurrentFontSnapshot => _fontSnapshot;

    // 只更新字体相关属性，不触碰配色；若控件尚未创建则仅记录，待创建时应用
    public void ApplyFontSnapshot(TerminalFontSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _fontSnapshot = snapshot;
        if (_terminal != null)
        {
            ApplyFontToTerminal(_terminal, snapshot);
            // 字号变化会改变行高：等控件重新测量后再对齐一次
            Dispatcher.UIThread.Post(AlignGridToBottom, DispatcherPriority.Background);
        }
    }

    // 只设置字体属性（包括主字体与回退字体链）
    private static void ApplyFontToTerminal(TerminalControl terminal, TerminalFontSnapshot snapshot)
    {
        terminal.FontFamilyName = snapshot.PrimaryFontFamily;
        terminal.TerminalFontSize = snapshot.FontSize;

        // 构造回退字体链（0.6.0-preview.2 引入）
        System.Collections.Immutable.ImmutableArray<string>.Builder regularBuilder =
            System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.PrimaryFontFamily))
        {
            regularBuilder.Add(snapshot.PrimaryFontFamily);
        }
        foreach (string fallback in snapshot.FallbackFonts)
        {
            if (!string.IsNullOrWhiteSpace(fallback) && !regularBuilder.Contains(fallback))
            {
                regularBuilder.Add(fallback.Trim());
            }
        }

        terminal.FontFamilies = new RoyalTerminal.Terminal.TerminalFontFamilySettings
        {
            Regular = regularBuilder.ToImmutable()
        };

        // 适配终端行高（保持实际字号与字宽不变，仅依据度量策略适配行高）
        TerminalFontMetricAdapter.AdaptCellHeight(terminal, snapshot.PrimaryFontFamily, snapshot.FontSize);
    }

    [RelayCommand]
    public async Task CopySelectionAsync()
    {
        try
        {
            await Terminal.CopySelectionAsync();
        }
        catch { }
    }

    [RelayCommand]
    public async Task PasteToTerminalAsync()
    {
        try
        {
            await Terminal.PasteAsync();
        }
        catch { }
    }

    [RelayCommand]
    public void SelectAllTerminalText()
    {
        try
        {
            Terminal.SelectAll();
        }
        catch { }
    }

    // 本地清屏：只保留光标所在行并清空回滚缓冲区；不向远端发送任何字节，
    // 避免把 clear 敲进 vim 等全屏程序、也不会冲掉用户正在输入的半行命令
    [RelayCommand]
    public void ClearTerminalScreen()
    {
        Terminal.ClearHistory();
        // 全屏程序（vim / tmux 的备用屏幕）中 ClearHistory 不动主屏；仍要清掉主屏回滚，否则"清屏后还能往上滚"
        Terminal.ClearScrollback();
    }

    // 只清回滚缓冲区，保留当前屏幕内容
    [RelayCommand]
    public void ClearTerminalScrollback()
    {
        Terminal.ClearScrollback();
    }

    private void OnTerminalLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 挂载后 Renderer 建立，幂等补偿目标行高（若尚未应用）
        if (_terminal is { } terminal)
        {
            TerminalFontMetricAdapter.AdaptCellHeight(terminal, _fontSnapshot.PrimaryFontFamily, _fontSnapshot.FontSize);
        }

        AlignGridToBottom();
        TrySyncTerminalSize();
        // 控件挂载后 Renderer 才存在：对最新 CurrentProfile 重放选区 alpha 恢复
        ReapplySelectionOverride();
    }

    // 对最新 CurrentProfile 重放选区 alpha 恢复（Loaded 后、以及重新 ApplyTheme 后需要）。
    // 返回 false 表示 Renderer 尚未就绪；此处记录诊断而非静默。
    public bool ReapplySelectionOverride()
    {
        if (IsDisposed)
        {
            return false;
        }

        if (_themeSink.TryReapplySelectionOverride(CurrentProfile))
        {
            return true;
        }

        _logger.LogDebug(
            "终端选区 alpha 恢复推迟：Renderer 尚未就绪 标题={Title} ProfileId={ProfileId}",
            Title,
            CurrentProfile.Id);
        return false;
    }

    private void OnTerminalSizeChanged(object? sender, Avalonia.Controls.SizeChangedEventArgs e)
    {
        AlignGridToBottom();
        TrySyncTerminalSize();
    }

    // 行列数变化（含字号引起的行高变化）后重新计算贴底余量
    private void OnTerminalGridResized(object? sender, TerminalSizeEventArgs e)
    {
        AlignGridToBottom();
    }

    // 把不足一行的余量放到顶部，使全屏程序的底部状态栏贴住终端底边
    private void AlignGridToBottom()
    {
        if (_terminal?.Renderer is not { } renderer || IsDisposed)
        {
            return;
        }

        double inset = !_hasRemoteOutput && State is ConnectionState.Error or ConnectionState.Disconnected
            ? 0
            : TerminalGridAlignment.TopInset(_terminal.Bounds.Height, renderer.CellHeight);
        if (Math.Abs(_terminal.Padding.Top - inset) > 0.001)
        {
            _terminal.Padding = new Thickness(0, inset, 0, 0);
        }
    }

    // 尝试同步终端尺寸至后台 SSH 会话
    public void TrySyncTerminalSize()
    {
        // 控件尚未创建即无尺寸可同步：不为此惰性创建原生控件
        if (_terminal is { } terminal && terminal.Bounds.Width > 0 && terminal.Bounds.Height > 0)
        {
            _endpoint?.SetSize((int)terminal.Bounds.Width, (int)terminal.Bounds.Height);
        }
    }
}
