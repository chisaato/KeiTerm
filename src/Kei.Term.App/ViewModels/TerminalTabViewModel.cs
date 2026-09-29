namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    Error
}

// 保持平滑兼容保留过时别名
[Obsolete("Use ConnectionState instead.")]
public enum TabStatus
{
    Connecting,
    Connected,
    Disconnected,
    Error
}

// 配色应用抽象：默认走真实终端控件，纯状态测试可注入不触碰原生控件的实现
public interface ITerminalThemeSink
{
    void Apply(TerminalProfile profile);

    // Loaded 后 / 重新 ApplyTheme 后恢复被上游强制改写的选区 alpha；返回 false 表示 Renderer 尚未就绪
    bool TryReapplySelectionOverride(TerminalProfile profile);
}

// 默认实现：把方案注入真实 TerminalControl（不启动任何会话）。
// 通过访问器延迟获取控件：测试不访问 Terminal 时不会创建原生控件。
internal sealed class TerminalControlThemeSink : ITerminalThemeSink
{
    private readonly Func<TerminalControl> _terminalAccessor;

    public TerminalControlThemeSink(Func<TerminalControl> terminalAccessor) => _terminalAccessor = terminalAccessor;

    public void Apply(TerminalProfile profile) => TerminalThemeAdapter.Apply(_terminalAccessor(), profile);

    public bool TryReapplySelectionOverride(TerminalProfile profile)
    {
        var terminal = _terminalAccessor();
        if (!TerminalThemeAdapter.TryReapplySelectionOverride(terminal, profile))
        {
            return false;
        }

        // 恢复成功后再请求重绘，避免闪一帧 0x80
        terminal.InvalidateTerminal();
        return true;
    }
}

// 标签右键菜单中需要主窗口处理的动作（涉及其他标签或连接编排）
public enum TabAction
{
    Reconnect,
    Clone,
    Rename,
    CloseOthers,
    CloseToRight
}

public partial class TerminalTabViewModel : ViewModelBase, IAsyncDisposable
{
    // 远端标题最长保留字符数：防止异常程序刷出超长标题撑爆标签条与提示框
    private const int MaxRemoteTitleLength = 256;

    // 请求关闭此标签页（如会话正常退出时触发）
    public event Action<TerminalTabViewModel>? CloseRequested;

    // 标签右键菜单动作，由主窗口 VM 订阅处理
    public event Action<TerminalTabViewModel, TabAction>? ActionRequested;

    // 显示在标签上的标题：跟随远端时为远端标题（未设置则回退标签名），否则为标签名
    [ObservableProperty]
    private string _title = Strings.Get("Main.Tab.DefaultTitle");

    // 标签名：默认取会话名，可在标签上重命名（仅本标签生效，不改会话）
    [ObservableProperty]
    private string _tabName = Strings.Get("Main.Tab.DefaultTitle");

    // 远端程序通过 OSC 0/2 设置的最近一次标题
    [ObservableProperty]
    private string? _remoteTitle;

    // 本标签是否用远端标题；初值来自会话/全局设置，可在标签右键菜单临时切换
    [ObservableProperty]
    private bool _followRemoteTitle;

    // 后台标签收到新输出或响铃：标签上显示活动标记，切到该标签时清除
    [ObservableProperty]
    private bool _hasActivity;

    // 标签悬停提示：会话、目标地址、状态
    [ObservableProperty]
    private string _toolTip = string.Empty;

    // 发起本标签连接时的解析配置（重连 / 克隆 / 提示信息用）；未经连接编排创建的标签为 null
    public ResolvedSessionConfig? Config { get; private set; }

    partial void OnTabNameChanged(string value) => UpdateTitle();

    partial void OnRemoteTitleChanged(string? value) => UpdateTitle();

    partial void OnFollowRemoteTitleChanged(bool value) => UpdateTitle();

    partial void OnStatusMessageChanged(string? value) => UpdateToolTip();

    partial void OnTitleChanged(string value) => UpdateToolTip();

    private void UpdateTitle()
        => Title = FollowRemoteTitle && !string.IsNullOrWhiteSpace(RemoteTitle) ? RemoteTitle! : TabName;

    private void UpdateToolTip()
    {
        var lines = new List<string> { Title };
        if (!string.Equals(Title, TabName, StringComparison.Ordinal))
        {
            lines.Add(Strings.Format("Main.Tab.ToolTip.Session", TabName));
        }

        if (Config is { } config)
        {
            lines.Add($"{config.Username}@{config.Host}:{config.Port}");
        }

        if (!string.IsNullOrWhiteSpace(StatusMessage))
        {
            lines.Add(StatusMessage!);
        }

        ToolTip = string.Join(Environment.NewLine, lines);
    }

    // 记录连接配置并按其行为设置初始化标题跟随（连接编排开标签时调用）
    public void BindConfig(ResolvedSessionConfig config)
    {
        // 标题跟随只在首次绑定时取会话/全局设置；重连沿用用户在本标签上的选择（含重命名后的停止跟随）
        if (Config == null)
        {
            FollowRemoteTitle = config.FollowRemoteTitle;
        }

        Config = config;
        UpdateToolTip();
    }

    // 远端标题：去掉控制字符并截断；空标题表示远端清除了标题
    public static string? SanitizeRemoteTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, MaxRemoteTitleLength));
        foreach (char c in raw)
        {
            if (builder.Length >= MaxRemoteTitleLength)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public void ApplyRemoteTitle(string? raw) => RemoteTitle = SanitizeRemoteTitle(raw);

    // 重命名：用户给定的名字优先于远端标题，因此同时停止跟随
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        TabName = name.Trim();
        FollowRemoteTitle = false;
    }

    [RelayCommand]
    private void ToggleFollowRemoteTitle() => FollowRemoteTitle = !FollowRemoteTitle;

    [RelayCommand]
    private void RequestReconnect() => ActionRequested?.Invoke(this, TabAction.Reconnect);

    [RelayCommand]
    private void RequestClone() => ActionRequested?.Invoke(this, TabAction.Clone);

    [RelayCommand]
    private void RequestRename() => ActionRequested?.Invoke(this, TabAction.Rename);

    [RelayCommand]
    private void RequestCloseOthers() => ActionRequested?.Invoke(this, TabAction.CloseOthers);

    [RelayCommand]
    private void RequestCloseToRight() => ActionRequested?.Invoke(this, TabAction.CloseToRight);

    [RelayCommand]
    private void RequestClose() => CloseRequested?.Invoke(this);

    [RelayCommand]
    private Task Disconnect() => DisconnectAsync();

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            HasActivity = false;
        }
    }

    // 后台线程上的输出回调：只在状态需要翻转时才投递到 UI 线程，避免每个数据块都排队
    private void OnSessionOutput(byte[] data)
    {
        if (_activityPending || IsSelected || HasActivity)
        {
            return;
        }

        _activityPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _activityPending = false;
            if (!IsSelected)
            {
                HasActivity = true;
            }
        });
    }

    private volatile bool _activityPending;

    [ObservableProperty]
    private ConnectionState _state = ConnectionState.Connecting;

    // 向后兼容旧绑定与调用点
#pragma warning disable CS0618
    [Obsolete("Use State instead.")]
    public ConnectionState Status
    {
        get => State;
        set => State = value;
    }

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(Status));
    }
#pragma warning restore CS0618

    [ObservableProperty]
    private string? _statusMessage;

    // 当前标签是否选中（标签条样式绑定用）
    [ObservableProperty]
    private bool _isSelected;

    // 文件管理侧栏显隐控制
    [ObservableProperty]
    private bool _isFileManagerVisible;

    // 文件管理侧栏停靠在左侧（false 为右侧，true 为左侧）
    [ObservableProperty]
    private bool _isFileManagerOnLeft;

    [ObservableProperty]
    private RemoteFileManagerViewModel? _fileManager;

    // 终端区底色：网格贴底后顶部余量与滚动条轨道用配色背景填充，避免露出窗口底色
    [ObservableProperty]
    private IBrush _terminalBackground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.Black);

    // 惰性创建：测试只验证状态/主题注入时不会构造原生 TerminalControl
    private readonly Func<TerminalControl> _terminalFactory;
    private TerminalControl? _terminal;
    private TerminalFontSnapshot _fontSnapshot;
    private readonly int _scrollbackLines;

    public TerminalControl Terminal
    {
        get
        {
            if (_terminal == null)
            {
                var terminal = _terminalFactory();
                terminal.ScrollbackLimit = _scrollbackLines;
                ApplyFontToTerminal(terminal, _fontSnapshot);
                terminal.Loaded += OnTerminalLoaded;
                terminal.SizeChanged += OnTerminalSizeChanged;
                terminal.TerminalResized += OnTerminalGridResized;
                _terminal = terminal;
            }

            return _terminal;
        }
    }

    private ITerminalSession? _session;
    private TerminalSessionEndpoint? _endpoint;
    private readonly ILogger _logger;
    private readonly ITerminalThemeSink _themeSink;

    // 释放状态：0 = 未释放，1 = 释放中/已释放（防止 DisposeAsync 重入）
    private int _disposeState;

    // 标签是否已释放（后台连接管线据此判断是否需要中止）
    public bool IsDisposed => _disposeState != 0;

    // 会话级显式配色 ID；null/空表示继承全局默认
    public string? ExplicitProfileId { get; }

    // 当前有效配色 ID（继承时随全局切换更新；显式时保持显式 ID）
    public string EffectiveProfileId { get; private set; }

    // 当前生效方案（供刷新断言与调试）
    public TerminalProfile CurrentProfile { get; private set; }

    // 字体与字号由设置传入（新标签生效，不要求旧标签热更新）
    public TerminalTabViewModel(string title, string fontFamily, double fontSize, ILogger? logger = null)
        : this(
            title,
            new TerminalFontSnapshot(fontFamily, Array.Empty<string>(), fontSize, true),
            BuiltInPresets.GetDefaultTerminalProfile(),
            explicitProfileId: null,
            logger)
    {
    }

    // 带配色方案的新构造：只记录状态；Terminal 惰性创建，注入由 ApplyTerminalProfile / ApplyFontSnapshot 完成
    public TerminalTabViewModel(
        string title,
        TerminalFontSnapshot font,
        TerminalProfile profile,
        string? explicitProfileId,
        ILogger? logger = null,
        Func<TerminalControl>? terminalFactory = null,
        ITerminalThemeSink? themeSink = null,
        int scrollbackLines = 5000)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(profile);

        TabName = title;
        Title = title;
        _logger = logger ?? NullLogger.Instance;
        ExplicitProfileId = explicitProfileId;
        CurrentProfile = profile;
        EffectiveProfileId = profile.Id;
        TerminalBackground = ParseBrush(profile.Background);
        _fontSnapshot = font;
        _scrollbackLines = Math.Max(0, scrollbackLines);

        _terminalFactory = terminalFactory ?? CreateHookedTerminal;
        // 生产默认绑定真实控件（惰性）；测试可注入无控件实现以验证纯状态
        _themeSink = themeSink ?? new TerminalControlThemeSink(() => Terminal);
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

    private void OnRemoteBell()
    {
        if (!IsSelected)
        {
            HasActivity = true;
        }
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

        double inset = TerminalGridAlignment.TopInset(_terminal.Bounds.Height, renderer.CellHeight);
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

    // 挂载会话与端点（必须在 UI 线程调用）；重试时替换旧会话/端点
    public void AttachSession(ITerminalSession session)
    {
        if (IsDisposed)
        {
            _ = session.DisposeAsync();
            return;
        }

        // 结旧：仅释放端点订阅，不断开状态机（重试场景由调用方先 Detach）
        _endpoint?.Dispose();
        Terminal.DetachEndpoint();

        _session = session;
        _endpoint = new TerminalSessionEndpoint(session, Terminal);
        Terminal.AttachEndpoint(_endpoint);
        session.Disconnected += OnSessionDisconnected;
        session.OutputReceived += OnSessionOutput;

        State = ConnectionState.Connecting;
        StatusMessage = null;
        _logger.LogInformation("终端标签挂载会话 标题={Title} SessionId={SessionId}", Title, session.SessionId);
        TrySyncTerminalSize();
    }

    // 连接成功（UI 线程）
    public void MarkConnected()
    {
        State = ConnectionState.Connected;
        StatusMessage = null;
        _logger.LogInformation("终端标签已连接 标题={Title}", Title);
        TrySyncTerminalSize();
        if (IsSelected)
        {
            _terminal?.Focus();
        }
    }

    // 连接失败：状态置错并在终端输出红字（UI 线程）
    public void ReportError(string message)
    {
        State = ConnectionState.Error;
        StatusMessage = message;
        Terminal.WriteOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[31m{Strings.Get("Main.Tab.ConnectErrorPrefix")}{message}\x1b[0m\r\n"));
        _logger.LogWarning("终端标签连接失败 标题={Title} 原因={Reason}", Title, message);
    }

    // 解除当前会话/端点（不改标签状态与释放标志），供认证失败重试前清理
    public async Task DetachSessionAsync()
    {
        _endpoint?.Dispose();
        _terminal?.DetachEndpoint();
        _endpoint = null;

        var session = _session;
        _session = null;
        if (session != null)
        {
            session.Disconnected -= OnSessionDisconnected;
            session.OutputReceived -= OnSessionOutput;
            await session.DisposeAsync();
            _logger.LogInformation("终端标签释放会话 标题={Title} SessionId={SessionId}", Title, session.SessionId);
        }
    }

    // 会话断开事件：可能来自后台读取线程，统一 Post 回 UI 线程更新状态
    private void OnSessionDisconnected(Exception? ex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            State = ex != null ? ConnectionState.Error : ConnectionState.Disconnected;
            StatusMessage = ex?.Message ?? Strings.Get("Main.Tab.ConnectionDropped");
            if (ex != null)
            {
                _logger.LogWarning(ex, "终端标签会话断开（异常） 标题={Title}", Title);
            }
            else
            {
                _logger.LogInformation("终端标签会话断开 标题={Title}", Title);
                CloseRequested?.Invoke(this);
            }
        });
    }

    public async Task SendCommandAsync(string text)
    {
        if (_session != null && _session.IsConnected)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\r");
            await _session.SendInputAsync(bytes);
        }
    }

    [RelayCommand]
    public void ToggleFileManager()
    {
        IsFileManagerVisible = !IsFileManagerVisible;
        Dispatcher.UIThread.Post(() => TrySyncTerminalSize(), DispatcherPriority.Render);
    }

    public async Task InitializeFileManagerAsync(
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService = null,
        IExternalEditorRepository? editorRepo = null,
        ILogger? logger = null)
    {
        if (_session == null) return;
        var vm = new RemoteFileManagerViewModel(_session.SessionId, fileSystem, fileTracker, settingsService, editorRepo, logger ?? _logger);
        vm.CloseRequested += () =>
        {
            IsFileManagerVisible = false;
            Dispatcher.UIThread.Post(() => TrySyncTerminalSize(), DispatcherPriority.Render);
        };
        vm.ToggleDockPositionRequested += () =>
        {
            ToggleDockPosition();
        };

        if (settingsService != null)
        {
            IsFileManagerOnLeft = settingsService.Current.FileTransfer.IsFileManagerOnLeft;
            vm.IsOnLeft = IsFileManagerOnLeft;
        }

        await vm.InitializeAsync();
        FileManager = vm;
    }

    [RelayCommand]
    public void ToggleDockPosition()
    {
        IsFileManagerOnLeft = !IsFileManagerOnLeft;
        if (FileManager != null)
        {
            FileManager.IsOnLeft = IsFileManagerOnLeft;
        }
        Dispatcher.UIThread.Post(() => TrySyncTerminalSize(), DispatcherPriority.Render);
    }

    // 主动断开：只卸下会话与文件侧栏，标签与终端内容保留，可原地重连
    public async Task DisconnectAsync()
    {
        await DetachSessionAsync();
        await CloseFileManagerAsync();
        State = ConnectionState.Disconnected;
        StatusMessage = Strings.Get("Main.Tab.Disconnected");
        _logger.LogInformation("终端标签主动断开 标题={Title}", Title);
    }

    // 原地重连前的清理：卸下旧会话与文件侧栏，终端内保留历史并打印分隔提示
    public async Task PrepareReconnectAsync()
    {
        await DetachSessionAsync();
        await CloseFileManagerAsync();
        RemoteTitle = null;
        State = ConnectionState.Connecting;
        StatusMessage = null;
        Terminal.WriteOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[90m{Strings.Get("Main.Tab.Reconnecting")}\x1b[0m\r\n"));
    }

    // 文件侧栏持有独立的 SFTP/SCP 通道（专用模式下是第二条 SSH 连接），标签断开或关闭时必须释放
    // releaseOnly：关闭标签时在后台线程释放，不再改动已解绑界面的可观察属性
    private async Task CloseFileManagerAsync(bool releaseOnly = false)
    {
        RemoteFileManagerViewModel? fileManager = FileManager;
        if (!releaseOnly)
        {
            IsFileManagerVisible = false;
            FileManager = null;
        }

        if (fileManager == null)
        {
            return;
        }

        try
        {
            await fileManager.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放文件侧栏失败 标题={Title}", Title);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 重入保护：断开与关闭标签可能并发触发，只允许首次调用进入清理
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        // 控件可能从未创建（纯状态使用），仅在创建后解绑
        if (_terminal != null)
        {
            _terminal.Loaded -= OnTerminalLoaded;
            _terminal.SizeChanged -= OnTerminalSizeChanged;
            _terminal.TerminalResized -= OnTerminalGridResized;
        }

        await DetachSessionAsync();
        await CloseFileManagerAsync(releaseOnly: true);
        _logger.LogInformation("终端标签已释放 标题={Title}", Title);
    }
}
