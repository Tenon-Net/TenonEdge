using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Theory]
    [InlineData(HttpCredentialKind.Basic, "safe")]
    [InlineData(HttpCredentialKind.Bearer, "safe")]
    [InlineData(HttpCredentialKind.ApiKeyHeader, "safe")]
    [InlineData(HttpCredentialKind.ApiKeyQuery, "safe")]
    [InlineData(HttpCredentialKind.Basic, "echo")]
    [InlineData(HttpCredentialKind.Bearer, "echo")]
    [InlineData(HttpCredentialKind.ApiKeyHeader, "echo")]
    [InlineData(HttpCredentialKind.ApiKeyQuery, "echo")]
    [InlineData(HttpCredentialKind.Basic, "slow")]
    [InlineData(HttpCredentialKind.Bearer, "slow")]
    [InlineData(HttpCredentialKind.ApiKeyHeader, "slow")]
    [InlineData(HttpCredentialKind.ApiKeyQuery, "slow")]
    [InlineData(HttpCredentialKind.Basic, "disconnect")]
    [InlineData(HttpCredentialKind.Bearer, "disconnect")]
    [InlineData(HttpCredentialKind.ApiKeyHeader, "disconnect")]
    [InlineData(HttpCredentialKind.ApiKeyQuery, "disconnect")]
    public async Task RealPeerSummaryUsesThisRequestsCredentialAndSurvivesRestartWithoutChangingFailureClass(HttpCredentialKind kind, string scenario)
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var credentials = new Credentials
        {
            Value = new(new(kind, kind == HttpCredentialKind.Basic ? "user" : null,
            kind is HttpCredentialKind.ApiKeyHeader or HttpCredentialKind.ApiKeyQuery ? "X-Key" : null, "account", "v1"), "fixture-first-secret")
        };
        var events = new RecordingEventSink();
        var status = scenario == "slow" ? 401 : scenario == "disconnect" ? 503 : 422;
        const string safeBody = "<b>field must be integer</b> 9007199254740993";
        var echo = kind == HttpCredentialKind.Basic ? Convert.ToBase64String(Encoding.UTF8.GetBytes("user:fixture-first-secret")) : "fixture-first-secret";
        var bodyBytes = Encoding.UTF8.GetBytes(scenario == "echo" ? echo : safeBody);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await server.AcceptTcpClientAsync(Token);
            var request = await ReadAsync(socket.GetStream());
            Assert.Contains(echo, request.Header, StringComparison.Ordinal);
            // A credential change while the response is in flight must not replace the actual request's redaction context.
            if (scenario == "echo")
            { credentials.Value = credentials.Value with { Secret = "fixture-next-secret", Identity = credentials.Value.Identity with { Version = "v2" } }; }
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n"), Token);
            if (scenario == "slow")
            { await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); }
            else if (scenario != "disconnect")
            { await socket.GetStream().WriteAsync(bodyBytes, Token); }
            return request;
        }, Token);
        Guid deliveryId;
        var area = status == 422 ? DeliveryState.Failed : DeliveryState.Quarantined;
        await using (var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, events: events,
            outputs: [new HttpOutputFactory(credentials, TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections())]))
        {
            var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.test", "device/input"), JsonSerializer.SerializeToElement(new { }));
            await using var plan = runtime.Prepare(new RuntimePlan(1, [input, Plan(server, "events")]));
            Assert.Empty(plan.Failures);
            runtime.StartCore();
            try
            {
                await runtime.ActivateAsync(plan);
                await OutputPipelineTests.UntilAsync(() => status == 422 ? runtime.Deliveries.ReadFailed(1, 0, 10).Count == 1 :
                    runtime.Deliveries.ReadPending(1, 0, 10).SingleOrDefault()?.LastError is not null);
                var record = Assert.Single(status == 422 ? runtime.Deliveries.ReadFailed(1, 0, 10) : runtime.Deliveries.ReadPending(1, 0, 10));
                deliveryId = record.DeliveryId;
                Assert.Equal(0, record.FailureCount); // 422 rejects; 401 suspends; 503 does not poison.
                Assert.Equal(status == 422 ? 1 : 0, runtime.Deliveries.GetFailureTotals(1).Rejected);
                Assert.DoesNotContain("field must be integer", JsonSerializer.Serialize(runtime.GetStatus()), StringComparison.Ordinal);
                Assert.DoesNotContain("fixture-first-secret", JsonSerializer.Serialize(events.Events), StringComparison.Ordinal);
                Assert.DoesNotContain("field must be integer", JsonSerializer.Serialize(events.Events), StringComparison.Ordinal);
                if (status == 401)
                { Assert.Contains("HTTP 401", record.LastError, StringComparison.Ordinal); }
                if (status != 422)
                { await runtime.Deliveries.QuarantineAsync(record.Seq, DeliveryQuarantineReasons.TargetChanged, Token); }
                var summary = runtime.Deliveries.InspectBody(deliveryId, area)!.ResponseSummary!;
                Assert.Equal(status, summary.StatusCode);
                Assert.Equal(scenario switch { "safe" => HttpResponseSummaryState.Captured, "echo" => HttpResponseSummaryState.Redacted, _ => HttpResponseSummaryState.Unavailable }, summary.State);
                Assert.Equal(scenario == "safe" ? safeBody : null, summary.Text);
            }
            finally
            {
                release.TrySetResult();
                await peer.WaitAsync(TimeSpan.FromSeconds(10), Token);
                await runtime.StopAsync(Token);
            }
        }
        await using var reopened = RuntimeTesting.CreateRuntime([], data.Path);
        reopened.StartCore();
        var saved = reopened.Deliveries.InspectBody(deliveryId, area)!.ResponseSummary!;
        Assert.Equal(status, saved.StatusCode);
        Assert.Equal(scenario == "safe" ? safeBody : null, saved.Text);
        Assert.False(server.Pending());
    }
}
