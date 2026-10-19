using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ExtensionOutputRuntimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimeUnitPlan Input() => new(RuntimeUnitId.Create("input.testscalar", "meter/source"),
        JsonSerializer.Deserialize<JsonElement>("""{"inputModule":{"version":3,"parameters":{"value":9007199254740993}}}"""));

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton(TestWireOutputModule.Definition);
        services.AddSingleton<IOutputModuleFactory, TestWireOutputModule>();
        services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { UsesDurableHandoff = true });
        services.AddSingleton<TestWireOutputTransport>();
        services.AddSingleton<IOutputAdapterFactory>(provider => provider.GetRequiredService<TestWireOutputTransport>());
    }

    [Fact]
    public async Task PreparationRejectsTransportWhoseActualRecipientDiffersFromFrozenRoute()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: Register);
        var factory = host.Services.GetRequiredService<TestWireOutputTransport>();
        factory.RecipientOverride = "wire://127.0.0.1:19002";
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        await using var prepared = runtime.Prepare(new(1, [Input(), new TestWireOutputModule().Unit()]));
        Assert.Single(prepared.Failures);
        Assert.Equal(0, factory.Adapters);
        Assert.Equal(0, factory.Sessions);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("recipient")]
    [InlineData("protocol")]
    [InlineData("summary")]
    [InlineData("fingerprint")]
    public async Task MessageOutsideFrozenTransportContractIsRejectedBeforeDurableHandoff(string invalid)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: Register);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        switch (invalid)
        {
            case "address":
                transport.MessageAddressOverride = "other/meter";
                break;
            case "recipient":
                transport.MessageSummaryOverride = OutputRecipientIdentity.Extension("wire", "wire://127.0.0.1:19002", "none", null).Freeze();
                break;
            case "protocol":
                transport.MessageSummaryOverride = OutputRecipientIdentity.Extension("other", "other://127.0.0.1:19001", "none", null).Freeze();
                break;
            case "summary":
                transport.MessageSummaryOverride = "unknown frozen identity";
                break;
            case "fingerprint":
                transport.MessageFingerprintOverride = " ";
                break;
        }
        host.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "test", null);
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        await using var prepared = runtime.Prepare(new(1, [Input(), new TestWireOutputModule().Unit()]));
        Assert.Empty(prepared.Failures);
        await runtime.ActivateAsync(prepared);
        var input = Assert.IsType<TestScalarInputModule>(Assert.Single(host.Services.GetServices<IInputModuleFactory>()));
        Assert.Equal(InputHandoffStatus.Rejected, await input.Handoff.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task TwoOutputPathsUseOneOriginalDurableSenderAndOneActualTcpSession()
    {
        using var data = TemporaryDirectory.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) }, Register);
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var factory = host.Services.GetRequiredService<TestWireOutputTransport>();
        var module = new TestWireOutputModule();
        await using var prepared = runtime.Prepare(new(1, [Input(), module.Unit(port), module.Unit(port, "second/{deviceKey}", key: "route/second")]));
        Assert.Empty(prepared.Failures);
        Assert.Equal(0, factory.Adapters);
        Assert.False(server.Pending());
        await runtime.ActivateAsync(prepared);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = await server.AcceptTcpClientAsync(deadline.Token);
        var stream = client.GetStream();
        var first = await ReadFrameAsync(stream, deadline.Token);
        var pending = runtime.Deliveries.ReadPending(1, 0, 10);
        Assert.Equal(2, pending.Count); // Neither delivery may disappear before a real wire ACK.
        Assert.Equal(pending[0].Payload.ToArray(), first.Payload);
        Assert.Equal("events/meter", first.Address);
        using var firstJson = JsonDocument.Parse(first.Payload);
        Assert.Equal("9007199254740993", firstJson.RootElement.GetProperty("values").GetProperty("value").GetRawText());
        Assert.Equal(pending[0].DeliveryId, firstJson.RootElement.GetProperty("messageId").GetGuid());
        await stream.WriteAsync(new byte[] { 6 }, deadline.Token);
        var second = await ReadFrameAsync(stream, deadline.Token);
        Assert.Equal("second/meter", second.Address);
        Assert.Equal(pending[1].Payload.ToArray(), second.Payload);
        using var secondJson = JsonDocument.Parse(second.Payload);
        Assert.Equal(firstJson.RootElement.GetProperty("sourceMessageId").GetGuid(), secondJson.RootElement.GetProperty("sourceMessageId").GetGuid());
        Assert.NotEqual(firstJson.RootElement.GetProperty("messageId").GetGuid(), secondJson.RootElement.GetProperty("messageId").GetGuid());
        await stream.WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 2);
        Assert.Equal(1, factory.Adapters);
        Assert.Equal(1, factory.Sessions);
        Assert.False(server.Pending());
        await runtime.StopAsync(deadline.Token);
        Assert.Equal(1, factory.DisposedSessions);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
    }

    private sealed record WireFrame(string Address, byte[] Payload);
    private static async Task<WireFrame> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var addressLength = BinaryPrimitives.ReadInt32BigEndian(header);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        Assert.InRange(addressLength, 0, 4096);
        Assert.InRange(payloadLength, 1, 256 * 1024);
        var address = new byte[addressLength];
        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(address, token);
        await stream.ReadExactlyAsync(payload, token);
        return new(Encoding.UTF8.GetString(address), payload);
    }
}
