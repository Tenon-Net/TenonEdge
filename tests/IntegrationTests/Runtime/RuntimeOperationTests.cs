using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeOperationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ValidationFailureHasTerminalStatusWithoutRevisionAndDoesNotBlockTheNextApplication()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.Null(coordinator.ApplicationStatus);
        await Assert.ThrowsAsync<DraftWriteException>(() => coordinator.ApplyDraftAsync("stale", "operator", null, Token));
        var failed = coordinator.ApplicationStatus!;
        Assert.Equal(RuntimeApplicationStage.Failed, failed.Stage);
        Assert.Equal("configuration.draftConflict", failed.ErrorCode);
        Assert.Null(failed.Revision);
        Assert.Null(failed.Result);
        Assert.NotNull(failed.FinishedAt);
        Assert.False(failed.Committed);
        var result = await coordinator.ApplyDraftAsync("0", "operator", null, Token);
        var completed = coordinator.ApplicationStatus!;
        Assert.NotEqual(failed.Id, completed.Id);
        Assert.Equal(result, completed.Result);
        Assert.Equal(RuntimeApplicationStage.Completed, completed.Stage);
        Assert.Null(completed.ErrorCode);
        Assert.True(completed.Committed);
        Assert.Equal(failed, coordinator.ReadApplication(failed.Id));
    }

    [Fact]
    public async Task CancelledBeforeAdmissionDoesNotCreateAnOperation()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.ApplyDraftAsync("0", "operator", null, cancelled.Token));
        Assert.Null(coordinator.ApplicationStatus);
        Assert.Empty(coordinator.ReadApplications());
    }

    [Fact]
    public async Task RecentOperationHistoryIsBoundedAndRemainsDistinctFromDurableVersionHistory()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Guid oldest = default;
        for (var index = 0; index < 22; index++)
        {
            // 校验失败同样保留短期结果，但不能分配持久化版本。
            await Assert.ThrowsAsync<DraftWriteException>(() => coordinator.ApplyDraftAsync("stale", "operator", null, Token));
            if (index == 0)
            { oldest = coordinator.ApplicationStatus!.Id; }
        }
        var operations = coordinator.ReadApplications();
        Assert.Equal(20, operations.Length);
        Assert.Equal(20, operations.Select(operation => operation.Id).Distinct().Count());
        Assert.Equal(coordinator.ApplicationStatus, operations[0]);
        Assert.Null(coordinator.ReadApplication(oldest));
        Assert.All(operations, operation => Assert.Equal(RuntimeApplicationStage.Failed, operation.Stage));
        Assert.Empty(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task InvalidVersionShapeIsRejectedBeforeItCanEnterRecentOperationMetadata(string version)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        await Assert.ThrowsAsync<ArgumentException>(async () => await coordinator.ApplyDraftAsync(version, "operator", null, Token));
        Assert.Empty(coordinator.ReadApplications());
    }

    [Fact]
    public async Task UnexpectedErrorsExposeOnlyAFixedCodeNotTheExceptionDetail()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var database = factory.Services.GetRequiredService<TenonEdge.Host.Storage.ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_operation BEFORE INSERT ON runtime_snapshot BEGIN SELECT RAISE(ABORT, 'fixture-sensitive-exception-detail'); END");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ApplyDraftAsync("0", "operator", null, Token));
            var failed = coordinator.ApplicationStatus!;
            Assert.Equal("runtime.applicationFailed", failed.ErrorCode);
            Assert.Null(failed.Revision);
            Assert.False(failed.Committed);
            Assert.DoesNotContain("fixture-sensitive-exception-detail", System.Text.Json.JsonSerializer.Serialize(failed), StringComparison.Ordinal);
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_operation"); }
        Assert.True((await coordinator.ApplyDraftAsync("0", "operator", null, Token)).Activated);
    }
}
