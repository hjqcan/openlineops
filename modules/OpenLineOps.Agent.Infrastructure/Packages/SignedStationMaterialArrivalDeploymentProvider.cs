using OpenLineOps.Agent.Application.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Packages;

public sealed record SignedStationMaterialArrivalDeploymentOptions(
    string AgentId,
    string StationId,
    string PackagePath,
    string PackageContentSha256);

public sealed class SignedStationMaterialArrivalDeploymentProvider(
    SignedStationMaterialArrivalDeploymentOptions options,
    SignedStationPackageInstaller installer) :
    IStationMaterialArrivalDeploymentProvider
{
    private readonly string _agentId = Required(options.AgentId, nameof(options.AgentId));
    private readonly string _stationId = Required(options.StationId, nameof(options.StationId));
    private readonly string _packagePath = ExistingPackagePath(options.PackagePath);
    private readonly string _packageContentSha256 = RequireSha256(
        options.PackageContentSha256,
        nameof(options.PackageContentSha256));
    private readonly SignedStationPackageInstaller _installer =
        installer ?? throw new ArgumentNullException(nameof(installer));

    public async ValueTask<VerifiedStationMaterialArrivalDeployment> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var installed = await _installer
            .InstallAsync(_packagePath, _packageContentSha256, cancellationToken)
            .ConfigureAwait(false);
        var manifest = installed.Manifest;
        return new VerifiedStationMaterialArrivalDeployment(
            _agentId,
            _stationId,
            manifest.ProjectId,
            manifest.ApplicationId,
            manifest.ProjectSnapshotId,
            manifest.ProductionLineDefinitionId,
            manifest.StationSystemId,
            manifest.ContentSha256);
    }

    private static string ExistingPackagePath(string value)
    {
        var path = Path.GetFullPath(Required(value, nameof(value)));
        if (!string.Equals(Path.GetExtension(path), ".olopkg", StringComparison.Ordinal)
            || !File.Exists(path))
        {
            throw new FileNotFoundException(
                "Current Station material-arrival deployment package does not exist.",
                path);
        }

        return path;
    }

    private static string RequireSha256(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException(
                $"{parameterName} must be lowercase hexadecimal SHA-256.",
                parameterName);

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}
