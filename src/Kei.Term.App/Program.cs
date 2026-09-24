using Avalonia;
using Avalonia.Logging;
using Avalonia.X11;
using Avalonia.Skia;
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

        // 后端选择：Wayland 优先（Avalonia 12 默认，UsePlatformDetect 跟随环境）；
        // KEITERM_BACKEND=x11 时强制走 X11（XWayland）——用于 Wayland 后端异常时的诊断与逃生门。
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

        return builder;
    }
}
