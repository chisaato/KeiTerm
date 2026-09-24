namespace Kei.Term.Core.Settings;

public class AppSettings
{
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

    // 关闭窗口前是否弹出确认
    public bool ConfirmBeforeClose { get; set; } = true;

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
    public int ConnectTimeoutSeconds { get; set; } = 15;

    // SSH-Agent 设置
    public bool EnableAgentForwarding { get; set; } = false;
    public string? CustomAgentSocketPath { get; set; }

    // 标签栏位置："Top" (置顶，默认) | "Bottom" (置底)
    public string TabPlacement { get; set; } = "Top";

    // 选中的 GUI 配色 Profile ID 与终端配色 Profile ID
    public string? ActiveGuiProfileId { get; set; }
    public string? ActiveTerminalProfileId { get; set; }
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
}
