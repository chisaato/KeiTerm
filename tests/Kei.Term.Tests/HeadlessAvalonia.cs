using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Kei.Term.Tests;

// 无窗口系统下运行真实 Avalonia 控件（Skia 实绘），供需要真实 TerminalControl 布局/缓冲区的测试使用。
// 整个测试程序集共享一个 UI 线程会话；测试体经 RunAsync 派发到该线程执行。
public static class HeadlessAvalonia
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(
        () => HeadlessUnitTestSession.StartNew(typeof(HeadlessAvalonia), AvaloniaTestIsolationLevel.PerAssembly),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // HeadlessUnitTestSession 按约定反射调用此方法构建应用
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Kei.Term.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static Task RunAsync(Action action) => Session.Value.Dispatch(action, CancellationToken.None);

    // 在 UI 线程上推进调度队列与渲染计时器，让布局、输出解析等排队工作执行完
    public static void Pump(int iterations = 20)
    {
        for (int i = 0; i < iterations; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
