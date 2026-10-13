using System.Text.Json;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ConfigurationInterruptionProcessTests
{
    [Fact]
    public void MqttRecoveryPollingTreatsTransientMissingInputsAsNotReadyButStillRejectsDuplicates()
    {
        // Replay the full-suite failure snapshot: ForgetStopped has removed both old inputs,
        // while the original recovery path has not yet registered their replacements.
        var absent = JsonSerializer.SerializeToElement(new
        {
            runtime = new
            {
                units = new[] { Unit("delivery.writer", "queue"), Unit("output.http", "route/output") },
            },
        });
        Assert.False(MqttStopUnitRunning(absent, "meter/sample"));
        Assert.False(MqttStopUnitRunning(absent, "meter-slow/slow-stop"));
        var partial = JsonSerializer.SerializeToElement(new { runtime = new { units = new[] { Unit("input.mqtt", "meter/sample") } } });
        Assert.True(MqttStopUnitRunning(partial, "meter/sample"));
        Assert.False(MqttStopUnitRunning(partial, "meter-slow/slow-stop"));
        var full = JsonSerializer.SerializeToElement(new { runtime = new { units = new[] { Unit("input.mqtt", "meter/sample"), Unit("input.mqtt", "meter-slow/slow-stop") } } });
        Assert.True(MqttStopUnitRunning(full, "meter/sample") && MqttStopUnitRunning(full, "meter-slow/slow-stop"));
        var duplicate = JsonSerializer.SerializeToElement(new { runtime = new { units = new[] { Unit("input.mqtt", "meter/sample"), Unit("input.mqtt", "meter/sample") } } });
        Assert.ThrowsAny<Exception>(() => MqttStopUnitRunning(duplicate, "meter/sample"));
        static object Unit(string kind, string key) => new { id = new { kind, key }, state = "running" };
    }

    // ForgetStopped 与恢复组登记新实例之间可以暂时没有该单元；这是等待条件，不是已恢复断言。
    // 一旦存在仍用原 Single 校验唯一性；永久缺失由原等待期限失败，不能把缺失或重复当恢复成功。
    private static bool MqttStopUnitRunning(JsonElement status, string key) =>
        status.GetProperty("runtime").GetProperty("units").EnumerateArray().Any(unit =>
            unit.GetProperty("id").GetProperty("kind").GetString() == "input.mqtt" && unit.GetProperty("id").GetProperty("key").GetString() == key) &&
        MqttStopUnit(status, key).GetProperty("state").GetString() == "running";
}
