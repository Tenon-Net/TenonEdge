using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeBuildDiagnosticsTests
{
    private const string Secret = "artificial-password-build-marker";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FactoryFailuresDoNotExposeExceptionDetails(bool output, bool expected)
    {
        using var data = TemporaryDirectory.Create();
        using var logs = new LogCapture();
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([new FailingInput(expected)], data.Path,
            events: events, outputs: [new FailingOutput(expected)], logger: logs.CreateLogger<RuntimeHost>());
        var input = OutputPipelineTests.Input();
        var definition = output
            ? new RuntimeUnitPlan(RuntimeUnitId.Create("output.test", "out"), input.Configuration)
            : input;
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(definition));
        Assert.DoesNotContain(Secret, Assert.Single(plan.Failures).Reason);
        await runtime.ActivateAsync(plan);
        var status = Assert.Single(runtime.GetStatus().Units);
        Assert.Equal(RuntimeUnitState.BuildFailed, status.State);
        Assert.DoesNotContain(Secret, status.LastError ?? "");
        Assert.DoesNotContain(Secret, Assert.Single(events.OfType(RuntimeEventTypes.UnitBuildFailed)).Description);
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(Secret, entry.Message);
            Assert.Null(entry.ExceptionText);
        });
    }

    private static Exception Failure(RuntimeUnitId id, bool expected) => expected
        ? new RuntimeUnitBuildException(id, Secret)
        : new InvalidOperationException(Secret, new IOException(Secret));

    private sealed class FailingInput(bool expected) : IInputUnitFactory
    {
        public string Kind => "input.test";
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => throw Failure(unit.Id, expected);
    }

    private sealed class FailingOutput(bool expected) : IOutputAdapterFactory
    {
        public string Kind => "output.test";
        public IOutputTarget Build(RuntimeUnitPlan plan) => throw Failure(plan.Id, expected);
    }
}
