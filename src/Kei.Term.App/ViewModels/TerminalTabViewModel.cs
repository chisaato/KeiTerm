namespace Kei.Term.App.ViewModels;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoyalTerminal.Avalonia.Controls;
using Kei.Term.App.Helpers;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Abstractions;
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

public partial class TerminalTabViewModel : ViewModelBase, IAsyncDisposable
{
    // 请求关闭此标签页（如会话正常退出时触发）
    public event Action<TerminalTabViewModel>? CloseRequested;

    [ObservableProperty]
    private string _title = Strings.Get("Main.Tab.DefaultTitle");

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

    public TerminalControl Terminal { get; }

    private ISshSession? _session;
    private SshTerminalEndpoint? _endpoint;
    private readonly ILogger _logger;

    // 释放状态：0 = 未释放，1 = 释放中/已释放（防止 DisposeAsync 重入）
    private int _disposeState;

    // 标签是否已释放（后台连接管线据此判断是否需要中止）
    public bool IsDisposed => _disposeState != 0;

    // 字体与字号由设置传入（新标签生效，不要求旧标签热更新）
    public TerminalTabViewModel(string title, string fontFamily, double fontSize, ILogger? logger = null)
    {
        Title = title;
        _logger = logger ?? NullLogger.Instance;
        Terminal = new TerminalControl
        {
            // TerminalControl.FontFamilyName 仅接受单一字体族名，先归一化再赋值
            FontFamilyName = NormalizeFontFamilyName(fontFamily),
            TerminalFontSize = fontSize
        };

        Terminal.Loaded += OnTerminalLoaded;
        Terminal.SizeChanged += OnTerminalSizeChanged;
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

    [RelayCommand]
    public async Task ClearTerminalScreenAsync()
    {
        if (_session != null && _session.IsConnected)
        {
            await _session.SendInputAsync(Encoding.UTF8.GetBytes("clear\r"));
        }
    }

    private void OnTerminalLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        TrySyncTerminalSize();
    }

    private void OnTerminalSizeChanged(object? sender, Avalonia.Controls.SizeChangedEventArgs e)
    {
        TrySyncTerminalSize();
    }

    // 尝试同步终端尺寸至后台 SSH 会话
    public void TrySyncTerminalSize()
    {
        if (Terminal.Bounds.Width > 0 && Terminal.Bounds.Height > 0)
        {
            _endpoint?.SetSize((int)Terminal.Bounds.Width, (int)Terminal.Bounds.Height);
        }
    }

    // 归一化字体族名：兼容旧配置里的逗号分隔候选列表，取第一段首选名；空值回退默认等宽字体
    private static string NormalizeFontFamilyName(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            return "Noto Sans Mono";
        }

        var first = fontFamily.Split(',')[0].Trim();
        return first.Length > 0 ? first : "Noto Sans Mono";
    }

    // 挂载会话与端点（必须在 UI 线程调用）；重试时替换旧会话/端点
    public void AttachSession(ISshSession session)
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
        _endpoint = new SshTerminalEndpoint(session, Terminal);
        Terminal.AttachEndpoint(_endpoint);
        session.Disconnected += OnSessionDisconnected;

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
            Terminal.Focus();
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
        Terminal.DetachEndpoint();
        _endpoint = null;

        var session = _session;
        _session = null;
        if (session != null)
        {
            session.Disconnected -= OnSessionDisconnected;
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

    public async Task DisconnectAsync()
    {
        await DisposeAsync();
        State = ConnectionState.Disconnected;
        StatusMessage = Strings.Get("Main.Tab.Disconnected");
        _logger.LogInformation("终端标签主动断开 标题={Title}", Title);
    }

    public async ValueTask DisposeAsync()
    {
        // 重入保护：断开与关闭标签可能并发触发，只允许首次调用进入清理
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        Terminal.Loaded -= OnTerminalLoaded;
        Terminal.SizeChanged -= OnTerminalSizeChanged;

        await DetachSessionAsync();
        _logger.LogInformation("终端标签已释放 标题={Title}", Title);
    }
}
