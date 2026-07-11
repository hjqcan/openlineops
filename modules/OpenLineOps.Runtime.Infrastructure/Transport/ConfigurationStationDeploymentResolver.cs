using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class ConfigurationStationDeploymentResolver : IStationDeploymentResolver
{
    private readonly Dictionary<DeploymentKey, StationDeploymentRoute> _routes;

    public ConfigurationStationDeploymentResolver(StationCoordinatorTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var routes = new Dictionary<DeploymentKey, StationDeploymentRoute>();
        foreach (var deployment in options.Deployments)
        {
            var key = new DeploymentKey(
                Required(deployment.ProjectId, nameof(deployment.ProjectId)),
                Required(deployment.ApplicationId, nameof(deployment.ApplicationId)),
                Required(deployment.ProjectSnapshotId, nameof(deployment.ProjectSnapshotId)),
                Required(deployment.StationSystemId, nameof(deployment.StationSystemId)));
            var route = new StationDeploymentRoute(
                Required(deployment.AgentId, nameof(deployment.AgentId)),
                Required(deployment.StationId, nameof(deployment.StationId)),
                deployment.PackageContentSha256);
            if (!routes.TryAdd(key, route))
            {
                throw new InvalidOperationException(
                    $"Station deployment mapping '{key}' is duplicated.");
            }
        }

        _routes = routes;
    }

    public ValueTask<StationDeploymentRoute> ResolveAsync(
        StationDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var key = new DeploymentKey(
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.StationSystemId);
        return _routes.TryGetValue(key, out var route)
            ? ValueTask.FromResult(route)
            : throw new InvalidOperationException(
                $"No signed package deployment maps Project/Application/Snapshot/Station '{key}'.");
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidOperationException(
                $"Station deployment {name} must be canonical non-empty text.")
            : value;

    private sealed record DeploymentKey(
        string ProjectId,
        string ApplicationId,
        string ProjectSnapshotId,
        string StationSystemId)
    {
        public override string ToString() =>
            $"{ProjectId}/{ApplicationId}/{ProjectSnapshotId}/{StationSystemId}";
    }
}
