using System;
using System.Collections.Generic;
using System.IO;

namespace Kei.Term.App.Services;

// Avalonia 的 X11 后端只凭 AVALONIA_IM_MODULE / GTK_IM_MODULE / QT_IM_MODULE / XMODIFIERS 选择 IME。
// Plasma Wayland 推荐不设置 *_IM_MODULE，而从桌面菜单启动的进程又常常拿不到 XMODIFIERS，
// 结果是 IME 一个都选不中、无法切换中文。这里在 Avalonia 初始化前补一个兜底：
// 用户未声明任何输入法时，按正在运行的输入法守护进程写入 AVALONIA_IM_MODULE（仅影响 Avalonia 应用）。
public static class LinuxImeEnvironment
{
    public const string AvaloniaImModuleVariable = "AVALONIA_IM_MODULE";

    public static void Apply()
    {
        if (!OperatingSystem.IsLinux()) return;

        string? module = Resolve(Environment.GetEnvironmentVariable, EnumerateProcessNames());
        if (module != null) Environment.SetEnvironmentVariable(AvaloniaImModuleVariable, module);
    }

    // 返回需要写入 AVALONIA_IM_MODULE 的值；用户已有显式配置或未发现输入法守护进程时返回 null。
    public static string? Resolve(Func<string, string?> getEnvironment, IEnumerable<string> processNames)
    {
        // 任何显式声明（含 "none"）都尊重用户选择，不做覆盖
        foreach (string name in new[] { AvaloniaImModuleVariable, "GTK_IM_MODULE", "QT_IM_MODULE" })
        {
            if (!string.IsNullOrEmpty(getEnvironment(name))) return null;
        }

        string? modifiers = getEnvironment("XMODIFIERS");
        if (modifiers != null && modifiers.Contains("@im=", StringComparison.Ordinal)) return null;

        bool hasIbus = false;
        foreach (string process in processNames)
        {
            // fcitx5 同时提供 IBus 兼容接口，优先走其原生 DBus 协议
            if (process is "fcitx5" or "fcitx") return "fcitx";
            if (process == "ibus-daemon") hasIbus = true;
        }

        return hasIbus ? "ibus" : null;
    }

    private static IEnumerable<string> EnumerateProcessNames()
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories("/proc");
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (name.Length == 0 || !char.IsAsciiDigit(name[0])) continue;

            string? comm = null;
            try
            {
                comm = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
            }
            catch (Exception)
            {
                // 进程在枚举期间退出或无权读取，跳过即可
            }

            if (!string.IsNullOrEmpty(comm)) yield return comm;
        }
    }
}
