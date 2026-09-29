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

// 终端标签：会话生命周期（挂载 / 连接 / 断开 / 重连）与文件侧栏；标签头见 .Header.cs，终端控件见 .Control.cs
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
