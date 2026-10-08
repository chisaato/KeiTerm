using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Kei.Term.Tests.HeadlessWarmUpFramework", "Kei.Term.Tests")]

namespace Kei.Term.Tests;

// 测试运行前先在会话线程上完成 Avalonia 初始化：Dispatcher.UIThread 归属于首个访问它的线程，
// 若被并行执行的其他测试（经生产代码间接投递到 UI 线程）抢先占用，Headless 测试会报跨线程访问。
// 不用 ModuleInitializer：会话线程反射本程序集类型时会等待模块初始化完成，造成死锁。
public sealed class HeadlessWarmUpFramework : XunitTestFramework
{
    public HeadlessWarmUpFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        HeadlessAvalonia.RunAsync(() => { }).GetAwaiter().GetResult();
    }
}

// 无窗口系统下运行真实 Avalonia 控件（Skia 实绘），供需要真实 TerminalControl 布局/缓冲区的测试使用。
// 整个测试程序集共享一个 UI 线程会话；测试体经 RunAsync 派发到该线程执行。
public static class HeadlessAvalonia
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(
        () => HeadlessUnitTestSession.StartNew(typeof(HeadlessAvalonia), AvaloniaTestIsolationLevel.PerAssembly),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // 特性构造期用来判断能不能安全派发到 UI 线程。未启动时禁止访问 Dispatcher.UIThread。
    public static bool IsSessionStarted => Session.IsValueCreated;

    // HeadlessUnitTestSession 按约定反射调用此方法构建应用
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Kei.Term.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static Task RunAsync(Action action) => Session.Value.Dispatch(action, CancellationToken.None);

    public static Task RunAsync(Func<Task> action) => Session.Value.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    // 在 UI 线程上推进调度队列与渲染计时器，让布局、输出解析等排队工作执行完
    public static void Pump(int iterations = 20)
    {
        for (int i = 0; i < iterations; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // 后台搜索不会被单次 Pump 等完：Sleep 让工作线程跑完，下一轮 Pump 再消化到期的 DispatcherTimer。
    // 超时直接断言失败，避免调用方忘了检查返回值而假绿。
    public static void WaitUntil(Func<bool> condition, int timeoutMs = 2000, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (timeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition())
            {
                return;
            }

            if (watch.ElapsedMilliseconds >= timeoutMs)
            {
                Pump();
                if (condition())
                {
                    return;
                }

                Assert.Fail(message ?? $"条件在 {timeoutMs} ms 内未成立");
            }

            Thread.Sleep(15);
        }
    }
}
