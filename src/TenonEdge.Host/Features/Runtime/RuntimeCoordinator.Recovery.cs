using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    // Local preparation only, no input activation, output connection or snapshot allocation.
    public async Task<(ConfigurationRecoveryReview Review, RuntimeRecoveryAuthorization Authorization)> ReviewRecoveryAsync(
        long targetRevision, string draftVersion, ManagementActor actor, CancellationToken token)
    {
        RequireRecoveryAvailable();
        token.ThrowIfCancellationRequested();
        var checkpoint = versions.ReadRecovery(targetRevision, draftVersion);
        await using var prepared = runtime.Prepare(checkpoint.Plan.ToPlan(checked(checkpoint.EffectiveRevision + 1)));
        var ids = checkpoint.Plan.Units.Select(unit => unit.Id.Kind switch
        {
            "output.http" => (long?)HttpOutputSettings.Parse(unit.Configuration).ConnectionId,
            "output.mqtt" => MqttOutputSettings.Parse(unit.Configuration).ConnectionId,
            _ => null,
        }).Where(id => id is not null).Select(id => id!.Value).Distinct().Order().ToArray();
        var targets = new List<RuntimeRecoveryTarget>();
        var problems = prepared.Failures.Select(failure => failure.Id.ToString()).ToList();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var identity = prepared.CaptureMigrationTarget(id);
            if (identity is null || !identity.Recipient.AccountRecorded)
            { problems.Add($"connection:{id}"); }
            else
            { targets.Add(new(id, identity)); }
        }
        var approval = new RuntimeRecoveryAuthorization(checkpoint, targets.ToArray(), actor);
        versions.ValidateRecovery(approval, requireDraftVersion: true);
        var queue = versions.CheckRecoveryQueue(approval, token);
        RequireRecoveryAvailable();
        return (new(checkpoint.EffectiveRevision, targetRevision, draftVersion, RuntimeVersionStore.RecoveryConfirmation(approval),
            checkpoint.Target.ContentHash, approval.Targets, queue, problems.ToArray()), approval);
    }

    private void RequireRecoveryAvailable()
    {
        lock (_lock)
        {
            if (_state != RuntimeCoordinatorState.RunFailed || runtime.ActiveRevision is not null || _application is not null || _deliveryWrite is not null || _stopping is not null)
            { throw new RuntimeApplicationUnavailableException(); }
        }
    }

    public RuntimeApplicationStatus BeginRecovery(RuntimeRecoveryAuthorization approval, AuditAccount account, string? description, CancellationToken token)
    {
        description = ConfigurationChangeDescription.Normalize(description);
        var checkpoint = approval.Checkpoint;
        return StartOperation("recovery", checkpoint.Draft.Version, checkpoint.Target.Revision, account,
            id => ExecuteApplicationAsync(checkpoint.Target.Revision, checkpoint.Draft.Version, account, approval.Actor.SourceIp, id,
                description, new(checkpoint.EffectiveRevision, checkpoint.Plan.ContentHash), [], approval), token).Status;
    }
}
