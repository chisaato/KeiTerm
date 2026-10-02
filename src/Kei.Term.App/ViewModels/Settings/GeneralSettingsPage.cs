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

    // 界面与面板显隐配置 (常开/常关/保持上次)
    public IReadOnlyList<string> VisibilityModes { get; } = ["常开 (Always Visible)", "常关 (Always Hidden)", "保持上次状态 (Remember Last)"];

    [ObservableProperty]
    private string _selectedSessionManagerMode = "保持上次状态 (Remember Last)";

    [ObservableProperty]
    private string _selectedComposeBarMode = "保持上次状态 (Remember Last)";

    [RelayCommand]
    private async Task ClearCacheDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(CacheDirectory) || !Directory.Exists(CacheDirectory)) return;
        try
        {
            await Task.Run(() =>
            {
                foreach (var dir in Directory.GetDirectories(CacheDirectory))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
                foreach (var file in Directory.GetFiles(CacheDirectory))
                {
                    try { File.Delete(file); } catch { }
                }
            });
        }
        catch { }
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
        catch
        {
            // 打开失败不打断设置窗口
        }
        await Task.CompletedTask;
    }
}
