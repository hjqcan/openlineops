using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IStationJobGateway
{
    ValueTask<StationJobCompleted> DispatchAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default);
}

public interface IStationJobCancellationGateway
{
    ValueTask<StationJobCancelAcknowledged> RequestCancelAsync(
        StationJobCancelRequested request,
        CancellationToken cancellationToken = default);
}

public interface IStationDeploymentResolver
{
    ValueTask<StationDeploymentRoute> ResolveAsync(
        StationDeploymentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StationDeploymentRequest(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string StationSystemId);

public sealed record StationDeploymentRoute
{
    public StationDeploymentRoute(
        string agentId,
        string stationId,
        string packageContentSha256,
        string productionLineDefinitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageContentSha256);
        if (packageContentSha256.Length != 64
            || packageContentSha256.Any(character => character is not (>= '0' and <= '9'
                or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Package content SHA-256 must be canonical lowercase hexadecimal.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(productionLineDefinitionId);
        if (char.IsWhiteSpace(productionLineDefinitionId[0])
            || char.IsWhiteSpace(productionLineDefinitionId[^1]))
        {
            throw new ArgumentException(
                "Production Line definition ID must be canonical text.",
                nameof(productionLineDefinitionId));
        }

        AgentId = agentId;
        StationId = stationId;
        PackageContentSha256 = packageContentSha256;
        ProductionLineDefinitionId = productionLineDefinitionId;
    }

    public string AgentId { get; }

    public string StationId { get; }

    public string PackageContentSha256 { get; }

    public string ProductionLineDefinitionId { get; }
}
