using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Safety;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationEmergencyStopServiceTests
{
    private static readonly DateTimeOffset RequestedAtUtc =
        new(2026, 7, 11, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task TerminalReplayReturnsOriginalEvidenceWithoutDispatchingAgain()
    {
        var repository = new InMemoryStationEmergencyStopRepository();
        var gateway = new RecordingGateway(Acknowledge);
        var service = CreateService(repository, gateway);
        var request = Request();

        var first = await service.RequestAsync(request);
        var replay = await service.RequestAsync(request);

        Assert.Equal(StationEmergencyStopStatus.Acknowledged, first.Record.Status);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.False(replay.DispatchAttempted);
        Assert.Equal(first.Record, replay.Record);
        Assert.Equal(1, gateway.InvocationCount);
        Assert.Collection(
            replay.Record.Evidence,
            evidence => Assert.Equal(
                StationSafetyEvidenceKind.EmergencyStopRequested,
                evidence.Kind),
            evidence => Assert.Equal(
                StationSafetyEvidenceKind.EmergencyStopAcknowledged,
                evidence.Kind));
    }

    [Fact]
    public async Task IdempotencyKeyOrMessageReuseWithDifferentEvidenceIsRejected()
    {
        var repository = new InMemoryStationEmergencyStopRepository();
        var gateway = new RecordingGateway(Acknowledge);
        var service = CreateService(repository, gateway);
        var request = Request();
        await service.RequestAsync(request);

        await Assert.ThrowsAsync<StationEmergencyStopIdempotencyConflictException>(async () =>
            await service.RequestAsync(request with { Reason = "Different physical evidence." }));
        await Assert.ThrowsAsync<StationEmergencyStopIdempotencyConflictException>(async () =>
            await service.RequestAsync(request with
            {
                IdempotencyKey = "22222222-2222-2222-2222-222222222222"
            }));
        Assert.Equal(1, gateway.InvocationCount);
    }

    [Fact]
    public async Task AcknowledgementIdentityMismatchRemainsPendingAndIsAudited()
    {
        var repository = new InMemoryStationEmergencyStopRepository();
        var gateway = new RecordingGateway(request => Acknowledge(request) with
        {
            StationId = "station.spoofed"
        });
        var service = CreateService(repository, gateway);

        var submission = await service.RequestAsync(Request());

        Assert.Equal(StationEmergencyStopStatus.Pending, submission.Record.Status);
        Assert.Null(submission.Record.Acknowledgement);
        Assert.Equal(1, submission.Record.DispatchAttemptCount);
        var failure = Assert.Single(
            submission.Record.Evidence,
            evidence => evidence.Kind == StationSafetyEvidenceKind.EmergencyStopDispatchFailed);
        Assert.Equal("Runtime.EmergencyStopAcknowledgementMismatch", failure.FailureCode);
    }

    [Fact]
    public async Task TransportFailureCanColdRetrySameMessageFromSqliteEvidence()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-emergency-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "runtime.sqlite");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var request = Request();
        try
        {
            using (var firstRepository = new SqliteStationEmergencyStopRepository(connectionString))
            {
                var failed = await CreateService(
                    firstRepository,
                    new RecordingGateway(_ => throw new IOException("broker unavailable")))
                    .RequestAsync(request);
                Assert.Equal(StationEmergencyStopStatus.Pending, failed.Record.Status);
                Assert.Equal(1, failed.Record.DispatchAttemptCount);
                Assert.Equal(2, failed.Record.Evidence.Count);
            }

            using (var restartedRepository = new SqliteStationEmergencyStopRepository(connectionString))
            {
                var gateway = new RecordingGateway(Acknowledge);
                var recovered = await CreateService(restartedRepository, gateway)
                    .RequestAsync(request);
                Assert.Equal(StationEmergencyStopStatus.Acknowledged, recovered.Record.Status);
                Assert.True(recovered.Replayed);
                Assert.Equal(2, recovered.Record.DispatchAttemptCount);
                Assert.Equal(3, recovered.Record.Evidence.Count);
                Assert.Equal(request.MessageId, Assert.Single(gateway.Requests).MessageId);

                var coldRead = await restartedRepository.GetByIdempotencyKeyAsync(
                    request.IdempotencyKey);
                Assert.NotNull(coldRead);
                Assert.True(StationSafetyCanonical.SameRequest(
                    recovered.Record.Request,
                    coldRead.Request));
                Assert.Equal(recovered.Record.Status, coldRead.Status);
                Assert.Equal(recovered.Record.Acknowledgement, coldRead.Acknowledgement);
                Assert.Equal(
                    recovered.Record.Evidence.ToArray(),
                    coldRead.Evidence.ToArray());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownDeploymentFailsClosedBeforeGatewayOrAudit()
    {
        var repository = new InMemoryStationEmergencyStopRepository();
        var gateway = new RecordingGateway(Acknowledge);
        var service = new StationEmergencyStopService(
            repository,
            new RejectingDeploymentResolver(),
            gateway,
            new ExplicitStationEmergencyStopOperatorAuthorizer(),
            new NoActiveProductionRunLinker(),
            new TestClock(RequestedAtUtc.AddSeconds(5)));

        await Assert.ThrowsAsync<StationEmergencyStopDeploymentException>(async () =>
            await service.RequestAsync(Request()));

        Assert.Equal(0, gateway.InvocationCount);
        Assert.Null(await repository.GetByIdempotencyKeyAsync(Request().IdempotencyKey));
    }

    [Fact]
    public async Task DefaultOrFutureRequestTimestampFailsBeforeAuthorizationAndDispatch()
    {
        var repository = new InMemoryStationEmergencyStopRepository();
        var gateway = new RecordingGateway(Acknowledge);
        var service = CreateService(repository, gateway);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.RequestAsync(Request() with { RequestedAtUtc = default }));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.RequestAsync(Request() with
            {
                RequestedAtUtc = RequestedAtUtc.AddMinutes(1)
            }));

        Assert.Equal(0, gateway.InvocationCount);
    }

    [Fact]
    public async Task ActiveProductionRunLinksAreFrozenIntoSafetyAndTraceEvidence()
    {
        var runId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var repository = new InMemoryStationEmergencyStopRepository();
        var gateway = new RecordingGateway(Acknowledge);
        var service = new StationEmergencyStopService(
            repository,
            new DeploymentResolver(),
            gateway,
            new ExplicitStationEmergencyStopOperatorAuthorizer(),
            new FixedProductionRunLinker(runId),
            new TestClock(RequestedAtUtc.AddSeconds(10)));

        var submission = await service.RequestAsync(Request());

        Assert.Equal([runId], submission.Record.Request.RelatedProductionRunIds);
        Assert.Equal(
            [runId],
            (await repository.GetByIdempotencyKeyAsync(Request().IdempotencyKey))!
                .Request.RelatedProductionRunIds);
    }

    private static RequestStationEmergencyStop Request() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "11111111-1111-1111-1111-111111111112",
        "project.safety",
        "application.safety",
        "snapshot.safety",
        "station-system.safety",
        "operator.safety",
        "Observed smoke at the guarded cell.",
        RequestedAtUtc);

    private static StationEmergencyStopService CreateService(
        IStationEmergencyStopRepository repository,
        IStationEmergencyStopGateway gateway) => new(
        repository,
        new DeploymentResolver(),
        gateway,
        new ExplicitStationEmergencyStopOperatorAuthorizer(),
        new NoActiveProductionRunLinker(),
        new TestClock(RequestedAtUtc.AddSeconds(10)));

    private static EmergencyStopAcknowledged Acknowledge(EmergencyStopRequested request) => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        request.MessageId,
        request.IdempotencyKey,
        request.AgentId,
        request.StationId,
        Accepted: true,
        FailureCode: null,
        FailureReason: null,
        RequestedAtUtc.AddSeconds(1));

    private sealed class DeploymentResolver : IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationDeploymentRoute(
                "agent.safety",
                "station.safety",
                new string('a', 64),
                "line.safety"));
    }

    private sealed class RejectingDeploymentResolver : IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No deployment.");
    }

    private sealed class RecordingGateway(
        Func<EmergencyStopRequested, EmergencyStopAcknowledged> handler) :
        IStationEmergencyStopGateway
    {
        public List<EmergencyStopRequested> Requests { get; } = [];

        public int InvocationCount => Requests.Count;

        public ValueTask<EmergencyStopAcknowledged> RequestEmergencyStopAsync(
            EmergencyStopRequested request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(handler(request));
        }
    }

    private sealed record TestClock(DateTimeOffset UtcNow) : IClock;

    private sealed class NoActiveProductionRunLinker :
        IStationEmergencyStopProductionRunLinker
    {
        public ValueTask<IReadOnlyCollection<Guid>> ListActiveProductionRunIdsAsync(
            StationEmergencyStopAuthorization scope,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<Guid>>([]);
    }

    private sealed class FixedProductionRunLinker(params Guid[] runIds) :
        IStationEmergencyStopProductionRunLinker
    {
        public ValueTask<IReadOnlyCollection<Guid>> ListActiveProductionRunIdsAsync(
            StationEmergencyStopAuthorization scope,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<Guid>>(runIds);
    }
}
