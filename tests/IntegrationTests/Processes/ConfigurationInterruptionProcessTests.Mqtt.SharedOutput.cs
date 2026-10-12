using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MQTTnet.Protocol;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ConfigurationInterruptionProcessTests
{
    private static MqttFanOutRow[] ReadMqttFanOutRows(string data, long connectionId)
    {
        using var database = OpenQueue(data);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT seq, delivery_id, revision, source_message_id, payload, target_address, qos FROM delivery WHERE connection_id=$connection ORDER BY seq";
        command.Parameters.AddWithValue("$connection", connectionId);
        using var reader = command.ExecuteReader();
        var rows = new List<MqttFanOutRow>();
        while (reader.Read())
        {
            rows.Add(new(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), Encoding.UTF8.GetString((byte[])reader[4])),
                reader.GetString(5), reader.GetInt32(6)));
        }
        return rows.ToArray();
    }

    private static DeliveryRow[] HttpFanOutRows(string data, MqttFanOutRow[] mqtt) => ReadDeliveries(data)
        .Where(row => !mqtt.Any(other => other.Delivery.Id == row.Id)).ToArray();

    private static async Task SetMqttStopOutputPauseAsync(HttpClient api, long connectionId, bool paused)
    {
        var path = $"{Api}/connections/{connectionId}/delivery-control";
        var before = await api.GetJsonAsync(path, Token);
        Assert.Equal(!paused, before.GetProperty("manuallyPaused").GetBoolean());
        using var change = await api.PutAsJsonAsync(new Uri(path, UriKind.Relative), new
        { paused, confirmed = true, expectedVersion = before.GetProperty("resumeVersion").GetString() }, Token);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var result = await change.ReadJsonAsync(Token);
        Assert.Equal(paused, result.GetProperty("manuallyPaused").GetBoolean());
        Assert.NotEqual(before.GetProperty("resumeVersion").GetString(), result.GetProperty("resumeVersion").GetString());
        var saved = await api.GetJsonAsync(path, Token);
        Assert.Equal(result.GetRawText(), saved.GetRawText());
    }

    private static async Task AssertMqttStopOutputQuiescingAsync(HttpClient api, JsonElement quiet)
    {
        var sender = Assert.Single(quiet.GetProperty("runtime").GetProperty("units").EnumerateArray(), unit =>
            unit.GetProperty("id").GetProperty("kind").GetString() == "output.mqtt");
        // 此窗口尚未停完输入，耦合发送者监督循环仍在运行；由原 MQTT 会话拒绝重连/发布，而非管理员继续暂停。
        Assert.Equal("running", sender.GetProperty("state").GetString());
        var id = sender.GetProperty("id");
        var target = $"{id.GetProperty("kind").GetString()}/{id.GetProperty("key").GetString()}";
        const string expected = "MQTT 输入连接正在停止，记录保持待发。";
        var events = await WaitForAsync(api, $"{Api}/runtime/events?type=output.retry&target={Uri.EscapeDataString(target)}", page =>
            page.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("message").GetString() == expected));
        Assert.Contains(events.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("target").GetString() == target && item.GetProperty("message").GetString() == expected);
    }

    private static void AssertMqttFanOutFrozen(MqttFanOutRow[] mqtt, DeliveryRow[] http, string topic)
    {
        Assert.Equal(2, mqtt.Length);
        Assert.Equal(2, http.Length);
        Assert.Equal(4, mqtt.Select(row => row.Delivery.Id).Concat(http.Select(row => row.Id)).Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < mqtt.Length; index++)
        {
            Assert.Equal(topic, mqtt[index].Topic);
            Assert.Equal(1, mqtt[index].Qos);
            Assert.Equal(1, mqtt[index].Delivery.Revision);
            Assert.Equal(http[index].SourceId, mqtt[index].Delivery.SourceId);
            using var mqttJson = JsonDocument.Parse(mqtt[index].Delivery.Payload);
            using var httpJson = JsonDocument.Parse(http[index].Payload);
            Assert.Equal(index == 0 ? 42 : 43, mqttJson.RootElement.GetProperty("values").GetProperty("value").GetInt64());
            Assert.Equal(mqtt[index].Delivery.Id, mqttJson.RootElement.GetProperty("messageId").GetString());
            Assert.Equal(mqtt[index].Delivery.SourceId, mqttJson.RootElement.GetProperty("sourceMessageId").GetString());
            AssertMqttFanOutEnvelope(mqttJson.RootElement, httpJson.RootElement);
        }
    }

    private static async Task ReceiveMqttFanOutFrozenAsync(ReceivedMessages messages, MqttFanOutRow[] frozen)
    {
        foreach (var row in frozen)
        {
            var message = (await messages.NextAsync(Token, Deadline)).ApplicationMessage;
            Assert.Equal(row.Topic, message.Topic);
            Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, message.QualityOfServiceLevel);
            Assert.False(message.Retain);
            Assert.Equal(Encoding.UTF8.GetBytes(row.Delivery.Payload), message.Payload.ToArray());
        }
    }

    private static async Task ReceiveMqttFanOutSamplesAsync(ReceivedMessages messages, string topic, WireRequest[] http)
    {
        foreach (var request in http)
        {
            var message = (await messages.NextAsync(Token, Deadline)).ApplicationMessage;
            Assert.Equal(topic, message.Topic);
            Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, message.QualityOfServiceLevel);
            Assert.False(message.Retain);
            using var mqttJson = JsonDocument.Parse(message.Payload);
            using var httpJson = JsonDocument.Parse(request.Body);
            AssertMqttFanOutEnvelope(mqttJson.RootElement, httpJson.RootElement);
            Assert.Contains($"Idempotency-Key: {httpJson.RootElement.GetProperty("messageId").GetString()}", request.Header, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertMqttFanOutEnvelope(JsonElement mqtt, JsonElement http)
    {
        Assert.NotEqual(Guid.Empty, Guid.Parse(mqtt.GetProperty("messageId").GetString()!));
        Assert.NotEqual(mqtt.GetProperty("messageId").GetString(), http.GetProperty("messageId").GetString());
        foreach (var name in new[] { "schemaVersion", "sourceMessageId", "deviceKey", "collectedAt", "timeSource", "values", "quality" })
        { Assert.Equal(http.GetProperty(name).GetRawText(), mqtt.GetProperty(name).GetRawText()); }
    }

    private static async Task AssertMqttStopOutputResumedAsync(HttpClient api, RuntimeStateReader state, long connectionId)
    {
        var control = await api.GetJsonAsync($"{Api}/connections/{connectionId}/delivery-control", Token);
        Assert.False(control.GetProperty("manuallyPaused").GetBoolean());
        var target = connectionId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(target, Assert.Single(state.Audit("delivery.pause")).Target);
        Assert.Equal(target, Assert.Single(state.Audit("delivery.resume")).Target);
    }

    private sealed record MqttFanOutRow(DeliveryRow Delivery, string Topic, int Qos);
}
