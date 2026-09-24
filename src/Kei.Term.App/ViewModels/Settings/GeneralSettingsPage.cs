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

    // 树同级排序模式
    public IReadOnlyList<TreeSortOption> TreeSortOptions { get; }

    [ObservableProperty]
    private TreeSortOption _selectedTreeSort;

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
