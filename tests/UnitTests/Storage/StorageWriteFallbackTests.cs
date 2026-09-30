using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Storage;

/// <summary>
/// 审计与运行事件写入失败时改写到日志的限额（总设计 §3.3；T0-09 验收“存储异常可测，不无限写日志耗尽磁盘”）：
/// 每类每分钟最多逐条输出 20 条，超出时只输出一行提示并计数，下一分钟先汇总被省略的条数。
/// </summary>
public sealed class StorageWriteFallbackTests
{
    private const string Audit = "审计记录";
    private const string Events = "运行事件";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AtMostTwentyRecordsPerKindAreWrittenPerMinuteAndTheLimitIsAnnouncedOnce()
    {
        var time = new SettableTime(Start);
        var logger = new ListLogger();
        var fallback = new StorageWriteFallback(time, logger);

        var granted = Enumerable.Range(0, 100).Count(_ => fallback.TryAcquire(Audit));

        Assert.Equal(StorageWriteFallback.LinesPerMinute, granted);
        var notice = Assert.Single(logger.Messages);
        Assert.Contains("每分钟 20 条的上限", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressedRecordsAreSummarizedWhenTheNextMinuteStarts()
    {
        var time = new SettableTime(Start);
        var logger = new ListLogger();
        var fallback = new StorageWriteFallback(time, logger);
        for (var i = 0; i < StorageWriteFallback.LinesPerMinute + 7; i++)
        {
            fallback.TryAcquire(Audit);
        }

        time.Value = Start + TimeSpan.FromSeconds(59);
        Assert.False(fallback.TryAcquire(Audit));

        time.Value = Start + TimeSpan.FromMinutes(1);
        Assert.True(fallback.TryAcquire(Audit));

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("另有 8 条审计记录写入配置库失败，未逐条输出", logger.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void EachKindHasItsOwnQuota()
    {
        var time = new SettableTime(Start);
        var fallback = new StorageWriteFallback(time, new ListLogger());
        for (var i = 0; i < StorageWriteFallback.LinesPerMinute; i++)
        {
            Assert.True(fallback.TryAcquire(Audit));
        }

        Assert.False(fallback.TryAcquire(Audit));
        Assert.True(fallback.TryAcquire(Events));
    }

    [Fact]
    public void ClockMovingBackwardsStartsANewWindowInsteadOfSuppressingIndefinitely()
    {
        var time = new SettableTime(Start);
        var logger = new ListLogger();
        var fallback = new StorageWriteFallback(time, logger);
        for (var i = 0; i <= StorageWriteFallback.LinesPerMinute; i++)
        {
            fallback.TryAcquire(Events);
        }

        time.Value = Start - TimeSpan.FromHours(1);

        Assert.True(fallback.TryAcquire(Events));
        Assert.Contains("另有 1 条运行事件", logger.Messages[^1], StringComparison.Ordinal);
    }

    private sealed class SettableTime(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = value;

        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class ListLogger : ILogger<StorageWriteFallback>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
