using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.ViewModels.Settings;

public record TreeSortOption(string Mode, string Label);

// 「常规」分类页：应用行为、树排序规则与数据存储
public partial class GeneralSettingsPage : ViewModelBase
{
    public GeneralSettingsPage(string dataDirectory)
    {
        DataDirectory = dataDirectory ?? string.Empty;

        TreeSortOptions = new[]
        {
            new TreeSortOption("AsciiFirst", Strings.Get("Settings.General.TreeSortAsciiFirst")),
            new TreeSortOption("Pinyin", Strings.Get("Settings.General.TreeSortPinyin")),
        };
        _selectedTreeSort = TreeSortOptions[0];
    }

    // 数据目录只读展示，生命周期内不变，无需变更通知
    public string DataDirectory { get; }

    // 关闭窗口前是否弹出确认
    [ObservableProperty]
    private bool _confirmBeforeClose = true;

    // 意外断开后自动重连。默认关。
    [ObservableProperty]
    private bool _autoReconnectOnDisconnect;

    // Linux 菜单导出偏好；桌面组件接管后菜单移到全局栏，否则仍显示在窗口内。
    // 关闭时不导出。改动需重启应用生效。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGlobalMenuChecked))]
    private bool _useNativeGlobalMenu = true;

    // 当前平台是否可切换该开关：只有 Linux/X11 后端存在可关闭的 DBusMenu 导出器。
    // macOS 走原生 NSMenu 且系统菜单栏为强制，Windows 无全局菜单概念——两者置灰。
    public bool IsGlobalMenuOptionSupported { get; } = OperatingSystem.IsLinux();

    // 禁用的复选框显示当前平台的行为，同时保留跨平台迁移配置中的 Linux 偏好。
    public bool IsGlobalMenuChecked
    {
        get => IsGlobalMenuOptionSupported ? UseNativeGlobalMenu : OperatingSystem.IsMacOS();
        set
        {
            if (IsGlobalMenuOptionSupported)
            {
                UseNativeGlobalMenu = value;
            }
        }
    }

    // 保留跨平台偏好；支持的平台启用系统原生上下文菜单，其他平台沿用应用菜单。
    [ObservableProperty]
    private bool _useNativeContextMenus;

    // 树同级排序模式
    public IReadOnlyList<TreeSortOption> TreeSortOptions { get; }

    [ObservableProperty]
    private TreeSortOption _selectedTreeSort;

    // 文件传输缓存目录与监视模式
    [ObservableProperty]
    private string _cacheDirectory = string.Empty;

    [ObservableProperty]
    private string _customEditorPath = string.Empty;

    public IReadOnlyList<string> WatcherModes { get; } = ["Auto", "OSNative", "Polling"];

    [ObservableProperty]
    private string _selectedWatcherMode = "Auto";

    // 界面与面板显隐配置
    public IReadOnlyList<string> VisibilityModes { get; } =
    [
        Strings.Get("Settings.General.Visibility.AlwaysVisible"),
        Strings.Get("Settings.General.Visibility.AlwaysHidden"),
        Strings.Get("Settings.General.Visibility.RememberLast")
    ];

    [ObservableProperty]
    private string _selectedSessionManagerMode = Strings.Get("Settings.General.Visibility.RememberLast");

    [ObservableProperty]
    private bool _sessionManagerPinned = true;

    [ObservableProperty]
    private string _selectedComposeBarMode = Strings.Get("Settings.General.Visibility.RememberLast");

    [RelayCommand]
    private async Task ClearCacheDirectoryAsync()
    {
        string directory = CacheDirectory;
        await Task.Run(() => Kei.Term.Core.Services.LocalFileTracker.ClearClosedCache(directory));
    }

    public void SetTreeSortMode(string? mode)
    {
        var target = TreeSortOptions.FirstOrDefault(o => o.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase))
            ?? TreeSortOptions[0];
        SelectedTreeSort = target;
    }

    // 在系统文件管理器中打开数据目录，失败时静默
    [RelayCommand]
    private async Task OpenDataDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) || !Directory.Exists(DataDirectory)) return;
        try
        {
            var fileName = OperatingSystem.IsWindows() ? "explorer.exe"
                : OperatingSystem.IsMacOS() ? "open"
                : "xdg-open";
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"\"{DataDirectory}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开数据目录失败: {ex}");
        }
        await Task.CompletedTask;
    }
}
