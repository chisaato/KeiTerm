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
using CommunityToolkit.Mvvm.Messaging;
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

// 连接标签：会话生命周期（挂载 / 连接 / 断开 / 重连）与侧栏中的文件视图；标签头见 .Header.cs，终端控件见 .Control.cs
public partial class TerminalTabViewModel : ViewModelBase, IAsyncDisposable
{
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
        UpdateTitle();
        AlignGridToBottom();
    }
#pragma warning restore CS0618

    [ObservableProperty]
    private string? _statusMessage;

    // 当前标签是否选中（标签条样式绑定用）
    [ObservableProperty]
    private bool _isSelected;

    // 文件管理侧栏显隐控制
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileManagerToggleToolTip))]
    private bool _isFileManagerVisible;

    public string FileManagerToggleToolTip => Strings.Get(IsFileManagerVisible ? "Terminal.FileManager.Hide" : "Terminal.FileManager.Show");

    // 会话能力驱动的文件管理入口可用性（由 ITerminalSession.Capabilities 决定）
    [ObservableProperty]
    private bool _hasFileSystem;

    // 串口操作支持
    public bool IsSerialSession => Config?.Protocol == SessionProtocols.Serial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRts))]
    [NotifyPropertyChangedFor(nameof(RtsToolTip))]
    private bool _isHardwareFlowControl;

    [ObservableProperty]
    private bool? _isDtrEnabled;

    [ObservableProperty]
    private bool? _isRtsEnabled;

    public bool CanSendSerialControl => IsSerialSession && State == ConnectionState.Connected;
    public bool CanToggleRts => CanSendSerialControl && _sessionAttachment?.Session is ISerialControl { CanSetRts: true };
    public string RtsToolTip => IsHardwareFlowControl ? Strings.Get("SessionEdit.Serial.RtsConflict") : Strings.Get("Terminal.Serial.RtsTip");

    // 文件管理侧栏停靠在左侧（false 为右侧，true 为左侧）
    [ObservableProperty]
    private bool _isFileManagerOnLeft;

    [ObservableProperty]
    private RemoteFileManagerViewModel? _fileManager;

    private ITerminalSession? _session;
    private SessionAttachment? _sessionAttachment;

    // 订阅与排队工作归属于一次挂载；即使同一会话对象重新挂载也不可复用旧回调。
    private sealed class SessionAttachment(ITerminalSession session)
    {
        public ITerminalSession Session { get; } = session;
        public Action<Exception?> DisconnectedHandler { get; set; } = null!;
        public Action<byte[]> OutputHandler { get; set; } = null!;
        public int Disconnected;
        public int ActivityPending;
        public bool DisconnectHandled;
    }

    private bool IsCurrentAttachment(SessionAttachment attachment)
        => !IsDisposed && ReferenceEquals(_sessionAttachment, attachment);

    private void UnsubscribeSessionEvents()
    {
        var attachment = _sessionAttachment;
        _sessionAttachment = null;
        if (attachment == null) return;
        attachment.Session.Disconnected -= attachment.DisconnectedHandler;
        attachment.Session.OutputReceived -= attachment.OutputHandler;
    }
    private CancellationTokenSource? _reconnectWait;
    private int _reconnectAttempt;
    private bool _userDisconnect;

    public bool RequiresInteractiveAuthentication { get; internal set; }

    // 认证弹窗打开期间状态仍可能是 Error，防止连续回车并发发起重连。
    internal bool IsReconnectPending { get; set; }

    // 意外断开时由主窗口接上。开关关闭时保持原有「干净断开即关标签」行为。
    public Func<bool>? AutoReconnectEnabled { get; set; }
    public Func<TerminalTabViewModel, int, CancellationToken, Task>? UnexpectedDisconnectAsync { get; set; }

    public void CancelReconnect()
    {
        _reconnectWait?.Cancel();
    }
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
            new TerminalFontSnapshot(fontFamily, [], fontSize, true),
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
        int scrollbackLines = 5000,
        bool mouseReportingEnabled = true)
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
        _mouseReportingEnabled = mouseReportingEnabled;

        _terminalFactory = terminalFactory ?? CreateHookedTerminal;
        // 生产默认绑定真实控件（惰性）；测试可注入无控件实现以验证纯状态
        _themeSink = themeSink ?? new TerminalControlThemeSink(() => Terminal);

        RegisterSidebarSizingMessages();
        WeakReferenceMessenger.Default.Register<TerminalTabViewModel, TerminalMouseReportingChangedMessage>(this, static (recipient, message) =>
        {
            void Apply()
            {
                recipient._mouseReportingEnabled = message.Enabled;
                if (recipient._terminal is KeiTerminalControl keiTerminal)
                {
                    keiTerminal.IsMouseReportingEnabled = message.Enabled;
                }
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        });
        WeakReferenceMessenger.Default.Register<TerminalTabViewModel, FileManagerDockChangedMessage>(this, static (recipient, message) =>
        {
            void Apply()
            {
                recipient.IsFileManagerOnLeft = message.IsOnLeft;
                if (recipient.FileManager != null)
                {
                    recipient.FileManager.IsOnLeft = message.IsOnLeft;
                }
                recipient.TrySyncTerminalSize();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
            }
        });
    }


    [RelayCommand]
    private Task Disconnect() => DisconnectAsync();

    // 挂载会话与端点（必须在 UI 线程调用）；重试时替换旧会话/端点
    public void AttachSession(ITerminalSession session)
    {
        if (IsDisposed)
        {
            _ = session.DisposeAsync();
            return;
        }

        // 结旧：仅释放端点订阅，不断开状态机（重试场景由调用方先 Detach）
        UnsubscribeSessionEvents();
        _endpoint?.Dispose();
        _endpoint = null;
        _terminal?.DetachEndpoint();

        _session = session;
        _userDisconnect = false;
        HasFileSystem = session.Capabilities.HasFileSystem;
        _connectionDropAction = null;
        ResetDirectoryFollow();
        if (TerminalIfOnUiThread is { } terminal)
        {
            _endpoint = new TerminalSessionEndpoint(session, terminal);
            terminal.AttachEndpoint(_endpoint);
        }
        var attachment = new SessionAttachment(session);
        attachment.DisconnectedHandler = error => OnSessionDisconnected(attachment, error);
        attachment.OutputHandler = data => OnSessionOutput(attachment, data);
        _sessionAttachment = attachment;
        IsHardwareFlowControl = session is ISerialControl { CanSetRts: false };
        IsDtrEnabled = null;
        IsRtsEnabled = null;
        OnPropertyChanged(nameof(CanToggleRts));
        session.Disconnected += attachment.DisconnectedHandler;
        session.OutputReceived += attachment.OutputHandler;

        State = ConnectionState.Connecting;
        StatusMessage = null;
        _logger.LogInformation("终端标签挂载会话 标题={Title} SessionId={SessionId}", Title, session.SessionId);
        TrySyncTerminalSize();
    }

    // 连接成功（UI 线程）
    public void MarkConnected()
    {
        // 不以假会话的 IsConnected 初值推断失败；已观察到终止事件则绝不复活本世代。
        if (IsDisposed || _sessionAttachment is { } attachment && Volatile.Read(ref attachment.Disconnected) != 0) return;
        State = ConnectionState.Connected;
        StatusMessage = null;
        _reconnectAttempt = 0;
        _userDisconnect = false;
        ScheduleDirectoryFollow();
        OnPropertyChanged(nameof(CanSendSerialControl));
        OnPropertyChanged(nameof(CanToggleRts));
        SendBreakCommand.NotifyCanExecuteChanged();
        ToggleDtrCommand.NotifyCanExecuteChanged();
        ToggleRtsCommand.NotifyCanExecuteChanged();
        _logger.LogInformation("终端标签已连接 标题={Title}", Title);
        TrySyncTerminalSize();
        if (IsSelected)
        {
            _terminal?.Focus();
        }
    }

    // 会话已连上，但端口转发有条目没起来。保持 Connected，并在终端与状态写入可见警告。
    public void ReportWarning(string message)
    {
        if (State != ConnectionState.Error)
        {
            State = ConnectionState.Connected;
        }

        StatusMessage = message;
        TerminalIfOnUiThread?.WriteOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[33m⚠ {message}\x1b[0m\r\n"));
        _logger.LogWarning("终端标签提示 标题={Title} 原因={Reason}", Title, message);
    }

    // 本地状态行，不发给远端
    public void WriteLocalStatus(string message)
    {
        StatusMessage = message;
        TerminalIfOnUiThread?.WriteOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[90m{message}\x1b[0m\r\n"));
    }

    // 连接失败：状态置错并在终端输出红字（UI 线程）
    public void ReportError(string message)
    {
        State = ConnectionState.Error;
        StatusMessage = message;
        // 尚无远端内容时错误页从第一行开始；已有会话历史则继续追加，避免覆盖用户输出。
        string position = _hasRemoteOutput ? "\r\n" : "\x1b[2J\x1b[H";
        TerminalIfOnUiThread?.WriteOutput(Encoding.UTF8.GetBytes(
            $"{position}\x1b[31m{Strings.Get("Main.Tab.ConnectErrorPrefix")}{message}\x1b[0m\r\n"
            + $"\x1b[90m{Strings.Get("Main.Tab.PressEnterToReconnect")}\x1b[0m\r\n"));
        if (IsSelected) TerminalIfOnUiThread?.Focus();
        _logger.LogWarning("终端标签连接失败 标题={Title} 原因={Reason}", Title, message);
    }

    // 解除当前会话/端点（不改标签状态与释放标志），供认证失败重试前清理
    public async Task DetachSessionAsync(ITerminalSession? expectedSession = null)
    {
        if (expectedSession != null && !ReferenceEquals(_session, expectedSession))
        {
            await Task.Run(async () => await expectedSession.DisposeAsync());
            return;
        }
        // 在任何 await 前使已排队回调失效。
        UnsubscribeSessionEvents();
        IsHardwareFlowControl = false;
        IsDtrEnabled = null;
        IsRtsEnabled = null;
        OnPropertyChanged(nameof(CanToggleRts));
        _fileManagerInitializationCancellation?.Cancel();
        ResetDirectoryFollow();
        await DrainDirectoryFollowAsync();
        await DrainTerminalFileDropAsync();
        _endpoint?.Dispose();
        _terminal?.DetachEndpoint();
        _endpoint = null;

        var session = _session;
        _session = null;
        HasFileSystem = false;
        if (session != null)
        {
            // SSH 断开可能同步等待网络；先在 UI 线程卸下端点，再在后台释放连接。
            await Task.Run(async () => await session.DisposeAsync());
            _logger.LogInformation("终端标签释放会话 标题={Title} SessionId={SessionId}", Title, session.SessionId);
        }
    }

    // 会话断开事件：可能来自后台读取线程，统一 Post 回 UI 线程更新状态
    private void OnSessionDisconnected(SessionAttachment attachment, Exception? ex)
    {
        Interlocked.Exchange(ref attachment.Disconnected, 1);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentAttachment(attachment) || attachment.DisconnectHandled || _userDisconnect)
            {
                return;
            }
            attachment.DisconnectHandled = true;

            State = ex != null ? ConnectionState.Error : ConnectionState.Disconnected;
            _dropCancellation?.Cancel();
            ResetDirectoryFollow();
            StatusMessage = ex?.Message ?? Strings.Get("Main.Tab.ConnectionDropped");
            if (ex != null)
            {
                _logger.LogWarning(ex, "终端标签会话断开（异常） 标题={Title}", Title);
            }
            else
            {
                _logger.LogInformation("终端标签会话断开 标题={Title}", Title);
            }

            if (AutoReconnectEnabled?.Invoke() == true && UnexpectedDisconnectAsync != null)
            {
                _reconnectAttempt++;
                _reconnectWait?.Cancel();
                _reconnectWait = new CancellationTokenSource();
                _ = UnexpectedDisconnectAsync(this, _reconnectAttempt, _reconnectWait.Token);
                return;
            }

            if (ex == null)
            {
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
        if (!HasFileSystem)
        {
            IsFileManagerVisible = false;
            return;
        }

        if (IsFileManagerMaximized)
        {
            if (IsFileManagerVisible)
            {
                IsShellVisible = true;
                IsFileManagerVisible = false;
            }
            else ShowFileManagerFullScreen();
            return;
        }
        IsFileManagerVisible = !IsFileManagerVisible;
        Dispatcher.UIThread.Post(() => TrySyncTerminalSize(), DispatcherPriority.Render);
    }

    [RelayCommand(CanExecute = nameof(CanSendSerialControl))]
    public async Task SendBreakAsync()
    {
        if (_sessionAttachment is not { Session: ISerialControl serial } attachment) return;
        try
        {
            await serial.SetBreakStateAsync(true);
            await Task.Delay(250);
        }
        catch (Exception ex) { ReportSerialControlFailure(attachment, "Break", ex); }
        finally
        {
            // 无论失败、关闭还是世代更替，只向捕获的原会话尽力释放 Break。
            try { await serial.SetBreakStateAsync(false); }
            catch (Exception ex) { ReportSerialControlFailure(attachment, "Break 释放", ex); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendSerialControl))]
    public async Task ToggleDtrAsync()
    {
        if (_sessionAttachment is not { Session: ISerialControl serial } attachment) return;
        bool next = IsDtrEnabled != true;
        try
        {
            await serial.SetDtrAsync(next);
            if (IsCurrentAttachment(attachment) && Volatile.Read(ref attachment.Disconnected) == 0) IsDtrEnabled = next;
        }
        catch (Exception ex) { ReportSerialControlFailure(attachment, "DTR", ex); }
    }

    [RelayCommand(CanExecute = nameof(CanToggleRts))]
    public async Task ToggleRtsAsync()
    {
        if (_sessionAttachment is not { Session: ISerialControl { CanSetRts: true } serial } attachment) return;
        bool next = IsRtsEnabled != true;
        try
        {
            await serial.SetRtsAsync(next);
            if (IsCurrentAttachment(attachment) && Volatile.Read(ref attachment.Disconnected) == 0) IsRtsEnabled = next;
        }
        catch (Exception ex) { ReportSerialControlFailure(attachment, "RTS", ex); }
    }

    private void ReportSerialControlFailure(SessionAttachment attachment, string operation, Exception error)
    {
        _logger.LogWarning(error, "串口可选控制失败 操作={Operation}", operation);
        if (IsCurrentAttachment(attachment) && Volatile.Read(ref attachment.Disconnected) == 0)
        {
            string message = Strings.Format("Terminal.Serial.ControlFailedFormat", operation, error.Message);
            StatusMessage = message;
            TerminalIfOnUiThread?.WriteOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[33m⚠ {message}\x1b[0m\r\n"));
        }
    }

    [RelayCommand]
    public void ToggleDockPosition()
    {
        SetDockPosition(!IsFileManagerOnLeft);
    }

    [RelayCommand]
    public void MoveFileManagerToLeft()
    {
        SetDockPosition(true);
    }

    [RelayCommand]
    public void MoveFileManagerToRight()
    {
        SetDockPosition(false);
    }

    public void SetDockPosition(bool onLeft)
    {
        if (IsFileManagerOnLeft == onLeft && FileManager?.IsOnLeft == onLeft)
        {
            return;
        }

        IsFileManagerOnLeft = onLeft;
        if (FileManager != null)
        {
            FileManager.IsOnLeft = onLeft;
        }
        Dispatcher.UIThread.Post(() => TrySyncTerminalSize(), DispatcherPriority.Render);
    }

    private CancellationTokenSource? _connectionAttempt;

    public CancellationToken BeginConnectionAttempt()
    {
        _connectionAttempt?.Cancel();
        _connectionAttempt?.Dispose();
        _connectionAttempt = new CancellationTokenSource();
        if (IsDisposed) _connectionAttempt.Cancel();
        return _connectionAttempt.Token;
    }

    // 主动断开：只卸下会话与文件视图，标签与终端内容保留，可原地重连
    public async Task DisconnectAsync()
    {
        _connectionAttempt?.Cancel();
        IsShellVisible = true;
        _userDisconnect = true;
        CancelDirectoryFollow();
        CancelReconnect();
        await DetachSessionAsync();
        await CloseFileManagerAsync();
        State = ConnectionState.Disconnected;
        StatusMessage = Strings.Get("Main.Tab.Disconnected");
        _logger.LogInformation("终端标签主动断开 标题={Title}", Title);
    }

    // 原地重连前的清理：卸下旧会话与文件视图，终端内保留历史并打印分隔提示
    public async Task PrepareReconnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IsShellVisible = true;
        await DetachSessionAsync();
        // 旧会话的网络释放可能较慢；被取消的自动重连不能继续复位后来建立的新会话。
        ct.ThrowIfCancellationRequested();
        if (IsDisposed) return;
        await CloseFileManagerAsync();
        ct.ThrowIfCancellationRequested();
        if (IsDisposed) return;
        RemoteTitle = null;
        State = ConnectionState.Connecting;
        StatusMessage = null;
        TerminalIfOnUiThread?.WriteOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[90m{Strings.Get("Main.Tab.Reconnecting")}\x1b[0m\r\n"));
    }

    public async ValueTask DisposeAsync()
    {
        // 重入保护：断开与关闭标签可能并发触发，只允许首次调用进入清理
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        _connectionAttempt?.Cancel();
        // 控件可能从未创建（纯状态使用），仅在创建后解绑
        if (_terminal != null)
        {
            _terminal.RemoveHandler(Avalonia.Input.InputElement.KeyDownEvent, OnTerminalKeyDown);
            _terminal.Loaded -= OnTerminalLoaded;
            _terminal.SizeChanged -= OnTerminalSizeChanged;
            _terminal.TerminalResized -= OnTerminalGridResized;
            _terminal.ShellIntegrationEventReceived -= OnTerminalShellIntegrationEvent;
        }

        WeakReferenceMessenger.Default.Unregister<TerminalMouseReportingChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<FileManagerDockChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<FileManagerSidebarRatioChangedMessage>(this);
        if (_boundSessionNode != null)
        {
            _boundSessionNode.PropertyChanged -= OnBoundSessionNodePropertyChanged;
            _boundSessionNode = null;
        }
        _userDisconnect = true;
        CancelReconnect();
        await DrainDirectoryFollowAsync();
        await DetachSessionAsync();
        await CloseFileManagerAsync();
        _logger.LogInformation("终端标签已释放 标题={Title}", Title);
    }
}
