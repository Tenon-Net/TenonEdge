using Microsoft.Extensions.Logging;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.UnitTests.Storage;

/// <summary>
/// 雪花 ID 生成器（总设计 §10.1）：位布局沿用 TenonAdmin 的实现；时钟规则按边缘设备的情况修改。
/// 全部使用可控时钟，不依赖真实时间或 sleep。
/// </summary>
public sealed class SnowflakeIdGeneratorTests
{
    private const long WorkerAndSequenceMask = (1L << 12) - 1;

    private static readonly DateTimeOffset BuildFloor = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IdsIncreaseStrictlyStayBelowTwoToThe53AndUseTheFixedWorkerId()
    {
        var time = new SettableTime(Now);
        var generator = Create(time);
        var previous = 0L;

        for (var i = 0; i < 10_000; i++)
        {
            if (i % 7 == 0)
            {
                time.Advance(TimeSpan.FromMilliseconds(1));
            }

            var id = generator.NextId();
            Assert.True(id > previous, $"第 {i} 个 ID {id} 不大于前一个 {previous}");
            Assert.True(id < SnowflakeIdGenerator.IdLimit);
            Assert.Equal(SnowflakeIdGenerator.WorkerId, (id >> 6) & 63);
            previous = id;
        }
    }

    [Fact]
    public void ParallelBurstIdsAreUnique()
    {
        // 沿用原实现的并发测试：多线程同时发号不重复。
        var generator = new SnowflakeIdGenerator(0, BuildFloor, TimeProvider.System, new RecordingLogger());
        var ids = new long[120_000];

        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = 32 }, index => ids[index] = generator.NextId());

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.InRange(id, 1, SnowflakeIdGenerator.IdLimit - 1));
    }

    [Fact]
    public void ClockGoingBackwardsContinuesOnTheLogicalTimestamp()
    {
        var time = new SettableTime(Now);
        var generator = Create(time);
        var before = generator.NextId();

        time.SetUtcNow(Now - TimeSpan.FromHours(1));
        var after = generator.NextId();

        Assert.True(after > before);
        Assert.Equal(SnowflakeIdGenerator.TimestampOf(before), SnowflakeIdGenerator.TimestampOf(after));
    }

    [Fact(Timeout = 10_000)]
    public async Task SequenceExhaustionAdvancesTheLogicalMillisecondWithoutWaitingForTheClock()
    {
        // 时钟停住不动：原实现会自旋等待下一毫秒，这里直接把逻辑时间戳加 1。
        var time = new SettableTime(Now);
        var generator = Create(time);
        var first = generator.NextId();

        var ids = await Task.Run(
            () => Enumerable.Range(0, (64 * 10) - 1).Select(_ => generator.NextId()).ToList(),
            TestContext.Current.CancellationToken);
        var next = generator.NextId();

        Assert.Equal(SnowflakeIdGenerator.TimestampOf(first) + 10, SnowflakeIdGenerator.TimestampOf(next));
        Assert.Equal(0, next & 63);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void UntrustedClockReadingIsIgnoredAndReportedOnceUntilItRecovers()
    {
        var time = new SettableTime(Now);
        var logger = new RecordingLogger();
        var generator = Create(time, logger: logger);
        var trusted = generator.NextId();

        // 读数达到 2^41 毫秒（约 2089 年 10 月之后）视为不可信：沿用逻辑时间戳，只告警一次。
        time.SetUtcNow(SnowflakeIdGenerator.Epoch + TimeSpan.FromMilliseconds(SnowflakeIdGenerator.TimestampLimit) + TimeSpan.FromDays(3));
        var untrusted = Enumerable.Range(0, 5).Select(_ => generator.NextId()).ToList();

        Assert.All(untrusted, id => Assert.Equal(SnowflakeIdGenerator.TimestampOf(trusted), SnowflakeIdGenerator.TimestampOf(id)));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);

        time.SetUtcNow(Now + TimeSpan.FromSeconds(1));
        var recovered = generator.NextId();
        Assert.True(recovered > untrusted[^1]);
        Assert.Equal(
            SnowflakeIdGenerator.TimestampOf(trusted) + 1000,
            SnowflakeIdGenerator.TimestampOf(recovered));
    }

    [Fact]
    public void FirstIdIsNotEarlierThanTheBuildTimestampFloorWhenTheClockIsWrong()
    {
        // 开机时实时时钟未设置，读数停在 1970 年。
        var time = new SettableTime(DateTimeOffset.UnixEpoch);
        var generator = Create(time);

        var id = generator.NextId();

        Assert.Equal((BuildFloor - SnowflakeIdGenerator.Epoch).Ticks / TimeSpan.TicksPerMillisecond, SnowflakeIdGenerator.TimestampOf(id));
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(17L, true)]
    [InlineData(63L, false)]
    [InlineData((5L << 6) | 3, false)]
    public void NewIdsAreGreaterThanTheFloorEvenWhenTheClockIsBehindIt(long floorLowBits, bool sameMillisecond)
    {
        // 下限位于时钟之后一天：例如恢复了时钟更快的设备上的备份。
        var floorTimestamp = ((Now + TimeSpan.FromDays(1)) - SnowflakeIdGenerator.Epoch).Ticks / TimeSpan.TicksPerMillisecond;
        var floor = (floorTimestamp << 12) | floorLowBits;
        var generator = Create(new SettableTime(Now), floor);

        var id = generator.NextId();

        Assert.True(id > floor);
        Assert.Equal(sameMillisecond ? floorTimestamp : floorTimestamp + 1, SnowflakeIdGenerator.TimestampOf(id));
    }

    [Fact]
    public void LogicalTimestampReachingTheLimitRefusesToIssue()
    {
        var lastTimestamp = SnowflakeIdGenerator.TimestampLimit - 1;
        var floor = (lastTimestamp << 12) | WorkerAndSequenceMask;
        var generator = Create(new SettableTime(Now), floor);

        var exception = Assert.Throws<InvalidOperationException>(() => generator.NextId());

        Assert.Contains("拒绝发号", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L << 53)]
    public void FloorOutsideTheIdRangeIsRejected(long floor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new SettableTime(Now), floor));
    }

    [Fact]
    public void BuildTimestampFloorIsRecordedAndNotInTheFuture()
    {
        Assert.InRange(IdGeneration.BuildTimestampFloor, SnowflakeIdGenerator.Epoch, DateTimeOffset.UtcNow);
    }

    private static SnowflakeIdGenerator Create(SettableTime time, long floor = 0, RecordingLogger? logger = null) =>
        new(floor, BuildFloor, time, logger ?? new RecordingLogger());

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset value) => _now = value;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
