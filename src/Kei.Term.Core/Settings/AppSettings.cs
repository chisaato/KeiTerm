namespace Kei.Term.Core.Settings;

using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Transfers;

public enum PanelVisibilityMode
{
    AlwaysVisible = 0,
    AlwaysHidden = 1,
    RememberLastState = 2
}

public enum FileSizeDisplayMode
{
    Iec = 0,
    Si = 1,
    Bytes = 2
}

public class AppSettings : System.Text.Json.Serialization.IJsonOnDeserialized, System.Text.Json.Serialization.IJsonOnSerializing
{
    // 连接管理器显示模式与最后状态
    public PanelVisibilityMode SessionManagerVisibilityMode { get; set; } = PanelVisibilityMode.RememberLastState;
    public bool LastSessionManagerVisible { get; set; } = true;
    // 不固定时作为临时覆盖面板展开，不占用终端宽度；重启后保持收起。
    public bool SessionManagerPinned { get; set; } = true;

    // 撰写栏显示模式与最后状态
    public PanelVisibilityMode ComposeBarVisibilityMode { get; set; } = PanelVisibilityMode.RememberLastState;
    public bool LastComposeBarVisible { get; set; } = false;

    // 终端外观设置（FontFamily 必须是单一字体族名，TerminalControl 不支持逗号候选列表）
    public string FontFamily { get; set; } = "JetBrainsMono Nerd Font Mono";
    public double FontSize { get; set; } = 14.0;
    public int TerminalFontWeight { get; set; } = 400;
    public int ScrollbackLines { get; set; } = 5000;
    public bool CursorBlink { get; set; } = true;

    // 界面全局字体（UI Font），用于整个 App 的菜单、树、弹窗与常规文字渲染
    public string UiFontFamily { get; set; } = "Noto Sans CJK SC";
    public string UiFallbackFontFamily { get; set; } = "Source Han Sans SC, Microsoft YaHei UI, Segoe UI, sans-serif";
    public double UiFontSize { get; set; } = 13.0;

    // 终端备用/回退字体（Fallback Font），供终端排版时字符缺失回退
    private string? _legacyFallbackFonts = "Noto Sans Mono CJK SC, Source Han Sans HW SC, Microsoft YaHei, monospace";
    private List<FallbackFontSetting> _terminalFallbackFonts = [];
    private bool _hasLegacyFallbackFonts;
    private bool _hasFallbackFonts;

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TerminalFallbackFontFamily
    {
        get => _legacyFallbackFonts;
        set { _legacyFallbackFonts = value; _hasLegacyFallbackFonts = true; }
    }

    public List<FallbackFontSetting> TerminalFallbackFonts
    {
        get => _terminalFallbackFonts;
        set { _terminalFallbackFonts = value; _hasFallbackFonts = true; }
    }

    public void MigrateFallbackFonts()
    {
        if (TerminalFallbackFonts is not { Count: > 0 } && !string.IsNullOrWhiteSpace(TerminalFallbackFontFamily))
            TerminalFallbackFonts = TerminalFallbackFontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(family => new FallbackFontSetting { Family = family }).ToList();
        TerminalFallbackFonts ??= [];
        TerminalFallbackFontFamily = null;
    }

    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        // 新版明确保存的空列表不能被构造器的旧版默认字符串重新填充；
        // 两个字段都缺失的更早配置仍然沿用默认字体链。
        if (_hasFallbackFonts && !_hasLegacyFallbackFonts) _legacyFallbackFonts = null;
        MigrateFallbackFonts();
    }
    void System.Text.Json.Serialization.IJsonOnSerializing.OnSerializing() => MigrateFallbackFonts();

    // UI 主题，取值 "Dark" | "System"
    public string UiTheme { get; set; } = "Dark";

    // 桌面布局外壳。保存后下次启动才生效；当前窗口的有效模式由启动期锁定，不在此处热切换。
    public LayoutMode LayoutMode { get; set; } = LayoutMode.Classic;

    // 控件库主题："KeiClassic" | "Semi" | "Material"（Semi/Material 为覆盖式第三方控件主题）
    public string ControlLibraryTheme { get; set; } = "KeiClassic";

    // 关闭窗口或通过快捷键退出前是否确认；“不再提示”在确认退出时关闭此选项。
    public bool ConfirmBeforeClose { get; set; } = true;

    // 意外断开后在原标签里自动重连。默认关：误锤生产机比少一次重连更糟。
    public bool AutoReconnectOnDisconnect { get; set; }

    // Linux 菜单栏显示偏好：true = 允许桌面全局菜单组件接管，未接管时仍显示窗口内菜单；
    // false = 不导出，菜单固定显示在应用窗口内。
    // 仅 Linux/X11 可切换（对应 X11PlatformOptions.UseDBusMenu）；macOS 系统菜单栏为强制、
    // 无对应开关，Windows 无全局菜单概念，两端均为无操作。
    // 该值在 AppBuilder 阶段被读取，改动需重启应用才生效。
    public bool UseNativeGlobalMenu { get; set; } = true;

    // 支持的平台使用系统原生上下文菜单，其余平台回退应用菜单；默认关闭，保存后立即生效。
    public bool UseNativeContextMenus { get; set; }

    // 标签导航共用的修饰键；Primary 随平台解析，可包含 Shift 等组合。
    public string TabNavigationModifier { get; set; } = "Primary";

    // Primary 在 macOS 表示 Command，在其他平台表示 Ctrl；快捷面板仅在应用前台响应。
    public string CommandPaletteShortcut { get; set; } = "Primary+Shift+P";

    // 目录树排序规则："AsciiFirst" (英文优先，默认) | "Pinyin" (中文拼音本地化优先)
    public string TreeSortMode { get; set; } = "AsciiFirst";

    // 会话树显示密度预设："UltraCompact" (极度紧凑) | "Compact" (紧凑，默认) | "Normal" (标准) | "Custom" (自定义微调)
    public string TreeDensityPreset { get; set; } = "Compact";

    // 会话树尺寸微调
    public double TreeItemHeight { get; set; } = 22.0;
    public double TreeFontSize { get; set; } = 12.0;
    public double TreeIconSize { get; set; } = 14.0;
    public double TreeIndent { get; set; } = 12.0;

    // 默认连接偏好
    public int DefaultPort { get; set; } = 22;
    public string DefaultTerminalType { get; set; } = "xterm-256color";
    public int KeepAliveIntervalSeconds { get; set; } = 30;

    // 全局默认用户名（会话未填用户名时的回退，空白视为未设置）
    public string DefaultUsername { get; set; } = "";

    // 全局默认身份（会话未绑定身份时的回退）
    public Guid? DefaultIdentityId { get; set; } = null;

    // 优先尝试系统 ssh-agent 进行认证（仅未绑定身份场景生效）
    public bool PreferSystemAgent { get; set; } = true;

    // Vault 自动锁定超时（分钟）；0 = 不自动锁定（App 层负责计时并调用 Lock）
    public int LockTimeoutMinutes { get; set; } = 0;

    // SSH 连接超时（秒）
    public int ConnectTimeoutSeconds { get; set; } = 60;

    // 主机密钥校验策略：Ask（未知弹窗确认/变更强警示）| AcceptNew（TOFU）| Strict（仅已知主机）
    public HostKeyPolicy HostKeyPolicy { get; set; } = HostKeyPolicy.Ask;

    // SSH-Agent 设置；CustomAgentSocketPath 为空走系统默认，"pageant" 走 PuTTY Pageant
    public bool EnableAgentForwarding { get; set; } = false;
    public string? CustomAgentSocketPath { get; set; }

    // 标签栏位置："Top" (置顶，默认) | "Bottom" (置底)
    public string TabPlacement { get; set; } = "Top";

    // 标签标题跟随远端（OSC 0/2）标题；会话可覆盖
    public bool TabTitleFollowsRemote { get; set; } = true;

    // 空白使用内建命名；终端和文件标签共用 local_* / remote_* 模板。
    public string TabTitleTemplate { get; set; } = string.Empty;

    // 文件侧栏跟随终端目录的全局默认方式；会话可覆盖
    public CwdFollowMode CwdFollowMode { get; set; } = CwdFollowMode.Off;

    // 目录跟随使用的复用器兼容策略；自动按远端上报的目录/标题信号识别。
    public TerminalMultiplexerMode TerminalMultiplexerMode { get; set; } = TerminalMultiplexerMode.Auto;

    // 选中的 GUI 配色 Profile ID 与终端配色 Profile ID
    public string? ActiveGuiProfileId { get; set; }
    public string? ActiveTerminalProfileId { get; set; }

    // 文件传输与本地缓存设置
    public FileTransferSettings FileTransfer { get; set; } = new();
}

public class FileTransferSettings
{
    // null 表示每次拖入终端时询问。仅「全局不再询问」持久化到此处。
    public TerminalFileDropAction? TerminalDropAction { get; set; }

    public TerminalUploadProtocol TerminalUploadProtocol { get; set; } = TerminalUploadProtocol.Scp;

    // 文件列表大小的显示单位；不影响远程文件大小或传输字节计数。
    public FileSizeDisplayMode SizeDisplayMode { get; set; } = FileSizeDisplayMode.Iec;

    // 本地缓存基目录，为空时默认使用应用标准缓存路径
    public string CacheDirectory { get; set; } = string.Empty;

    // 文件变更监视模式（默认智能 Auto）
    public FileWatcherMode WatcherMode { get; set; } = FileWatcherMode.Auto;

    // 轮询检测周期（秒）
    public int PollingIntervalSeconds { get; set; } = 3;

    // 文件写入防抖与等待时间（毫秒）
    public int WriteDebounceMilliseconds { get; set; } = 800;

    // 自定义外部编辑器路径（为空则使用系统默认关联程序，如 xdg-open）
    public string CustomEditorPath { get; set; } = string.Empty;

    // 文件管理侧栏停靠位置（false: 停靠在右侧，true: 停靠在左侧）
    public bool IsFileManagerOnLeft { get; set; } = false;

    public const int DefaultConcurrency = 3;
    public const int MinConcurrency = 1;
    public const int DefaultRetryCount = 3;
    public const int DefaultInitialRetryIntervalSeconds = 2;
    public const int DefaultMaxRetryIntervalSeconds = 60;

    // 同一连接同时传输的文件数。不设上限。小于 1 的值留在字段里，读取时再夹紧，避免保存把用户输入改掉。
    public int Concurrency { get; set; } = DefaultConcurrency;

    // 下载的默认目标目录。空字符串表示系统「下载」文件夹，由 ResolveDownloadDirectory 解析。
    public string DefaultDownloadDirectory { get; set; } = string.Empty;

    // 目标已有同名正式文件时的默认处置。枚举 0 是覆盖，缺字段必须靠初始值保持「每次询问」。
    public TransferConflictPolicy DefaultConflictPolicy { get; set; } = TransferConflictPolicy.Ask;

    // 关：遇到部分文件就删除并从头传。开：满足条件时从部分文件长度处接着传。
    public bool ResumeEnabled { get; set; } = false;

    // 暂时性失败是否自动重试。无法判断的失败按不重试处理。
    public bool AutoRetryEnabled { get; set; } = true;

    // 自动重试次数，不含首次传输。
    public int RetryCount { get; set; } = DefaultRetryCount;

    // 枚举 0 是固定间隔。缺字段必须靠初始值保持指数退避。
    public RetryBackoffMode BackoffMode { get; set; } = RetryBackoffMode.Exponential;

    // 第 1 次重试前的等待秒数。固定间隔始终用这个值。
    public int InitialRetryIntervalSeconds { get; set; } = DefaultInitialRetryIntervalSeconds;

    // 指数退避的等待上限（秒）。
    public int MaxRetryIntervalSeconds { get; set; } = DefaultMaxRetryIntervalSeconds;

    // 传输完成后比对哈希。与现有枚举一样按数字写入设置文件。
    public bool VerifyHashEnabled { get; set; } = true;

    // 前面的算法优先。不在列表中的算法视为禁用；读取时去重并丢掉未定义的数字。
    public List<TransferHashAlgorithm> HashAlgorithmOrder { get; set; } = [TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Md5];

    // 目录整包传输。默认关：开启后目录冲突按整个目录判定，而不是逐个文件。
    public bool ArchiveDirectoriesEnabled { get; set; }

    public List<TransferArchiveFormat> ArchiveFormatOrder { get; set; } = [TransferArchiveFormat.Zstd, TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar];

    public static int ClampConcurrency(int value)
        => value < MinConcurrency ? MinConcurrency : value;

    public int EffectiveConcurrency => ClampConcurrency(Concurrency);

    public IReadOnlyList<TransferHashAlgorithm> EffectiveHashAlgorithmOrder => DistinctDefined(HashAlgorithmOrder);

    public IReadOnlyList<TransferArchiveFormat> EffectiveArchiveFormatOrder => DistinctDefined(ArchiveFormatOrder);

    // 空列表等价于关掉该功能，即使上面的开关仍是 true。
    public bool EffectiveVerifyHashEnabled => VerifyHashEnabled && EffectiveHashAlgorithmOrder.Count > 0;

    public bool EffectiveArchiveDirectoriesEnabled => ArchiveDirectoriesEnabled && EffectiveArchiveFormatOrder.Count > 0;

    private static IReadOnlyList<T> DistinctDefined<T>(IEnumerable<T>? values)
        where T : struct, Enum
    {
        if (values == null)
        {
            return [];
        }

        List<T> result = [];
        HashSet<T> seen = [];
        foreach (T value in values)
        {
            if (!Enum.IsDefined(value) || !seen.Add(value))
            {
                continue;
            }

            result.Add(value);
        }

        return result;
    }

    public string ResolveDownloadDirectory()
        => DownloadDirectoryResolver.Resolve(DefaultDownloadDirectory);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);

    Task CommitTerminalDropActionAsync(TerminalFileDropAction? action, CancellationToken ct = default)
    {
        Current.FileTransfer.TerminalDropAction = action;
        return SaveSettingsAsync(Current, ct);
    }

    // 只提交字号。默认实现改内存后整对象保存。
    // 生产实现应在保存锁内补丁当前对象，避免用调用时的完整快照回滚其他字段。
    Task CommitFontSizeAsync(double fontSize, CancellationToken ct = default)
    {
        Current.FontSize = fontSize;
        return SaveSettingsAsync(Current, ct);
    }
}

// 已提交设置的内存值已变化（当前由外部字号提交触发）。
// 订阅方只同步自己尚未编辑的字段，禁止据此全量重载草稿。
public interface INotifySettingsCommitted
{
    event EventHandler? SettingsCommitted;
}
