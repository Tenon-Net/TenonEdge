using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Modbus;

/// <summary>建连不等于从站可读；reused 仅表示借用了现有流，没有重新检测远端存活。</summary>
public sealed record ModbusConnectionTestResult(bool Success, string Code, double ElapsedMs, ConnectionTestStep[] Steps);
