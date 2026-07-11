using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Packages;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed record PackageStationOperationExecutorOptions(string PackageDistributionDirectory);

public sealed class PackageStationOperationExecutor : IStationOperationExecutor
{
    private readonly string _packageDistributionDirectory;
    private readonly SignedStationPackageInstaller _installer;
    private readonly IStationRuntimeHost _runtimeHost;

    public PackageStationOperationExecutor(
        PackageStationOperationExecutorOptions options,
        SignedStationPackageInstaller installer,
        IStationRuntimeHost runtimeHost)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PackageDistributionDirectory);
        _packageDistributionDirectory = Path.GetFullPath(options.PackageDistributionDirectory);
        _installer = installer;
        _runtimeHost = runtimeHost;
        if (!Directory.Exists(_packageDistributionDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Station package distribution directory '{_packageDistributionDirectory}' does not exist.");
        }
    }

    public async ValueTask<StationOperationExecutionResult> ExecuteAsync(
        StationJobSnapshot job,
        Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(reportProgress);
        await reportProgress(
                new StationOperationProgress(1, "verifying-package"),
                cancellationToken)
            .ConfigureAwait(false);
        var packagePath = Path.Combine(
            _packageDistributionDirectory,
            $"{job.PackageContentSha256}.olopkg");
        var installed = await _installer
            .InstallAsync(packagePath, job.PackageContentSha256, cancellationToken)
            .ConfigureAwait(false);
        VerifyIdentity(job, installed);
        await reportProgress(
                new StationOperationProgress(5, "package-ready"),
                cancellationToken)
            .ConfigureAwait(false);
        return await _runtimeHost.ExecuteAsync(
                new StationRuntimeExecutionRequest(job, installed.ContentDirectory),
                reportProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void VerifyIdentity(
        StationJobSnapshot job,
        InstalledStationPackage installed)
    {
        if (!string.Equals(installed.Manifest.ProjectId, job.ProjectId, StringComparison.Ordinal)
            || !string.Equals(
                installed.Manifest.ApplicationId,
                job.ApplicationId,
                StringComparison.Ordinal)
            || !string.Equals(
                installed.Manifest.ProjectSnapshotId,
                job.ProjectSnapshotId,
                StringComparison.Ordinal)
            || !string.Equals(
                installed.Manifest.StationSystemId,
                job.StationSystemId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station package '{installed.Manifest.PackageId}' identity does not match job {job.JobId}.");
        }
    }
}
