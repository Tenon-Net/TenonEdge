using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.UnitTests.Routes;

public sealed partial class RoutePlanCompilerTests
{
    [Theory]
    [InlineData("sources", "sources.totalLimit")]
    [InlineData("paths", "paths.totalLimit")]
    [InlineData("fanout", "paths.sourceLimit")]
    [InlineData("connections", "connections.totalLimit")]
    public void NewAdmissionLimitsRejectBeforeReturningExecutablePlan(string limit, string code)
    {
        var route = Route([Input(sources: "[1,2]"), Map("a", "value", "a"), Map("b", "value", "b"), Output()],
            [new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out")]);
        var snapshot = Snapshot(route, 2);
        var connections = snapshot.Connections.ToDictionary();
        connections.Add(11, connections[10] with { Id = 11, Key = "unused-output" });
        snapshot = snapshot with { Connections = connections };
        var compiled = Compile(snapshot);
        Assert.Equal(4, compiled.Topology.Paths.Length);
        Assert.Single(compiled.Document.Units, unit => unit.Id.Key == "queue/unused-output/sender");
        var limits = limit switch
        {
            "sources" => new RouteCompilationLimits(Sources: 1),
            "paths" => new RouteCompilationLimits(TotalPaths: 3),
            "fanout" => new RouteCompilationLimits(PathsPerSource: 1),
            _ => new RouteCompilationLimits(Connections: 1),
        };
        Assert.Equal(code, Assert.Throws<RouteTopologyException>(() => Compile(snapshot with { Limits = limits })).Issue.Code);
        // 独立的冻结/COMMIT复核得到相同结论，不信任以前编译时使用的策略。
        Assert.Equal(code, Assert.Throws<RouteTopologyException>(() => limits.Check(compiled.Document)).Issue.Code);
        var exact = new RouteCompilationLimits(Sources: 2, Connections: 2, PathsPerSource: 2, TotalPaths: 4);
        exact.Check(compiled.Document);
        Assert.Equal(compiled.Document.ContentHash, Compile(snapshot with { Limits = exact }).Document.ContentHash);
        // 新准入上限不是快照格式规则；同一已存内容仍可校验/加载。
        Assert.Equal(compiled.Document.ContentHash, RuntimePlanDocument.Parse(compiled.Document.Canonical).ContentHash);
    }

    [Theory]
    [InlineData("sources", 0)]
    [InlineData("sources", 2001)]
    [InlineData("connections", 0)]
    [InlineData("connections", 501)]
    [InlineData("modbus", 0)]
    [InlineData("modbus", 257)]
    [InlineData("fanout", 0)]
    [InlineData("fanout", 33)]
    [InlineData("paths", 0)]
    [InlineData("paths", 20001)]
    public void LimitsCannotRemoveOrExceedExistingSafetyCaps(string limit, int value)
    {
        var limits = limit switch
        {
            "sources" => new RouteCompilationLimits(Sources: value),
            "connections" => new RouteCompilationLimits(Connections: value),
            "modbus" => new RouteCompilationLimits(ModbusConnections: value),
            "fanout" => new RouteCompilationLimits(PathsPerSource: value),
            _ => new RouteCompilationLimits(TotalPaths: value),
        };
        Assert.Throws<ArgumentException>(limits.Validate);
    }

    [Fact]
    public void ModbusLimitCountsPhysicalConnectionsNotGroups()
    {
        var plan = RuntimePlanDocument.Parse("""
            {"schemaVersion":1,"units":[
              {"kind":"input.modbus","key":"a/one","configuration":{"connectionId":1,"host":"device.local","unitId":1,"points":[{"key":"v","functionCode":3,"address":0,"type":"uint16"}]}},
              {"kind":"input.modbus","key":"a/two","configuration":{"connectionId":1,"host":"device.local","unitId":1,"points":[{"key":"other","functionCode":3,"address":1,"type":"uint16"}]}},
              {"kind":"input.modbus","key":"b/one","configuration":{"connectionId":2,"host":"other.local","unitId":1,"points":[{"key":"v","functionCode":3,"address":0,"type":"uint16"}]}}
            ]}
            """u8);
        new RouteCompilationLimits(Sources: 3, Connections: 2, ModbusConnections: 2).Check(plan);
        Assert.Equal("connections.modbusLimit", Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(ModbusConnections: 1).Check(plan)).Issue.Code);
        Assert.Equal("connections.totalLimit", Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(Connections: 1).Check(plan)).Issue.Code);
    }
}
