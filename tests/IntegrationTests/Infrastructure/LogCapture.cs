using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 收集日志的提供程序：直接构造被测服务时用 <see cref="CreateLogger{T}"/>，进程内宿主中用 AddProvider 加入。
/// 保存类别、级别、格式化后的消息与异常文本，供断言使用。
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => [.. _entries];

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new SingleProviderFactory(this));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    /// <summary>指定类别（完整类型名）中消息包含给定文字的条目。</summary>
    public IReadOnlyList<CapturedLogEntry> Matching(string category, string text) =>
        [.. _entries.Where(entry => entry.Category == category && entry.Message.Contains(text, StringComparison.Ordinal))];

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLogEntry(category, logLevel, formatter(state, exception), exception?.ToString()));
    }

    private sealed class SingleProviderFactory(LogCapture capture) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public ILogger CreateLogger(string categoryName) => capture.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}

public sealed record CapturedLogEntry(string Category, LogLevel Level, string Message, string? ExceptionText = null);
