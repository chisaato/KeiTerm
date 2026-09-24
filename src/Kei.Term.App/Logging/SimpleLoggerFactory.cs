namespace Kei.Term.App.Logging;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

// 极简 ILoggerFactory：仅抽象包环境下手动组装 Provider，并支持按类型创建 ILogger<T>
public sealed class SimpleLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> _providers;

    public SimpleLoggerFactory(params ILoggerProvider[] providers)
    {
        _providers = [.. providers];
    }

    public ILogger CreateLogger(string categoryName) => new SimpleLogger(categoryName, _providers);

    // 便捷泛型入口：类别名 = 类型全名
    public ILogger<T> CreateLogger<T>() => new SimpleLogger<T>(typeof(T).FullName ?? typeof(T).Name, _providers);

    public void AddProvider(ILoggerProvider provider) => _providers.Add(provider);

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }
}

// 把日志分派给各 Provider 创建的 Logger
internal class SimpleLogger : ILogger
{
    private readonly IReadOnlyList<ILogger> _inners;

    public SimpleLogger(string categoryName, IReadOnlyList<ILoggerProvider> providers)
    {
        _inners = providers.Select(p => p.CreateLogger(categoryName)).ToArray();
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _inners.Any(l => l.IsEnabled(logLevel));

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        foreach (var logger in _inners)
        {
            logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

internal sealed class SimpleLogger<T> : SimpleLogger, ILogger<T>
{
    public SimpleLogger(string categoryName, IReadOnlyList<ILoggerProvider> providers)
        : base(categoryName, providers)
    {
    }
}
