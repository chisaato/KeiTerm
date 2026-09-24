namespace Kei.Term.App.Logging;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

// 按天滚动的落盘日志选项
public sealed class RollingFileLoggerOptions
{
    // 日志目录（= 数据库/设置文件所在目录下的 logs/），不存在时自动创建
    public required string LogDirectory { get; init; }

    // 文件名前缀：{prefix}.yyyyMMdd.log
    public string FileNamePrefix { get; init; } = "KeiTerm";

    // 保留天数（含今天），启动时清理更早的文件
    public int RetainedDays { get; init; } = 7;

    // 最低记录级别
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
}

// 自研落盘 Provider：按天滚动、UTF-8 append、线程安全、同时镜像 Console
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileWriter _writer;
    private readonly LogLevel _minimumLevel;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _writer = new RollingFileWriter(options);
        _minimumLevel = options.MinimumLevel;
    }

    public ILogger CreateLogger(string categoryName)
        => new RollingFileLogger(categoryName, _writer, _minimumLevel);

    public void Dispose() => _writer.Dispose();
}

// 单行格式：`2026-09-23 12:33:44.123 [INF] [类别短名] 消息`；异常另起缩进行含 StackTrace
internal sealed class RollingFileLogger : ILogger
{
    private readonly string _categoryShortName;
    private readonly RollingFileWriter _writer;
    private readonly LogLevel _minimumLevel;

    public RollingFileLogger(string categoryName, RollingFileWriter writer, LogLevel minimumLevel)
    {
        _categoryShortName = ShortCategory(categoryName);
        _writer = writer;
        _minimumLevel = minimumLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
        => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        _writer.Write(DateTimeOffset.Now, logLevel, _categoryShortName, message, exception);
    }

    // 取类别短名：Kei.Term.App.ViewModels.MainViewModel -> MainViewModel
    private static string ShortCategory(string categoryName)
    {
        if (string.IsNullOrEmpty(categoryName))
        {
            return "App";
        }

        var index = categoryName.LastIndexOf('.');
        return index >= 0 && index < categoryName.Length - 1
            ? categoryName[(index + 1)..]
            : categoryName;
    }
}

// 线程安全的按天滚动写入器：day 变化时切换文件；写入即 flush 以便崩溃取证
internal sealed class RollingFileWriter : IDisposable
{
    private readonly RollingFileLoggerOptions _options;
    private readonly object _sync = new();

    private StreamWriter? _writer;
    private DateTime _currentDate;
    private bool _disposed;

    public RollingFileWriter(RollingFileLoggerOptions options)
    {
        _options = options;
        try
        {
            Directory.CreateDirectory(options.LogDirectory);
            CleanupExpiredFiles();
        }
        catch
        {
            // 日志目录不可用时不阻断应用启动
        }
    }

    public void Write(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception)
    {
        var line = RollingLogFormatter.Format(timestamp, level, category, message, exception);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(timestamp.LocalDateTime.Date);
                _writer?.WriteLine(line);
            }
            catch
            {
                // 落盘失败静默，避免日志本身影响业务
            }
        }

        // 镜像到 Console（dotnet run 调试可见）
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // Console 不可用时忽略
        }
    }

    // 切到当天文件（首次或跨天）
    private void EnsureWriter(DateTime date)
    {
        if (_writer != null && _currentDate == date)
        {
            return;
        }

        _writer?.Dispose();
        _currentDate = date;
        var path = Path.Combine(_options.LogDirectory, $"{_options.FileNamePrefix}.{date:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    // 启动清理：按文件名日期删除保留窗口之外的文件
    private void CleanupExpiredFiles()
    {
        var cutoff = DateTime.Today.AddDays(-(_options.RetainedDays - 1));
        foreach (var file in Directory.EnumerateFiles(_options.LogDirectory, $"{_options.FileNamePrefix}.*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var datePart = name.Length > _options.FileNamePrefix.Length
                ? name[(_options.FileNamePrefix.Length + 1)..]
                : string.Empty;

            var fileDate = DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : File.GetLastWriteTime(file).Date;

            if (fileDate < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 单个文件删除失败不影响其它
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // 忽略 flush 异常
            }
            _writer?.Dispose();
            _writer = null;
        }
    }
}

// 行格式器（独立出来便于单测）
internal static class RollingLogFormatter
{
    public static string Format(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(" [");
        builder.Append(Abbreviate(level));
        builder.Append("] [");
        builder.Append(category);
        builder.Append("] ");
        builder.Append(message);

        if (exception != null)
        {
            // 异常另起缩进行，含完整 StackTrace（ToString 也覆盖 InnerException）
            foreach (var line in exception.ToString().Split('\n'))
            {
                builder.Append('\n');
                builder.Append("    ");
                builder.Append(line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
