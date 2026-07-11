using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class FileSystemStationDeploymentResolver : IStationDeploymentResolver
{
    private const int MaximumCatalogBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        StationPackageCanonicalization.CreateJsonOptions();
    private readonly string? _catalogDirectory;
    private readonly Dictionary<DeploymentKey, DeploymentTarget> _routes;

    public FileSystemStationDeploymentResolver(StationCoordinatorTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var routes = new Dictionary<DeploymentKey, DeploymentTarget>();
        foreach (var deployment in options.Deployments)
        {
            var key = new DeploymentKey(
                Required(deployment.ProjectId, nameof(deployment.ProjectId)),
                Required(deployment.ApplicationId, nameof(deployment.ApplicationId)),
                Required(deployment.StationSystemId, nameof(deployment.StationSystemId)));
            var target = new DeploymentTarget(
                Required(deployment.AgentId, nameof(deployment.AgentId)),
                Required(deployment.StationId, nameof(deployment.StationId)));
            if (!routes.TryAdd(key, target))
            {
                throw new InvalidOperationException(
                    $"Station deployment mapping '{key}' is duplicated.");
            }
        }

        _catalogDirectory = routes.Count == 0
            ? null
            : ExistingCatalogDirectory(options.DeploymentCatalogDirectory);
        _routes = routes;
    }

    public async ValueTask<StationDeploymentRoute> ResolveAsync(
        StationDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = new DeploymentKey(
            Required(request.ProjectId, nameof(request.ProjectId)),
            Required(request.ApplicationId, nameof(request.ApplicationId)),
            Required(request.StationSystemId, nameof(request.StationSystemId)));
        if (!_routes.TryGetValue(key, out var target))
        {
            throw new InvalidOperationException(
                $"No Station Agent deployment maps stable Project/Application/Station '{key}'.");
        }

        var catalogPath = StationPackageCanonicalization.DeploymentCatalogPath(
            _catalogDirectory!,
            key.ProjectId,
            key.ApplicationId,
            Required(request.ProjectSnapshotId, nameof(request.ProjectSnapshotId)),
            key.StationSystemId);
        var info = new FileInfo(catalogPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumCatalogBytes)
        {
            throw new InvalidDataException(
                $"Signed Station package deployment catalog '{catalogPath}' is missing or invalid.");
        }

        var bytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken)
            .ConfigureAwait(false);
        StationPackageDeployment deployment;
        try
        {
            deployment = JsonSerializer.Deserialize<StationPackageDeployment>(bytes, JsonOptions)
                ?? throw new JsonException("Station package deployment catalog is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Station package deployment catalog '{catalogPath}' is invalid JSON.",
                exception);
        }

        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(deployment, JsonOptions)))
        {
            throw new InvalidDataException(
                $"Station package deployment catalog '{catalogPath}' is not canonical JSON.");
        }

        if (!string.Equals(deployment.Schema, StationPackageDeployment.RequiredSchema, StringComparison.Ordinal)
            || !string.Equals(deployment.ProjectId, key.ProjectId, StringComparison.Ordinal)
            || !string.Equals(deployment.ApplicationId, key.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(
                deployment.ProjectSnapshotId,
                request.ProjectSnapshotId,
                StringComparison.Ordinal)
            || !string.Equals(deployment.StationSystemId, key.StationSystemId, StringComparison.Ordinal)
            || deployment.PublishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Station package deployment catalog '{catalogPath}' identity is invalid.");
        }

        return new StationDeploymentRoute(
            target.AgentId,
            target.StationId,
            deployment.PackageContentSha256);
    }

    private static string ExistingCatalogDirectory(string? value)
    {
        var configured = Required(value, nameof(StationCoordinatorTransportOptions.DeploymentCatalogDirectory));
        var path = Path.GetFullPath(configured, AppContext.BaseDirectory);
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException(
                $"Station deployment catalog directory '{path}' does not exist.");
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidOperationException(
                $"Station deployment {name} must be canonical non-empty text.")
            : value;

    private sealed record DeploymentTarget(string AgentId, string StationId);

    private sealed record DeploymentKey(
        string ProjectId,
        string ApplicationId,
        string StationSystemId)
    {
        public override string ToString() =>
            $"{ProjectId}/{ApplicationId}/{StationSystemId}";
    }
}
