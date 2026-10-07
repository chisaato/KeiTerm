namespace Kei.Term.Core.Settings;

using Kei.Term.Core.Models;
using Kei.Term.Core.Security;

public enum PanelVisibilityMode
{
    AlwaysVisible = 0,
    AlwaysHidden = 1,
    RememberLastState = 2
}

public class AppSettings
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
    public int ScrollbackLines { get; set; } = 5000;
    public bool CursorBlink { get; set; } = true;

    // 界面全局字体（UI Font），用于整个 App 的菜单、树、弹窗与常规文字渲染
    public string UiFontFamily { get; set; } = "Noto Sans CJK SC";
    public string UiFallbackFontFamily { get; set; } = "Source Han Sans SC, Microsoft YaHei UI, Segoe UI, sans-serif";
    public double UiFontSize { get; set; } = 13.0;

    // 终端备用/回退字体（Fallback Font），供终端排版时字符缺失回退
    public string TerminalFallbackFontFamily { get; set; } = "Noto Sans Mono CJK SC, Source Han Sans HW SC, Microsoft YaHei, monospace";

    // UI 主题，取值 "Dark" | "System"
    public string UiTheme { get; set; } = "Dark";

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

    // 文件侧栏跟随终端目录的全局默认方式；会话可覆盖
    public CwdFollowMode CwdFollowMode { get; set; } = CwdFollowMode.Off;

    // 选中的 GUI 配色 Profile ID 与终端配色 Profile ID
    public string? ActiveGuiProfileId { get; set; }
    public string? ActiveTerminalProfileId { get; set; }

    // 文件传输与本地缓存设置
    public FileTransferSettings FileTransfer { get; set; } = new();
}

public class FileTransferSettings
{
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
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);

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
