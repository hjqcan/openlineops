using Npgsql;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresResourceLeaseProjectionIntegrationTests(
    PostgresContainerFixture fixture)
{

    [PostgresIntegrationFact]
    public async Task ResourceLeaseListRebuildsOwnerExpiryAndFencingTokenAfterRestart()
    {
        var unique = Guid.NewGuid().ToString("N");
        var runId = ProductionRunId.New();
        var operationRunId = $"operation-{unique}@0001";
        var resource = new ResourceRequirement(ResourceKind.Device, $"device-{unique}");
        ResourceLease acquired;
        var beforeAcquireUtc = await ReadDatabaseNowAsync();
        using (var repository = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            acquired = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await repository.TryAcquireAsync(
                    runId,
                    operationRunId,
                    [resource],
                    TimeSpan.FromMinutes(5))));
        }
        var afterAcquireUtc = await ReadDatabaseNowAsync();

        using var restarted = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        var restored = Assert.Single(
            await restarted.ListAsync(),
            lease => lease.Resource == resource);

        Assert.Equal(runId, restored.ProductionRunId);
        Assert.Equal(operationRunId, restored.OperationRunId);
        Assert.Equal(acquired.FencingToken, restored.FencingToken);
        Assert.InRange(restored.AcquiredAtUtc, beforeAcquireUtc, afterAcquireUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), restored.ExpiresAtUtc - restored.AcquiredAtUtc);
        await restarted.ReleaseAsync(
            runId,
            operationRunId,
            [ResourceLeaseReleaseClaim.FromLease(acquired)]);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentSameOwnerAttemptsAllowOnlyOneActiveLeaseSet()
    {
        var unique = Guid.NewGuid().ToString("N");
        var runId = ProductionRunId.New();
        var operationRunId = $"operation-{unique}@0001";
        ResourceRequirement[] resources =
        [
            new(ResourceKind.Station, $"station-{unique}"),
            new(ResourceKind.Device, $"device-{unique}")
        ];
        using var first = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        using var second = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);

        var acquired = await Task.WhenAll(
            first.TryAcquireAsync(
                    runId,
                    operationRunId,
                    resources,
                    TimeSpan.FromMinutes(5))
                .AsTask(),
            second.TryAcquireAsync(
                    runId,
                    operationRunId,
                    resources.Reverse().ToArray(),
                    TimeSpan.FromHours(1))
                .AsTask());

        var winningLeaseSet = Assert.Single(acquired, static result => result is not null)!;
        Assert.Single(acquired, static result => result is null);
        var partial = await Task.WhenAll(
            first.TryAcquireAsync(
                    runId,
                    operationRunId,
                    [resources[0]],
                    TimeSpan.FromMinutes(5))
                .AsTask(),
            second.TryAcquireAsync(
                    runId,
                    operationRunId,
                    [resources[1]],
                    TimeSpan.FromMinutes(5))
                .AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(partial, Assert.Null);
        await first.ReleaseAsync(
            runId,
            operationRunId,
            winningLeaseSet.Select(ResourceLeaseReleaseClaim.FromLease).ToArray());
    }

    [PostgresIntegrationFact]
    public async Task StaleReleaseClaimCannotDeleteAReplacementLease()
    {
        var unique = Guid.NewGuid().ToString("N");
        var resource = new ResourceRequirement(ResourceKind.Device, $"device-{unique}");
        var firstRunId = ProductionRunId.New();
        var replacementRunId = ProductionRunId.New();
        const string firstOperationRunId = "operation.first@0001";
        const string replacementOperationRunId = "operation.replacement@0001";
        using var repository = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        var firstLease = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                firstRunId,
                firstOperationRunId,
                [resource],
                TimeSpan.FromMinutes(5))));
        await ExpireLeaseAsync(resource);
        var replacementLease = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await repository.TryAcquireAsync(
                    replacementRunId,
                    replacementOperationRunId,
                    [resource],
                    TimeSpan.FromMinutes(5))));

        await repository.ReleaseAsync(
            firstRunId,
            firstOperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(firstLease)]);

        var persisted = Assert.Single(
            await repository.ListAsync(),
            lease => lease.Resource == resource);
        Assert.Equal(replacementRunId, persisted.ProductionRunId);
        Assert.Equal(replacementLease.FencingToken, persisted.FencingToken);
        Assert.Null(await repository.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.third@0001",
            [resource],
            TimeSpan.FromMinutes(1)));
        await repository.ReleaseAsync(
            replacementRunId,
            replacementOperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(replacementLease)]);
    }

    [PostgresIntegrationFact]
    public async Task CoordinatorClockSkewCannotStealOrMisvalidateDatabaseLease()
    {
        var unique = Guid.NewGuid().ToString("N");
        var resource = new ResourceRequirement(ResourceKind.Station, $"station-skew-{unique}");
        using var repository = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        var databaseNowUtc = await ReadDatabaseNowAsync();
        var owner = new SkewedCoordinatorLeaseClient(repository, databaseNowUtc);
        var fastCoordinator = new SkewedCoordinatorLeaseClient(
            repository,
            databaseNowUtc.AddYears(10));
        var slowCoordinator = new SkewedCoordinatorLeaseClient(
            repository,
            databaseNowUtc.AddYears(-10));
        var ownerRunId = ProductionRunId.New();
        const string ownerOperationRunId = "operation.owner@0001";
        var lease = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await owner.TryAcquireAsync(
                ownerRunId,
                ownerOperationRunId,
                [resource],
                TimeSpan.FromMinutes(5))));

        Assert.True(fastCoordinator.LocalUtcNow > lease.ExpiresAtUtc);
        Assert.True(slowCoordinator.LocalUtcNow < lease.AcquiredAtUtc);
        var evidence = new[] { ResourceLeaseFenceEvidence.FromLease(lease) };
        Assert.True((await fastCoordinator.ValidateCurrentAsync(
            ownerRunId,
            ownerOperationRunId,
            evidence)).Accepted);
        Assert.True((await slowCoordinator.ValidateCurrentAsync(
            ownerRunId,
            ownerOperationRunId,
            evidence)).Accepted);
        Assert.Null(await fastCoordinator.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.fast@0001",
            [resource],
            TimeSpan.FromMinutes(1)));
        Assert.Null(await slowCoordinator.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.slow@0001",
            [resource],
            TimeSpan.FromMinutes(1)));
        Assert.Equal(
            lease,
            Assert.Single(await repository.ListAsync(), candidate => candidate.Resource == resource));
        var expiredAtUtc = await ExpireLeaseAsync(resource);
        Assert.False((await slowCoordinator.ValidateCurrentAsync(
            ownerRunId,
            ownerOperationRunId,
            [new ResourceLeaseFenceEvidence(resource, lease.FencingToken, expiredAtUtc)])).Accepted);
        await repository.ReleaseAsync(
            ownerRunId,
            ownerOperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(lease)]);
    }

    private async ValueTask<DateTimeOffset> ReadDatabaseNowAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT clock_timestamp();";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async ValueTask<DateTimeOffset> ExpireLeaseAsync(ResourceRequirement resource)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE olo_resource_leases
            SET expires_at_utc = clock_timestamp() - interval '1 second'
            WHERE resource_kind = @resource_kind
              AND resource_id = @resource_id
            RETURNING expires_at_utc;
            """;
        command.Parameters.AddWithValue("resource_kind", resource.Kind.ToString());
        command.Parameters.AddWithValue("resource_id", resource.ResourceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private sealed class SkewedCoordinatorLeaseClient(
        IResourceLeaseRepository leases,
        DateTimeOffset localUtcNow)
    {
        public DateTimeOffset LocalUtcNow { get; } = localUtcNow;

        public ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceRequirement> resources,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            leases.TryAcquireAsync(
                runId,
                operationRunId,
                resources,
                duration,
                cancellationToken);

        public ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
            ProductionRunId runId,
            string operationRunId,
            IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
            CancellationToken cancellationToken = default) =>
            leases.ValidateCurrentAsync(
                runId,
                operationRunId,
                evidence,
                cancellationToken);
    }

}
