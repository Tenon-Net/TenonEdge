using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;

namespace TenonEdge.UnitTests.Adapters;

/// <summary>
/// 模拟输入（T1-02，GW-07；功能需求 §3.5）：配置解析，以及“同一种子、同一配置得到同一序列”——
/// 电表模板的数值范围与换算逐位确定；缺字段、非法值与连接错误按比例出现，各自的表现符合总设计 §6.1、§6.4。
/// </summary>
public sealed class SimulatorSequenceTests
{
    [Fact]
    public void DefaultsAreTheMeterTemplateEveryFiveSecondsWithoutFaults()
    {
        var settings = Parse("{}");

        Assert.Equal(TimeSpan.FromSeconds(5), settings.Period);
        Assert.Equal(SimulatorSettings.MeterTemplate, settings.Template);
        Assert.Null(settings.Sample);
        Assert.Equal(0, settings.MissingFieldRatio);
        Assert.Equal(0, settings.InvalidValueRatio);
        Assert.Equal(0, settings.ConnectionErrorRatio);

        // 省略种子时由数据来源推出：同一数据来源总是同一序列，不同数据来源不同。
        Assert.Equal(settings.Seed, Parse("{}").Seed);
        Assert.NotEqual(settings.Seed, Parse("{}", key: "meter-002/simulated").Seed);
    }

    [Theory]
    [InlineData("""{"periodMs":999}""", "periodMs")] // 首版最小 1 秒
    [InlineData("""{"periodMs":86400001}""", "periodMs")]
    [InlineData("""{"periodMs":1500.5}""", "periodMs")]
    [InlineData("""{"periodMs":"5000"}""", "periodMs")]
    [InlineData("""{"seed":"42"}""", "seed")]
    [InlineData("""{"seed":1.5}""", "seed")]
    [InlineData("""{"template":"boiler"}""", "template")]
    [InlineData("""{"template":1}""", "template")]
    [InlineData("""{"template":"meter","sample":{"a":1}}""", "只能选一个")]
    [InlineData("""{"sample":[]}""", "sample")]
    [InlineData("""{"sample":{}}""", "sample")]
    [InlineData("""{"sample":{"a":null}}""", "空值")]
    [InlineData("""{"sample":{"a":{"b":1}}}""", "一层")]
    [InlineData("""{"sample":{"":1}}""", "字段名")]
    [InlineData("""{"faults":{"missingField":1.5}}""", "faults.missingField")]
    [InlineData("""{"faults":{"connectionError":-0.1}}""", "faults.connectionError")]
    [InlineData("""{"faults":{"invalidValue":"0.1"}}""", "faults.invalidValue")]
    [InlineData("""{"faults":{"timeout":0.1}}""", "timeout")]
    [InlineData("""{"faults":[]}""", "faults")]
    [InlineData("""{"periodMS":5000}""", "periodMS")] // 拼写错误不能被悄悄忽略
    public void InvalidConfigurationIsAStructuralBuildFailure(string json, string expected)
    {
        var exception = Assert.Throws<RuntimeUnitBuildException>(() => Parse(json));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Equal(RuntimeUnitId.Create(SimulatorInputFactory.UnitKind, "meter-001/simulated"), exception.UnitId);
    }

    [Fact]
    public void SampleValuesKeepTheirJsonTypes()
    {
        var settings = Parse("""{"sample":{"count":9007199254740993,"ratio":1.50,"big":1e300,"on":true,"name":"温度","small":-40}}""");

        var sample = Assert.IsAssignableFrom<IReadOnlyDictionary<string, FieldValue>>(settings.Sample);
        Assert.Equal(FieldValue.FromInt64(9_007_199_254_740_993L), sample["count"]);
        Assert.Equal(FieldValue.FromDecimal(1.50m), sample["ratio"]);
        Assert.Equal("1.50", sample["ratio"].ToString());
        Assert.Equal(FieldValue.FromBoolean(true), sample["on"]);
        Assert.Equal(FieldValue.FromString("温度"), sample["name"]);
        Assert.Equal(FieldValue.FromInt64(-40), sample["small"]);
        Assert.Equal(FieldType.Number, sample["big"].Type);
    }

    [Fact]
    public void TheSameSeedAndConfigurationGiveTheSameSequence()
    {
        const string json = """{"seed":42,"faults":{"missingField":0.2,"invalidValue":0.2,"connectionError":0.2}}""";
        var first = Take(new SimulatorSequence(Parse(json)), 500);
        var second = Take(new SimulatorSequence(Parse(json)), 500);
        var otherSeed = Take(new SimulatorSequence(Parse(json.Replace("42", "43", StringComparison.Ordinal))), 500);

        Assert.Equal(first.Select(Describe), second.Select(Describe));
        Assert.NotEqual(first.Select(Describe), otherSeed.Select(Describe));
    }

    [Fact]
    public void TheSequenceIsPinnedAcrossVersions()
    {
        // 序列由 SplitMix64 与固定的换算规则决定，不随 .NET 版本或平台变化；改动生成规则时这里会失败，提醒同步更新文档。
        var first = new SimulatorSequence(Parse("""{"seed":42}""")).Next();

        // 216.6 × 11.21 ÷ 1000 = 2.428086，舍入为 2.428；电能为种子决定的起始值加 2.428 kW × 5 秒。
        Assert.Equal(
            "current=11.21,energy=74156.003,frequency=49.97,power=2.428,voltage=216.6|",
            Describe(first));
    }

    [Fact]
    public void MeterReadingsStayInRangeAndAreDerivedExactly()
    {
        var readings = Take(new SimulatorSequence(Parse("""{"seed":7,"periodMs":3600000}""")), 1000);

        decimal? previousEnergy = null;
        foreach (var reading in readings)
        {
            var voltage = decimal.Parse(reading.Values["voltage"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var current = decimal.Parse(reading.Values["current"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var frequency = decimal.Parse(reading.Values["frequency"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var power = decimal.Parse(reading.Values["power"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var energy = decimal.Parse(reading.Values["energy"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(voltage, 214.0m, 226.0m);
            Assert.InRange(current, 8.00m, 12.00m);
            Assert.InRange(frequency, 49.90m, 50.10m);
            Assert.Equal(Math.Round(voltage * current / 1000m, 3, MidpointRounding.AwayFromZero), power);
            Assert.All(reading.Values.Values, value => Assert.Equal(FieldType.Number, value.Type));
            Assert.Empty(reading.Quality);

            // 周期为 1 小时：电能每周期增加的千瓦时数等于功率（舍入到三位小数前后相差不超过 0.001）。
            if (previousEnergy is { } previous)
            {
                Assert.InRange(energy - previous, power - 0.001m, power + 0.001m);
            }

            previousEnergy = energy;
        }
    }

    [Fact]
    public void FaultsAppearInTheConfiguredProportionsAndBehaveAsSpecified()
    {
        var readings = Take(new SimulatorSequence(Parse("""{"seed":1,"faults":{"missingField":0.1,"invalidValue":0.05,"connectionError":0.02}}""")), 10_000);

        var connectionErrors = readings.Count(reading => reading.IsConnectionError);
        var invalid = readings.Count(reading => reading.Quality.Count > 0);
        var missing = readings.Count(reading => !reading.IsConnectionError && reading.Values.Count + reading.Quality.Count < 5);

        Assert.InRange(connectionErrors, 140, 260);
        Assert.InRange(invalid, 400, 600);
        Assert.InRange(missing, 850, 1150);

        // 连接错误：整组失败，不生成任何字段。
        Assert.All(readings.Where(reading => reading.IsConnectionError), reading =>
        {
            Assert.Empty(reading.Values);
            Assert.Empty(reading.Quality);
        });

        // 非法值：一个字段读到 NaN，从值中省略并标为 nonFinite，不变成 0。
        Assert.All(readings.Where(reading => reading.Quality.Count > 0), reading =>
        {
            var (field, code) = Assert.Single(reading.Quality);
            Assert.Equal(QualityCode.NonFinite, code);
            Assert.False(reading.Values.ContainsKey(field));
        });
    }

    [Fact]
    public void ACustomSampleIsSentAsIs()
    {
        var readings = Take(new SimulatorSequence(Parse("""{"sample":{"temperature":21.5,"status":"ok"}}""")), 3);

        Assert.All(readings, reading =>
        {
            Assert.Equal("21.5", reading.Values["temperature"].ToString());
            Assert.Equal("ok", reading.Values["status"].ToString());
        });
    }

    private static SimulatorSettings Parse(string json, string key = "meter-001/simulated")
    {
        using var document = JsonDocument.Parse(json);
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create(SimulatorInputFactory.UnitKind, key), document.RootElement);
        var segments = unit.Id.KeySegments;
        return SimulatorSettings.Parse(unit, segments[0], segments[1]);
    }

    private static List<SimulatedReading> Take(SimulatorSequence sequence, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => sequence.Next())];

    private static string Describe(SimulatedReading reading) =>
        reading.IsConnectionError
            ? "connection-error"
            : string.Join(",", reading.Values.Select(field => $"{field.Key}={field.Value}"))
                + "|" + string.Join(",", reading.Quality.Select(field => $"{field.Key}={field.Value}"));
}
