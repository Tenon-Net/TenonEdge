namespace TenonEdge.Abstractions.Messages;

/// <summary>接收时的诊断正文检查结果；只控制诊断留存，不改变业务投递。</summary>
public enum DiagnosticSampleAccess
{
    Unknown,
    Safe,
    Redacted
}
