using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresResourceLeaseProjectionIntegrationTests(
    PostgresContainerFixture fixture)
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [PostgresIntegrationFact]
    public async Task ResourceLeaseListRebuildsOwnerExpiryAndFencingTokenAfterRestart()
    {
        var unique = Guid.NewGuid().ToString("N");
        var runId = ProductionRunId.New();
        var operationRunId = $"operation-{unique}@0001";
        var resource = new ResourceRequirement(ResourceKind.Device, $"device-{unique}");
        ResourceLease acquired;
        using (var repository = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            acquired = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await repository.TryAcquireAsync(
                    runId,
                    operationRunId,
                    [resource],
                    BaseTimeUtc,
                    TimeSpan.FromMinutes(5))));
        }

        using var restarted = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        var restored = Assert.Single(
            await restarted.ListAsync(),
            lease => lease.Resource == resource);

        Assert.Equal(runId, restored.ProductionRunId);
        Assert.Equal(operationRunId, restored.OperationRunId);
        Assert.Equal(acquired.FencingToken, restored.FencingToken);
        Assert.Equal(BaseTimeUtc, restored.AcquiredAtUtc);
        Assert.Equal(BaseTimeUtc.AddMinutes(5), restored.ExpiresAtUtc);
        await restarted.ReleaseAsync(runId, operationRunId);
    }
}
