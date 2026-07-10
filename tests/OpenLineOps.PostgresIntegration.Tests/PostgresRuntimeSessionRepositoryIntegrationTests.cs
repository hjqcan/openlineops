using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresRuntimeSessionRepositoryIntegrationTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _postgres;

    public PostgresRuntimeSessionRepositoryIntegrationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [PostgresIntegrationFact]
    public async Task SaveAsyncPersistsRuntimeSessionGraphAndRecoveryCandidates()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var session = CreateRunningSession(suffix, BaseTimeUtc);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node-inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("node-inspect:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.inspect"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("vision-camera"),
            "Inspect",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(30));
        session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(4));
        session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(5));
        session.CompleteCommand(command.Id, "{\"ok\":true}", BaseTimeUtc.AddSeconds(6));
        session.CompleteStep(step.Id, BaseTimeUtc.AddSeconds(7));
        var incident = session.RecordIncident(
            RuntimeIncidentSeverity.Warning,
            "Runtime.CameraTemperatureHigh",
            "Camera temperature is above nominal range.",
            BaseTimeUtc.AddSeconds(8));

        await using (var repository = new PostgresRuntimeSessionRepository(_postgres.ConnectionString))
        {
            await repository.SaveAsync(session);
        }

        await using var restartedRepository = new PostgresRuntimeSessionRepository(_postgres.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(session.Id);
        var recoveryPlan = await new RuntimeSessionRecoveryService(restartedRepository)
            .CreateRecoveryPlanAsync();

        Assert.NotNull(restored);
        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(RuntimeSessionStatus.Running, restored.Status);
        Assert.Equal($"snapshot-{suffix}", restored.ConfigurationSnapshotId.Value);
        Assert.Equal(BaseTimeUtc.AddSeconds(1), restored.StartedAtUtc);
        Assert.Empty(restored.DomainEvents);
        Assert.Contains(recoveryPlan.Candidates, candidate => candidate.SessionId == session.Id);

        var restoredStep = Assert.Single(restored.Steps);
        Assert.Equal(step.Id, restoredStep.Id);
        Assert.Equal(RuntimeStepStatus.Completed, restoredStep.Status);

        var restoredCommand = Assert.Single(restored.Commands);
        Assert.Equal(command.Id, restoredCommand.Id);
        Assert.Equal(RuntimeCommandStatus.Completed, restoredCommand.Status);
        Assert.Equal("{\"ok\":true}", restoredCommand.ResultPayload);

        var restoredIncident = Assert.Single(restored.Incidents);
        Assert.Equal(incident.Id, restoredIncident.Id);
        Assert.Equal(RuntimeIncidentSeverity.Warning, restoredIncident.Severity);
    }

    private static RuntimeSession CreateRunningSession(string suffix, DateTimeOffset createdAtUtc)
    {
        var session = RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId($"station-{suffix}"),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId($"process-packaging@{suffix}"),
            new ConfigurationSnapshotId($"snapshot-{suffix}"),
            new RecipeSnapshotId($"recipe-{suffix}"),
            createdAtUtc,
            new RuntimeSessionTraceMetadata(
                null,
                null,
                null,
                null,
                "postgres-integration-tests",
                $"project-{suffix}",
                $"application-{suffix}",
                $"release-{suffix}",
                $"topology-{suffix}"));

        var result = session.Start(createdAtUtc.AddSeconds(1));
        Assert.True(result.Succeeded, result.Message);

        return session;
    }
}
