using OpenLineOps.Runtime.Application.Safety;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresStationEmergencyStopRepositoryIntegrationTests(
    PostgresContainerFixture fixture)
{
    private static readonly DateTimeOffset RequestedAtUtc =
        new(2026, 7, 11, 9, 45, 0, TimeSpan.Zero);

    [PostgresIntegrationFact]
    public async Task RequestFailureAndAcknowledgementEvidenceSurviveColdRestart()
    {
        var unique = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid().ToString("D");
        var request = new StationEmergencyStopRequestEvidence(
            unique,
            idempotencyKey,
            $"project.{unique:N}",
            $"application.{unique:N}",
            $"snapshot.{unique:N}",
            $"station-system.{unique:N}",
            $"agent.{unique:N}",
            $"station.{unique:N}",
            [],
            "operator.postgres",
            "PostgreSQL safety evidence integration.",
            RequestedAtUtc);
        using (var repository = new PostgreSqlStationEmergencyStopRepository(
                   fixture.ConnectionString))
        {
            var registration = await repository.RegisterRequestAsync(request);
            Assert.Equal(StationEmergencyStopRegistrationKind.Created, registration.Kind);
            await repository.RecordDispatchFailureAsync(
                idempotencyKey,
                unique,
                "Runtime.EmergencyStopTransportUnavailable",
                "Broker was unavailable.",
                RequestedAtUtc.AddSeconds(1));
            await repository.RecordAcknowledgementAsync(
                new StationEmergencyStopAcknowledgementEvidence(
                    Guid.NewGuid(),
                    unique,
                    idempotencyKey,
                    request.AgentId,
                    request.StationId,
                    Accepted: true,
                    FailureCode: null,
                    FailureReason: null,
                    RequestedAtUtc.AddSeconds(2)));
        }

        using var restarted = new PostgreSqlStationEmergencyStopRepository(
            fixture.ConnectionString);
        var restored = Assert.IsType<StationEmergencyStopRecord>(
            await restarted.GetByIdempotencyKeyAsync(idempotencyKey));
        Assert.Equal(StationEmergencyStopStatus.Acknowledged, restored.Status);
        Assert.Equal(2, restored.DispatchAttemptCount);
        Assert.Collection(
            restored.Evidence,
            evidence => Assert.Equal(
                StationSafetyEvidenceKind.EmergencyStopRequested,
                evidence.Kind),
            evidence => Assert.Equal(
                StationSafetyEvidenceKind.EmergencyStopDispatchFailed,
                evidence.Kind),
            evidence => Assert.Equal(
                StationSafetyEvidenceKind.EmergencyStopAcknowledged,
                evidence.Kind));
        var listed = Assert.Single(await restarted.ListAsync(
            new StationEmergencyStopQuery(
                request.ProjectId,
                request.ApplicationId,
                request.ProjectSnapshotId,
                request.StationSystemId)));
        Assert.Equal(idempotencyKey, listed.Request.IdempotencyKey);

        var replay = await restarted.RegisterRequestAsync(request);
        Assert.Equal(StationEmergencyStopRegistrationKind.Replay, replay.Kind);
        await Assert.ThrowsAsync<StationEmergencyStopIdempotencyConflictException>(async () =>
            await restarted.RegisterRequestAsync(request with
            {
                Reason = "Different immutable evidence."
            }));
    }
}
