using System;
using System.IO;
using System.Linq;
using Kei.Term.App.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kei.Term.Tests;

// 自研 RollingFileLoggerProvider 落盘行为测试：行格式、按天文件名、异常 StackTrace、级别过滤、过期清理
public class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir;

    public RollingFileLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeiTermLogTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private SimpleLoggerFactory CreateFactory(LogLevel minimum = LogLevel.Information)
        => new(new RollingFileLoggerProvider(new RollingFileLoggerOptions
        {
            LogDirectory = _dir,
            MinimumLevel = minimum,
        }));

    private string TodayFile => Path.Combine(_dir, $"KeiTerm.{DateTime.Now:yyyyMMdd}.log");

    [Fact]
    public void Write_Info_CreatesDailyFileWithExpectedLineFormat()
    {
        using (var factory = CreateFactory())
        {
            var logger = factory.CreateLogger("Kei.Term.Tests.SampleCategory");
            logger.LogInformation("行为埋点测试 message={Value}", 42);
        }

        Assert.True(File.Exists(TodayFile), $"未生成当天日志文件: {TodayFile}");
        var line = File.ReadAllLines(TodayFile).Single();
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INF\] \[SampleCategory\] 行为埋点测试 message=42$",
            line);
    }

    [Fact]
    public void Write_Exception_AppendsIndentedStackTrace()
    {
        // 未抛出的异常 StackTrace 为空，这里真实抛出以取得堆栈
        Exception captured;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        using (var factory = CreateFactory())
        {
            var logger = factory.CreateLogger("Cat");
            logger.LogError(captured, "操作失败");
        }

        var content = File.ReadAllText(TodayFile);
        Assert.Contains("[ERR] [Cat] 操作失败", content);
        Assert.Contains("System.InvalidOperationException", content);
        Assert.Contains("boom", content);
        // StackTrace 行存在（异常另起缩进行）
        Assert.Contains("   at ", content);
        Assert.Contains("\n    ", content);
    }

    [Fact]
    public void Write_BelowMinimumLevel_IsFiltered()
    {
        using (var factory = CreateFactory(LogLevel.Information))
        {
            var logger = factory.CreateLogger("Cat");
            logger.LogDebug("debug-should-be-filtered");
            logger.LogInformation("info-kept");
        }

        var content = File.ReadAllText(TodayFile);
        Assert.DoesNotContain("debug-should-be-filtered", content);
        Assert.Contains("info-kept", content);
    }

    [Fact]
    public void Constructor_DeletesExpiredFiles_AndKeepsRecent()
    {
        Directory.CreateDirectory(_dir);
        var expired = Path.Combine(_dir, "KeiTerm.20000101.log");
        File.WriteAllText(expired, "old");
        var recent = Path.Combine(_dir, $"KeiTerm.{DateTime.Today.AddDays(-2):yyyyMMdd}.log");
        File.WriteAllText(recent, "recent");

        using var factory = CreateFactory();

        Assert.False(File.Exists(expired), "过期日志未被清理");
        Assert.True(File.Exists(recent), "保留窗口内的日志被误删");
    }

    [Fact]
    public void FileName_ContainsTodayDate()
    {
        using (var factory = CreateFactory())
        {
            factory.CreateLogger("Cat").LogInformation("x");
        }

        Assert.True(File.Exists(TodayFile));
        Assert.EndsWith($".{DateTime.Now:yyyyMMdd}.log", TodayFile);
    }
}
