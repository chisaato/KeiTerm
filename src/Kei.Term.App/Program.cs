using Avalonia;
using Avalonia.Logging;
using Avalonia.X11;
using Avalonia.Skia;
using Kei.Term.App.Services;
using System;

namespace Kei.Term.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .WithInterFont()
            // 诊断期调低阈值：框架内部（含平台后端）信息一并经 TraceToLogBridge 落盘
            .LogToTrace(LogEventLevel.Information);

        // 后端说明：本项目经 Avalonia.Desktop 只引入 X11/Win32/Native 三个后端，
        // 未引用 Avalonia.Wayland 包，因此 Linux 上 UsePlatformDetect 只会选到 X11；
        // Wayland 会话下由 XWayland 承载，不会以 Wayland 原生客户端运行。
        // KEITERM_BACKEND=x11 目前等价于默认路径，保留为将来引入 Wayland 后端后的逃生门。
        // 注意：UsePlatformDetect 会同时配置窗口系统与 Skia 渲染；单用 UseX11 必须补 UseSkia。
        if (string.Equals(
                Environment.GetEnvironmentVariable("KEITERM_BACKEND"),
                "x11",
                StringComparison.OrdinalIgnoreCase))
        {
            builder = builder.UseX11().UseSkia().UseHarfBuzz();
        }
        else
        {
            builder = builder.UsePlatformDetect();
        }

        // 全局菜单开关必须在 AppBuilder 阶段决定：X11 后端的 DBusMenu 导出器只在窗口创建时
        // 按 UseDBusMenu 决定是否注册，运行期无法切换——故此处直接读 settings.json，
        // 用户改动该设置后需重启应用生效。非 X11 平台该选项无副作用。
        return builder.With(NativeMenuSettings.CreateX11Options(AppPaths.SettingsFile));
    }
}
