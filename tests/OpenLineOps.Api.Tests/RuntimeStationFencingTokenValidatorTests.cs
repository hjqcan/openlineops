using OpenLineOps.Api.Integrations;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeStationFencingTokenValidatorTests
{
    [Fact]
    public async Task AcceptsOnlyRepositoryValidatedCurrentStationLease()
    {
        var lease = Lease("station-a", fencingToken: 12);
        var repository = new StubResourceLeaseRepository(
            [lease],
            ResourceLeaseFenceValidationResult.Accept());
        var validator = new RuntimeStationFencingTokenValidator(repository);

        var accepted = await validator.IsCurrentAsync("station-a", 12);

        Assert.True(accepted);
        Assert.Equal(1, repository.ValidationCalls);
        Assert.Equal(lease.ProductionRunId, repository.ValidatedRunId);
        Assert.Equal(lease.OperationRunId, repository.ValidatedOperationRunId);
        var evidence = Assert.Single(repository.ValidatedEvidence!);
        Assert.Equal(lease.Resource, evidence.Resource);
        Assert.Equal(lease.FencingToken, evidence.FencingToken);
        Assert.Equal(lease.ExpiresAtUtc, evidence.ExpiresAtUtc);
    }

    [Fact]
    public async Task RejectsMissingWrongKindWrongTokenAndAmbiguousLeases()
    {
        var station = Lease("station-a", fencingToken: 12);
        var device = new ResourceLease(
            new ResourceRequirement(ResourceKind.Device, "station-a"),
            station.ProductionRunId,
            station.OperationRunId,
            station.FencingToken,
            station.AcquiredAtUtc,
            station.ExpiresAtUtc);
        var repository = new StubResourceLeaseRepository(
            [station, station, device],
            ResourceLeaseFenceValidationResult.Accept());
        var validator = new RuntimeStationFencingTokenValidator(repository);

        Assert.False(await validator.IsCurrentAsync("station-a", 12));
        Assert.False(await validator.IsCurrentAsync("station-a", 11));
        Assert.False(await validator.IsCurrentAsync("station-missing", 12));
        Assert.Equal(0, repository.ValidationCalls);
    }

    [Fact]
    public async Task RejectsLeaseWhenAuthoritativeValidationRejectsIt()
    {
        var lease = Lease("station-a", fencingToken: 12);
        var repository = new StubResourceLeaseRepository(
            [lease],
            ResourceLeaseFenceValidationResult.Reject("expired"));
        var validator = new RuntimeStationFencingTokenValidator(repository);

        Assert.False(await validator.IsCurrentAsync("station-a", 12));
        Assert.Equal(1, repository.ValidationCalls);
    }

    private static ResourceLease Lease(string stationId, long fencingToken)
    {
        var acquiredAtUtc = new DateTimeOffset(
            2026,
            7,
            31,
            8,
            0,
            0,
            TimeSpan.Zero);
        return new ResourceLease(
            new ResourceRequirement(ResourceKind.Station, stationId),
            new ProductionRunId(Guid.NewGuid()),
            "operation-a",
            fencingToken,
            acquiredAtUtc,
            acquiredAtUtc.AddMinutes(5));
    }

    private sealed class StubResourceLeaseRepository(
        IReadOnlyCollection<ResourceLease> leases,
        ResourceLeaseFenceValidationResult validation)
        : IResourceLeaseRepository
    {
        public int ValidationCalls { get; private set; }

        public ProductionRunId? ValidatedRunId { get; private set; }

        public string? ValidatedOperationRunId { get; private set; }

        public IReadOnlyCollection<ResourceLeaseFenceEvidence>? ValidatedEvidence
        {
            get;
            private set;
        }

        public ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(leases);
        }

        public ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceRequirement> resources,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidationCalls++;
            ValidatedRunId = runId;
            ValidatedOperationRunId = operationRunId;
            ValidatedEvidence = evidence;
            return ValueTask.FromResult(validation);
        }

        public ValueTask ReleaseAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseReleaseClaim> claims,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask HoldForRecoveryAsync(
            ProductionRunId runId,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ReleaseRecoveryHoldAsync(
            ProductionRunId runId,
            IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
