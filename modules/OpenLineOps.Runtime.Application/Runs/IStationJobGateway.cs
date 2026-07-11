using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IStationJobGateway
{
    ValueTask<StationJobCompleted> DispatchAsync(
        StationJobRequested request,
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
        string packageContentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        if (packageContentSha256.Length != 64
            || packageContentSha256.Any(character => character is not (>= '0' and <= '9'
                or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Package content SHA-256 must be canonical lowercase hexadecimal.");
        }

        AgentId = agentId;
        StationId = stationId;
        PackageContentSha256 = packageContentSha256;
    }

    public string AgentId { get; }

    public string StationId { get; }

    public string PackageContentSha256 { get; }
}
