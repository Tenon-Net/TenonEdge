namespace TenonEdge.Abstractions.Messages;

/// <summary>输入采样时的诊断信息，不含主题、地址或凭证；null 告警表示未知，而非确认正常。</summary>
public sealed record InputSampleMetadata(bool? InvalidUpstreamTime, MqttSampleMetadata? Mqtt = null);

public sealed record MqttSampleMetadata
{
    public MqttSampleMetadata(bool retained, bool duplicate, int qos)
    {
        if (qos is not (0 or 1))
        { throw new ArgumentOutOfRangeException(nameof(qos)); }
        Retained = retained;
        Duplicate = duplicate;
        Qos = qos;
    }

    public bool Retained { get; }
    public bool Duplicate { get; }
    public int Qos { get; }
}
