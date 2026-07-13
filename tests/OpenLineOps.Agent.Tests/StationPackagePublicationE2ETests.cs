using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.ContentProtection;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Agent.Tests;

public sealed class StationPackagePublicationE2ETests : IDisposable
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-station-package-publication-{Guid.NewGuid():N}");

    [Fact]
    public async Task FrozenReleasePublishesPerStationCatalogAndAgentInstallsBeforeExecution()
    {
        using var signingKey = RSA.Create(3072);
        var release = CreateRelease("project.line", "snapshot.main", "portable-content");
        var distribution = Path.Combine(_root, "distribution");
        var catalog = Path.Combine(_root, "catalog");
        var privateKeyPath = WritePrivateKey(signingKey);
        var publisher = CreatePublisher(distribution, catalog, privateKeyPath);

        var published = await publisher.PublishAsync(new ProjectReleaseStationPackageRequest(
            release,
            Metadata("station.assembly", "station.test"),
            PublishedAtUtc));

        Assert.Equal(2, published.Packages.Count);
        Assert.Equal(2, published.Packages.Select(package => package.PackageContentSha256).Distinct().Count());
        Assert.All(published.Packages, package =>
        {
            Assert.Equal(
                $"{package.PackageContentSha256}.olopkg",
                Path.GetFileName(package.PackagePath));
            Assert.True(File.Exists(package.PackagePath));
            Assert.True(File.Exists(package.DeploymentCatalogPath));
        });

        var testPackage = published.Packages.Single(package =>
            package.StationSystemId == "station.test");
        var resolver = new FileSystemStationDeploymentResolver(new StationCoordinatorTransportOptions
        {
            DeploymentCatalogDirectory = catalog,
            Deployments =
            [
                new StationDeploymentOptions
                {
                    ProjectId = release.ProjectId,
                    ApplicationId = release.ApplicationId,
                    StationSystemId = "station.test",
                    AgentId = "agent.test",
                    StationId = "station.test.physical"
                }
            ]
        });
        var route = await resolver.ResolveAsync(new(
            release.ProjectId,
            release.ApplicationId,
            release.SnapshotId,
            "station.test"));
        Assert.Equal(testPackage.PackageContentSha256, route.PackageContentSha256);
        Assert.Equal("line.portable", route.ProductionLineDefinitionId);

        var localRoute = await new FileSystemStationDeploymentResolver(
            new StationCoordinatorTransportOptions
            {
                Provider = StationCoordinatorTransportProviders.Disabled,
                DeploymentCatalogDirectory = catalog
            }).ResolveAsync(new(
                release.ProjectId,
                release.ApplicationId,
                release.SnapshotId,
                "station.test"));
        Assert.Equal(StationMaterialArrivalProducers.CoordinatorApi, localRoute.AgentId);
        Assert.Equal("station.test", localRoute.StationId);
        Assert.Equal(testPackage.PackageContentSha256, localRoute.PackageContentSha256);
        Assert.Equal("line.portable", localRoute.ProductionLineDefinitionId);
        var unmappedAgentResolver = new FileSystemStationDeploymentResolver(
            new StationCoordinatorTransportOptions
            {
                Provider = StationCoordinatorTransportProviders.RabbitMq,
                DeploymentCatalogDirectory = catalog
            });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await unmappedAgentResolver.ResolveAsync(new(
                release.ProjectId,
                release.ApplicationId,
                release.SnapshotId,
                "station.test")));

        var nextRelease = CreateRelease("project.line", "snapshot.next", "portable-content");
        var nextPublished = await publisher.PublishAsync(new ProjectReleaseStationPackageRequest(
            nextRelease,
            Metadata("station.assembly", "station.test"),
            PublishedAtUtc.AddMinutes(1)));
        var nextRoute = await resolver.ResolveAsync(new(
            nextRelease.ProjectId,
            nextRelease.ApplicationId,
            nextRelease.SnapshotId,
            "station.test"));
        Assert.Equal(
            nextPublished.Packages.Single(package => package.StationSystemId == "station.test")
                .PackageContentSha256,
            nextRoute.PackageContentSha256);
        Assert.NotEqual(route.PackageContentSha256, nextRoute.PackageContentSha256);

        var runtime = new RecordingRuntimeHost();
        var executor = new PackageStationOperationExecutor(
            new PackageStationOperationExecutorOptions(distribution),
            new SignedStationPackageInstaller(new StationPackageTrustOptions(
                Path.Combine(_root, "cache"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["release-signing"] = signingKey.ExportSubjectPublicKeyInfoPem()
                })),
            runtime);
        var result = await executor.ExecuteAsync(
            Job(release, route.PackageContentSha256, "station.test"),
            (_, _) => ValueTask.CompletedTask);

        Assert.Equal(ExecutionStatus.Completed, result.ExecutionStatus);
        Assert.NotNull(runtime.Request);
        Assert.True(File.Exists(Path.Combine(
            runtime.Request.PackageContentDirectory,
            "source",
            "applications",
            "portable",
            "application.oloapp")));
        Assert.All(
            Directory.EnumerateFiles(
                runtime.Request.PackageContentDirectory,
                "*",
                SearchOption.AllDirectories),
            file => Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));

        var identityError = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await executor.ExecuteAsync(
                Job(release, route.PackageContentSha256, "station.assembly"),
                (_, _) => ValueTask.CompletedTask));
        Assert.Contains("identity", identityError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, runtime.ExecutionCount);

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var current = identity.User
                          ?? throw new InvalidOperationException(
                              "Current Windows identity has no SID.");
            var frozenApplication = Path.Combine(
                runtime.Request.PackageContentDirectory,
                "source",
                "applications",
                "portable",
                "application.oloapp");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(frozenApplication), security);

            var driftError = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await executor.ExecuteAsync(
                    Job(release, route.PackageContentSha256, "station.test"),
                    (_, _) => ValueTask.CompletedTask));
            Assert.Contains("owner and ACL", driftError.Message, StringComparison.Ordinal);
            Assert.Equal(1, runtime.ExecutionCount);
        }
    }

    [Fact]
    public async Task CopiedApplicationPublishesInAnotherProjectWithoutRewritingItsFiles()
    {
        using var signingKey = RSA.Create(3072);
        var original = CreateRelease("project.a", "snapshot.a", "byte-for-byte-portable");
        var copied = CreateRelease("project.b", "snapshot.b", "byte-for-byte-portable");
        var originalApplication = Path.Combine(
            original.SourceRootPath,
            "applications",
            "portable",
            "application.oloapp");
        var copiedApplication = Path.Combine(
            copied.SourceRootPath,
            "applications",
            "portable",
            "application.oloapp");
        Assert.Equal(
            await File.ReadAllBytesAsync(originalApplication),
            await File.ReadAllBytesAsync(copiedApplication));

        var publisher = CreatePublisher(
            Path.Combine(_root, "portable-distribution"),
            Path.Combine(_root, "portable-catalog"),
            WritePrivateKey(signingKey));
        var first = Assert.Single((await publisher.PublishAsync(new(
            original,
            Metadata("station.portable"),
            PublishedAtUtc))).Packages);
        var second = Assert.Single((await publisher.PublishAsync(new(
            copied,
            Metadata("station.portable"),
            PublishedAtUtc.AddMinutes(1)))).Packages);

        Assert.NotEqual(first.PackageContentSha256, second.PackageContentSha256);
        var installer = new SignedStationPackageInstaller(new StationPackageTrustOptions(
            Path.Combine(_root, "portable-cache"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["release-signing"] = signingKey.ExportSubjectPublicKeyInfoPem()
            }));
        var firstInstalled = await installer.InstallAsync(first.PackagePath, first.PackageContentSha256);
        var secondInstalled = await installer.InstallAsync(second.PackagePath, second.PackageContentSha256);
        Assert.Equal("project.a", firstInstalled.Manifest.ProjectId);
        Assert.Equal("project.b", secondInstalled.Manifest.ProjectId);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(
                firstInstalled.ContentDirectory,
                "source",
                "applications",
                "portable",
                "application.oloapp")),
            await File.ReadAllBytesAsync(Path.Combine(
                secondInstalled.ContentDirectory,
                "source",
                "applications",
                "portable",
                "application.oloapp")));
    }

    [Fact]
    public async Task MidPublicationFailureAtomicallyRemovesNewPackagesAndCatalogEntries()
    {
        using var signingKey = RSA.Create(3072);
        var release = CreateRelease("project.atomic", "snapshot.atomic", "atomic-content");
        var distribution = Path.Combine(_root, "atomic-distribution");
        var catalog = Path.Combine(_root, "atomic-catalog");
        Directory.CreateDirectory(catalog);
        var conflictingCatalog = StationPackageCanonicalization.DeploymentCatalogPath(
            catalog,
            release.ProjectId,
            release.ApplicationId,
            release.SnapshotId,
            "station.test");
        await File.WriteAllTextAsync(conflictingCatalog, "preexisting");
        var publisher = CreatePublisher(distribution, catalog, WritePrivateKey(signingKey));

        await Assert.ThrowsAsync<IOException>(async () =>
            await publisher.PublishAsync(new(
                release,
                Metadata("station.assembly", "station.test"),
                PublishedAtUtc)));

        Assert.Empty(Directory.EnumerateFiles(distribution, "*.olopkg"));
        Assert.Equal([conflictingCatalog], Directory.EnumerateFiles(catalog, "*.json").ToArray());
        Assert.Equal("preexisting", await File.ReadAllTextAsync(conflictingCatalog));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        DeleteProtectedCacheEntries(Path.Combine(_root, "cache"));
        DeleteProtectedCacheEntries(Path.Combine(_root, "portable-cache"));
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     _root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(_root, File.GetAttributes(_root) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, recursive: true);
    }

    private static void DeleteProtectedCacheEntries(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var protector = new ImmutableContentProtector();
        foreach (var contentDirectory in Directory.EnumerateDirectories(cacheRoot).ToArray())
        {
            var leaf = Path.GetFileName(contentDirectory);
            if (leaf.Length == 64
                && leaf.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                protector.DeleteProtectedInstallation(cacheRoot, contentDirectory);
            }
        }

        Directory.Delete(cacheRoot);
    }

    private static FileSystemProjectReleaseStationPackagePublisher CreatePublisher(
        string distribution,
        string catalog,
        string privateKeyPath) => new(new StationPackagePublicationOptions(
        distribution,
        catalog,
        "release-signing",
        privateKeyPath));

    private string WritePrivateKey(RSA signingKey)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, signingKey.ExportRSAPrivateKeyPem());
        return path;
    }

    private ProjectReleaseArtifactDescriptor CreateRelease(
        string projectId,
        string snapshotId,
        string applicationContent)
    {
        var releaseRoot = Path.Combine(_root, projectId, snapshotId);
        var sourceRoot = Path.Combine(releaseRoot, "source");
        var applicationRoot = Path.Combine(sourceRoot, "applications", "portable");
        Directory.CreateDirectory(Path.Combine(applicationRoot, "flows"));
        File.WriteAllText(Path.Combine(releaseRoot, "release.json"), "{\"frozen\":true}");
        File.WriteAllText(Path.Combine(applicationRoot, "application.oloapp"), applicationContent);
        File.WriteAllText(Path.Combine(applicationRoot, "flows", "main.json"), "{\"nodes\":[]}");
        return new ProjectReleaseArtifactDescriptor(
            snapshotId,
            projectId,
            "application.portable",
            PublishedAtUtc,
            new string('a', 64),
            releaseRoot,
            sourceRoot,
            "applications/portable/application.oloapp",
            Path.Combine(releaseRoot, "release.json"),
            []);
    }

    private static ProjectReleaseSourceMetadata Metadata(params string[] stations)
    {
        var operations = stations.Select((station, index) => new ProjectReleaseOperation(
            $"operation.{index}",
            $"Operation {index}",
            station,
            $"flow.{index}",
            $"configuration.{index}",
            $"flow.{index}@1",
            "openlineops.flow-ir",
            new string('a', 64),
            "{}",
            [],
            [new ProjectReleaseOperationResource(
                $"resource.station.{index}",
                "Station",
                station,
                "Fixed",
                [])],
            [])).ToArray();
        var transitions = operations.Select((operation, index) =>
            new ProjectReleaseRouteTransition(
                $"transition.{index}",
                operation.OperationId,
                index + 1 < operations.Length ? operations[index + 1].OperationId : null,
                index + 1 < operations.Length ? null : "Completed",
                "Sequence",
                null,
                null,
                null,
                null,
                null,
                null)).ToArray();
        return new ProjectReleaseSourceMetadata(
            "topology.portable",
            ["layout.portable"],
            new ProjectReleaseProductionLine(
                "line.portable",
                "Portable Line",
                "topology.portable",
                new ProjectReleaseProductModel("product.portable", "PORTABLE", "serialNumber"),
                operations[0].OperationId,
                operations,
                transitions,
                []),
            [],
            [],
            [],
            [],
            []);
    }

    private static StationJobSnapshot Job(
        ProjectReleaseArtifactDescriptor release,
        string contentSha256,
        string stationSystemId) => new(
        new StationJobId(Guid.NewGuid()),
        "job-idempotency",
        "agent.test",
        "station.test.physical",
        stationSystemId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        new StationOperationRunId("operation-run"),
        1,
        "product.portable",
        "serialNumber",
        "SN-001",
        null,
        null,
        release.ProjectId,
        release.ApplicationId,
        release.SnapshotId,
        "line.portable",
        "topology.portable",
        "operator.test",
        contentSha256,
        "operation.0",
        "flow.0",
        "flow.0@1",
        "configuration.0",
        "recipe.0",
        [],
        "{}",
        StationJobStatus.Running,
        null,
        null,
        0,
        null,
        null,
        0,
        0,
        0,
        null,
        null,
        PublishedAtUtc,
        PublishedAtUtc,
        PublishedAtUtc,
        null,
        null);

    private sealed class RecordingRuntimeHost : IStationRuntimeHost
    {
        public StationRuntimeExecutionRequest? Request { get; private set; }

        public int ExecutionCount { get; private set; }

        public ValueTask<StationOperationExecutionResult> ExecuteAsync(
            StationRuntimeExecutionRequest request,
            Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            ExecutionCount += 1;
            return ValueTask.FromResult(new StationOperationExecutionResult(
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                "{}",
                [],
                [],
                [],
                [],
                0,
                0,
                0,
                null,
                null));
        }
    }
}
