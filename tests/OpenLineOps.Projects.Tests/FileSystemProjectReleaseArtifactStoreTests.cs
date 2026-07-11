using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class FileSystemProjectReleaseArtifactStoreTests : IDisposable
{
    private static readonly DateTimeOffset PublishedAtUtc = new(2026, 7, 10, 8, 30, 0, TimeSpan.Zero);

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-release-artifact-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RollbackRemovesVerifiedUncommittedReleaseAndAllowsSameSnapshotRepublish()
    {
        var scope = CreateScope("project.rollback", "application.rollback");
        var store = new FileSystemProjectReleaseArtifactStore();
        var first = await store.PublishAsync(
            scope,
            "snapshot.rollback",
            PublishedAtUtc,
            CreateMetadata());

        await store.RollbackPublicationAsync(
            scope,
            first.SnapshotId,
            first.ContentSha256);

        Assert.Null(await store.OpenAsync(scope, first.SnapshotId, first.ContentSha256));
        var republished = await store.PublishAsync(
            scope,
            "snapshot.rollback",
            PublishedAtUtc,
            CreateMetadata());
        Assert.Equal(first.ContentSha256, republished.ContentSha256);
    }

    [Fact]
    public async Task PublishAndOpenRoundTripCopiesApplicationSourceAndNormalizesNonDigestMetadata()
    {
        var scope = CreateScope("project.release", "application.main");
        WriteTopologyResources(scope, "topology.main", "layout.a", "layout.b");
        WriteSourceFile(scope, "flows/process-main/flow.json", "flow-v1");
        WriteSourceFile(scope, "flows/process-main/nodes/node-script/generated.py", "print('release')\n");
        Directory.CreateDirectory(Path.Combine(GetApplicationSourcePath(scope), "empty-folder"));
        var store = new FileSystemProjectReleaseArtifactStore();

        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.main.v1",
            PublishedAtUtc,
            CreateUnnormalizedMetadata());
        var opened = await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256);

        Assert.NotNull(opened);
        Assert.Equal(scope.ProjectId, opened.ProjectId);
        Assert.Equal(scope.ApplicationId, opened.ApplicationId);
        Assert.Equal(PublishedAtUtc, opened.PublishedAtUtc);
        Assert.Equal(descriptor.ContentSha256, opened.ContentSha256);
        Assert.Equal(8, opened.Files.Count);
        Assert.All(opened.Files, file =>
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.Equal(file.Sha256.ToLowerInvariant(), file.Sha256);
        });
        Assert.Equal(["layout.a", "layout.b"], opened.Metadata.LayoutIds);
        Assert.Equal(["block.inspect@1", "block.move@2"], opened.Metadata.BlockVersionIds);
        Assert.Equal(["capability.a", "capability.b"], opened.Metadata.CapabilityBindings
            .Select(binding => binding.CapabilityId));
        Assert.Equal(["Slot", "System"], opened.Metadata.TargetReferences.Select(target => target.Kind));
        var frozenOperation = Assert.Single(opened.Metadata.ProductionLine.Operations);
        Assert.Equal("openlineops.flow-ir", frozenOperation.FlowIrSchema);
        Assert.Equal("{}", frozenOperation.FlowIrCanonicalJson);
        Assert.Equal(
            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            frozenOperation.FlowIrSha256);

        var releaseSourceScope = new ProjectApplicationWorkspaceScope(
            opened.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);
        var releaseApplicationPath = GetApplicationSourcePath(releaseSourceScope);
        Assert.Equal("flow-v1", File.ReadAllText(Path.Combine(releaseApplicationPath, "flows", "process-main", "flow.json")));
        Assert.True(Directory.Exists(Path.Combine(releaseApplicationPath, "empty-folder")));
        Assert.StartsWith(Path.GetFullPath(scope.ProjectPath), descriptor.ReleaseRootPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(descriptor.ReleaseRootPath, "source"), descriptor.SourceRootPath);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath));
        Assert.Equal(
            "openlineops.project-release-artifact",
            manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var productionLine = manifest.RootElement
            .GetProperty("metadata")
            .GetProperty("productionLine");
        Assert.True(productionLine.TryGetProperty("productModel", out _));
        Assert.True(productionLine.TryGetProperty("entryOperationId", out _));
        Assert.True(productionLine.TryGetProperty("operations", out _));
        Assert.True(productionLine.TryGetProperty("transitions", out _));
        Assert.False(productionLine.TryGetProperty("dutModel", out _));
        Assert.False(productionLine.TryGetProperty("workstations", out _));
        Assert.False(productionLine.TryGetProperty("stages", out _));
        Assert.DoesNotContain(Path.GetFullPath(scope.ProjectPath), await File.ReadAllTextAsync(descriptor.ManifestPath));
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("block")]
    [InlineData("binding")]
    [InlineData("target")]
    public async Task PublishRejectsDuplicateFrozenIdentityCollections(string collection)
    {
        var scope = CreateScope($"project.duplicate-{collection}", "application.main");
        var metadata = CreateMetadata();
        metadata = collection switch
        {
            "layout" => metadata with { LayoutIds = [.. metadata.LayoutIds, metadata.LayoutIds.First()] },
            "block" => metadata with
            {
                BlockVersionIds = [.. metadata.BlockVersionIds, metadata.BlockVersionIds.First()]
            },
            "binding" => metadata with
            {
                CapabilityBindings =
                [
                    .. metadata.CapabilityBindings,
                    metadata.CapabilityBindings.First()
                ]
            },
            "target" => metadata with
            {
                TargetReferences = [.. metadata.TargetReferences, metadata.TargetReferences.First()]
            },
            _ => throw new InvalidOperationException($"Unsupported duplicate collection {collection}.")
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(
                scope,
                $"snapshot.duplicate-{collection}",
                PublishedAtUtc,
                metadata)
            .AsTask());

        Assert.Contains("duplicate frozen identities", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAndOpenRoundTripsDeterministicallyOrderedOperationGraph()
    {
        var scope = CreateScope("project.operation-graph", "application.main");
        var metadata = CreateMetadata();
        var terminal = Assert.Single(metadata.ProductionLine.Operations);
        var entry = terminal with
        {
            OperationId = "operation.inspect",
            DisplayName = "Inspect"
        };
        metadata = metadata with
        {
            ProductionLine = metadata.ProductionLine with
            {
                EntryOperationId = entry.OperationId,
                Operations = [terminal, entry],
                Transitions =
                [
                    new ProjectReleaseRouteTransition(
                        "transition.inspect-eol",
                        entry.OperationId,
                        terminal.OperationId,
                        "Sequence",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null)
                ]
            }
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.operation-graph",
            PublishedAtUtc,
            metadata);
        var opened = Assert.IsType<OpenedProjectReleaseArtifact>(
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Equal("product.main", opened.Metadata.ProductionLine.ProductModel.ProductModelId);
        Assert.Equal("operation.inspect", opened.Metadata.ProductionLine.EntryOperationId);
        Assert.Equal(
            ["operation.eol", "operation.inspect"],
            opened.Metadata.ProductionLine.Operations.Select(operation => operation.OperationId));
        var transition = Assert.Single(opened.Metadata.ProductionLine.Transitions);
        Assert.Equal("transition.inspect-eol", transition.TransitionId);
        Assert.Equal("Sequence", transition.Kind);
    }

    [Fact]
    public async Task OpenRejectsLegacyDutWorkstationStageReleaseShapeWithoutCompatibilityParsing()
    {
        var scope = CreateScope("project.legacy-release-shape", "application.main");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.legacy-release-shape",
            PublishedAtUtc,
            CreateMetadata());
        var document = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        var line = document["metadata"]!["productionLine"]!.AsObject();
        var productModel = line["productModel"]!.DeepClone();
        line.Remove("productModel");
        line["dutModel"] = productModel;
        line["workstations"] = new JsonArray();
        line["stages"] = line["operations"]!.DeepClone();
        await File.WriteAllTextAsync(descriptor.ManifestPath, document.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishRejectsUnreachableOperationGraph()
    {
        var scope = CreateScope("project.unreachable-operation", "application.main");
        var metadata = CreateMetadata();
        var entry = Assert.Single(metadata.ProductionLine.Operations);
        var unreachable = entry with { OperationId = "operation.unreachable" };
        metadata = metadata with
        {
            ProductionLine = metadata.ProductionLine with
            {
                Operations = [entry, unreachable],
                Transitions = []
            }
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.unreachable-operation",
                PublishedAtUtc,
                metadata));

        Assert.Contains("not reachable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsNonCanonicalRouteTransitionKind()
    {
        var scope = CreateScope("project.route-token", "application.main");
        var metadata = CreateMetadata();
        var terminal = Assert.Single(metadata.ProductionLine.Operations);
        var entry = terminal with { OperationId = "operation.entry" };
        metadata = metadata with
        {
            ProductionLine = metadata.ProductionLine with
            {
                EntryOperationId = entry.OperationId,
                Operations = [entry, terminal],
                Transitions =
                [
                    new ProjectReleaseRouteTransition(
                        "transition.entry-eol",
                        entry.OperationId,
                        terminal.OperationId,
                        "sequence",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null)
                ]
            }
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(scope, "snapshot.route-token", PublishedAtUtc, metadata));

        Assert.Contains("kind 'sequence' is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPreservesExplicitApplicationProjectLayoutInsideRelease()
    {
        var projectPath = Path.Combine(_testRoot, "explicit-project");
        var scope = new ProjectApplicationWorkspaceScope(
            "project.explicit",
            "application.main",
            projectPath,
            "applications/Main Line/MainLine.oloapp");
        WriteSourceFile(scope, "MainLine.oloapp", "{\"applicationId\":\"application.main\"}");
        WriteSourceFile(scope, "flows/process-main/flow.json", "explicit-flow");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();

        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.explicit",
            PublishedAtUtc,
            CreateMetadata());
        var opened = Assert.IsType<OpenedProjectReleaseArtifact>(
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));
        var releaseScope = new ProjectApplicationWorkspaceScope(
            opened.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);

        Assert.Equal(
            "explicit-flow",
            File.ReadAllText(Path.Combine(
                releaseScope.ApplicationRootPath,
                "flows",
                "process-main",
                "flow.json")));
        Assert.True(File.Exists(Path.Combine(releaseScope.ApplicationRootPath, "MainLine.oloapp")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath));
        var internalApplicationPath = manifest.RootElement
            .GetProperty("sourceApplicationRelativePath")
            .GetString();
        Assert.Equal("applications/Main Line", internalApplicationPath);
        Assert.Equal(
            "applications/Main Line/MainLine.oloapp",
            manifest.RootElement.GetProperty("applicationProjectRelativePath").GetString());
    }

    [Fact]
    public async Task PublishCopiesContentAddressedPluginPackageAndOpenRejectsPackageTampering()
    {
        var scope = CreateScope("project.package", "application.main");
        WriteSourceFile(scope, "flows/process-main/flow.json", "package-flow");
        var packagePath = Path.Combine(_testRoot, "plugin-package");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "manifest.json"), "{\"id\":\"plugin.motion\"}");
        await File.WriteAllTextAsync(Path.Combine(packagePath, "plugin.dll"), "plugin-binary-v1");
        await File.WriteAllTextAsync(Path.Combine(packagePath, "dependency.dat"), "dependency-v1");
        var dependency = CreatePackageDependency(packagePath);
        var metadata = CreateMetadata() with
        {
            CapabilityBindings =
            [
                new ProjectReleaseCapabilityBinding(
                    "capability.main",
                    "binding.main",
                    "PluginCommand",
                    "plugin.motion",
                    "station.eol",
                    "station.eol")
            ],
            PackageDependencies = [dependency]
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.package",
            PublishedAtUtc,
            metadata);
        var opened = Assert.IsType<OpenedProjectReleaseArtifact>(
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));
        var frozenDependency = Assert.Single(opened.Metadata.PackageDependencies);
        var frozenPackagePath = Path.Combine(
            opened.ReleaseRootPath,
            frozenDependency.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal("dependency-v1", File.ReadAllText(Path.Combine(frozenPackagePath, "dependency.dat")));
        Assert.DoesNotContain(Path.GetFullPath(packagePath), await File.ReadAllTextAsync(descriptor.ManifestPath));

        await File.WriteAllTextAsync(Path.Combine(frozenPackagePath, "dependency.dat"), "dependency-tampered");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));
        Assert.Contains("plugin package", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishRefusesOverwriteAndPreservesOriginalRelease()
    {
        var scope = CreateScope("project.immutable", "application.main");
        var sourcePath = WriteSourceFile(scope, "flows/process-main/flow.json", "original-flow");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.immutable",
            PublishedAtUtc,
            CreateMetadata());
        await File.WriteAllTextAsync(sourcePath, "changed-draft-flow");

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.immutable",
                PublishedAtUtc.AddMinutes(1),
                CreateMetadata()));
        var opened = await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256);
        var releaseScope = new ProjectApplicationWorkspaceScope(
            opened!.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);

        Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "original-flow",
            File.ReadAllText(Path.Combine(GetApplicationSourcePath(releaseScope), "flows", "process-main", "flow.json")));
    }

    [Fact]
    public async Task PublishRejectsSourceChangedWhileCopyIsBeingFrozen()
    {
        var scope = CreateScope("project.concurrent-source-change", "application.main");
        var sourcePath = Path.Combine(GetApplicationSourcePath(scope), "aaa-large-source.bin");
        var original = new byte[32 * 1024 * 1024];
        Array.Fill(original, (byte)0x41);
        await File.WriteAllBytesAsync(sourcePath, original);
        var changed = new byte[original.Length];
        Array.Fill(changed, (byte)0x42);
        var store = new FileSystemProjectReleaseArtifactStore();

        var publishTask = store.PublishAsync(
                scope,
                "snapshot.concurrent-source-change",
                PublishedAtUtc,
                CreateMetadata())
            .AsTask();
        var mutationTask = Task.Run(async () =>
        {
            var stagingDirectory = Path.Combine(scope.ProjectPath, "releases", ".staging");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!timeout.IsCancellationRequested)
            {
                if (Directory.Exists(stagingDirectory)
                    && Directory.EnumerateFiles(
                            stagingDirectory,
                            "aaa-large-source.bin",
                            SearchOption.AllDirectories)
                        .Any())
                {
                    try
                    {
                        await File.WriteAllBytesAsync(sourcePath, changed, timeout.Token);
                        return;
                    }
                    catch (IOException)
                    {
                    }
                }

                await Task.Delay(1, timeout.Token);
            }

            throw new TimeoutException("The release copy did not expose the deterministic mutation window.");
        });

        var exception = await Assert.ThrowsAsync<IOException>(async () => await publishTask);
        await mutationTask;

        Assert.Contains("changed while", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(
            scope.ProjectPath,
            "releases",
            "snapshot.concurrent-source-change")));
    }

    [Fact]
    public async Task OpenRejectsTamperedSourceFile()
    {
        var scope = CreateScope("project.tamper", "application.main");
        WriteSourceFile(scope, "configuration/project.json", "configuration-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.tamper",
            PublishedAtUtc,
            CreateMetadata());
        var releaseScope = new ProjectApplicationWorkspaceScope(
            scope.ProjectId,
            scope.ApplicationId,
            descriptor.SourceRootPath,
            descriptor.ApplicationProjectRelativePath);
        await File.WriteAllTextAsync(
            Path.Combine(GetApplicationSourcePath(releaseScope), "configuration", "project.json"),
            "configuration-updated");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("project.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRejectsTamperedProductionLine()
    {
        var scope = CreateScope("project.production-tamper", "application.main");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.production-tamper",
            PublishedAtUtc,
            CreateMetadata());
        var releasedLine = Path.Combine(
            descriptor.SourceRootPath,
            "applications",
            scope.ApplicationId,
            "production",
            "lines",
            "line.main",
            "line.json");
        await File.WriteAllTextAsync(
            releasedLine,
            $$"""
            {"schemaVersion":"openlineops.production-line","resourceKind":"OpenLineOps.ProductionLine","applicationId":"{{scope.ApplicationId}}","lineDefinitionId":"line.tampered"}
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("line.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsMissingProductionLine()
    {
        var scope = CreateScope("project.production-missing", "application.main");
        Directory.Delete(
            Path.Combine(GetApplicationSourcePath(scope), "production"),
            recursive: true);
        var store = new FileSystemProjectReleaseArtifactStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.production-missing",
                PublishedAtUtc,
                CreateMetadata()));

        Assert.Contains("production", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishRejectsProductionOperationConfigurationMissingFromApplication()
    {
        var scope = CreateScope("project.stage-configuration-missing", "application.main");
        var metadata = CreateMetadata();
        var frozenOperation = Assert.Single(metadata.ProductionLine.Operations) with
        {
            ConfigurationSnapshotId = "configuration.missing"
        };
        metadata = metadata with
        {
            ProductionLine = metadata.ProductionLine with { Operations = [frozenOperation] }
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.stage-configuration-missing",
                PublishedAtUtc,
                metadata));

        Assert.Contains("configuration.missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishRejectsFlowIrHashMismatch()
    {
        var scope = CreateScope("project.flow-ir-hash", "application.main");
        WriteSourceFile(scope, "flows/process-main/flow.json", "flow-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var metadata = WithOperation(
            CreateMetadata(),
            operation => operation with { FlowIrSha256 = new string('a', 64) });

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.flow-ir-hash",
                PublishedAtUtc,
                metadata));

        Assert.Contains(
            "Production operation operation.eol Flow IR SHA-256",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowIr")]
    [InlineData("packageContent")]
    [InlineData("manifest")]
    [InlineData("entryAssembly")]
    [InlineData("packageFile")]
    public async Task PublishRejectsDigestValuesWithSurroundingWhitespace(string digestField)
    {
        var scope = CreateScope($"project.digest-whitespace-{digestField}", "application.main");
        var packagePath = Path.Combine(_testRoot, $"plugin-package-{digestField}");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "manifest.json"), "{\"id\":\"plugin.motion\"}");
        await File.WriteAllTextAsync(Path.Combine(packagePath, "plugin.dll"), "plugin-binary-v1");
        var dependency = CreatePackageDependency(packagePath);
        var metadata = CreateMetadata() with
        {
            CapabilityBindings =
            [
                new ProjectReleaseCapabilityBinding(
                    "capability.main",
                    "binding.main",
                    "PluginCommand",
                    "plugin.motion",
                    "station.eol",
                    "station.eol")
            ],
            PackageDependencies = [dependency]
        };

        metadata = digestField switch
        {
            "flowIr" => metadata with
            {
                ProductionLine = metadata.ProductionLine with
                {
                    Operations =
                    [
                        Assert.Single(metadata.ProductionLine.Operations) with
                        {
                            FlowIrSha256 = $" {Assert.Single(metadata.ProductionLine.Operations).FlowIrSha256} "
                        }
                    ]
                }
            },
            "packageContent" => metadata with
            {
                PackageDependencies =
                [
                    dependency with
                    {
                        PackageContentSha256 = $" {dependency.PackageContentSha256} "
                    }
                ]
            },
            "manifest" => metadata with
            {
                PackageDependencies =
                [
                    dependency with
                    {
                        ManifestSha256 = $" {dependency.ManifestSha256} "
                    }
                ]
            },
            "entryAssembly" => metadata with
            {
                PackageDependencies =
                [
                    dependency with
                    {
                        EntryAssemblySha256 = $" {dependency.EntryAssemblySha256} "
                    }
                ]
            },
            "packageFile" => metadata with
            {
                PackageDependencies =
                [
                    dependency with
                    {
                        Files = dependency.Files
                            .Select((file, index) => index == 0
                                ? file with { Sha256 = $" {file.Sha256} " }
                                : file)
                            .ToArray()
                    }
                ]
            },
            _ => throw new InvalidOperationException($"Unknown digest field {digestField}.")
        };
        var store = new FileSystemProjectReleaseArtifactStore();

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(
                scope,
                $"snapshot.digest-whitespace-{digestField}",
                PublishedAtUtc,
                metadata));

        Assert.Contains("lowercase 64-character SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsInvalidFlowIrJsonEvenWhenHashMatches()
    {
        const string invalidJson = "not-json";
        var scope = CreateScope("project.flow-ir-json", "application.main");
        WriteSourceFile(scope, "flows/process-main/flow.json", "flow-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var metadata = WithOperation(
            CreateMetadata(),
            operation => operation with
            {
                FlowIrCanonicalJson = invalidJson,
                FlowIrSha256 = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(invalidJson)))
                    .ToLowerInvariant()
            });

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.flow-ir-json",
                PublishedAtUtc,
                metadata));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsExtraFileAnywhereInRelease()
    {
        var scope = CreateScope("project.extra", "application.main");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.extra",
            PublishedAtUtc,
            CreateMetadata());
        await File.WriteAllTextAsync(Path.Combine(descriptor.ReleaseRootPath, "untracked.txt"), "untracked");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("file set differs", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untracked.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRejectsMissingSourceFile()
    {
        var scope = CreateScope("project.missing", "application.main");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.missing",
            PublishedAtUtc,
            CreateMetadata());
        var releaseScope = new ProjectApplicationWorkspaceScope(
            scope.ProjectId,
            scope.ApplicationId,
            descriptor.SourceRootPath,
            descriptor.ApplicationProjectRelativePath);
        var deletedLayout = Directory.GetFiles(
            Path.Combine(GetApplicationSourcePath(releaseScope), "layouts"), "*.json").Single();
        File.Delete(deletedLayout);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("file set differs", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFileName(deletedLayout), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRejectsManifestFilePathTraversal()
    {
        var scope = CreateScope("project.path", "application.main");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.path",
            PublishedAtUtc,
            CreateMetadata());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        manifest["files"]!.AsArray()[0]!["relativePath"] = "../escaped.json";
        await File.WriteAllTextAsync(descriptor.ManifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("outside the application source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsUnsupportedReleaseSchemaVersion()
    {
        var scope = CreateScope("project.old-release", "application.main");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.old-release",
            PublishedAtUtc,
            CreateMetadata());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        manifest["schemaVersion"] = 99;
        await File.WriteAllTextAsync(descriptor.ManifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("schema version 99", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsUnknownReleaseManifestField()
    {
        var scope = CreateScope("project.unknown-field", "application.main");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.unknown-field",
            PublishedAtUtc,
            CreateMetadata());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        manifest["unexpectedSourcePath"] = "applications/application.main";
        await File.WriteAllTextAsync(descriptor.ManifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsUppercaseReleaseDigestAlias()
    {
        var scope = CreateScope("project.uppercase-digest", "application.main");
        WriteTopologyResources(scope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.uppercase-digest",
            PublishedAtUtc,
            CreateMetadata());

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.OpenAsync(
                scope,
                descriptor.SnapshotId,
                descriptor.ContentSha256.ToUpperInvariant()));

        Assert.Contains("lowercase", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("topology", "topologyId", "layoutId", "forbidden identity 'layoutId'")]
    [InlineData("layouts", "layoutId", "topologyId", "no valid 'layoutId'")]
    public async Task PublishRejectsCrossKindEmbeddedResourceIdentityAlias(
        string resourceDirectoryName,
        string canonicalIdentityProperty,
        string aliasIdentityProperty,
        string expectedError)
    {
        var scope = CreateScope($"project.cross-identity-{resourceDirectoryName}", "application.main");
        WriteTopologyResources(scope);
        var sourcePath = Directory.GetFiles(
            Path.Combine(GetApplicationSourcePath(scope), resourceDirectoryName),
            "*.json").Single();
        var source = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath))!.AsObject();
        var identity = source[canonicalIdentityProperty]!.GetValue<string>();
        source.Remove(canonicalIdentityProperty);
        source[aliasIdentityProperty] = identity;
        await File.WriteAllTextAsync(sourcePath, source.ToJsonString());
        var store = new FileSystemProjectReleaseArtifactStore();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.PublishAsync(
                scope,
                $"snapshot.cross-identity-{resourceDirectoryName}",
                PublishedAtUtc,
                CreateMetadata()));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseOpensAfterWholeProjectFolderMoves()
    {
        var originalProjectPath = Path.Combine(_testRoot, "original-project");
        var movedProjectPath = Path.Combine(_testRoot, "moved-project");
        var originalScope = new ProjectApplicationWorkspaceScope(
            "project.movable",
            "application.main",
            originalProjectPath,
            ApplicationProjectPath("application.main"));
        WriteSourceFile(originalScope, "flows/process-main/flow.json", "portable-flow");
        WriteTopologyResources(originalScope);
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            originalScope,
            "snapshot.portable",
            PublishedAtUtc,
            CreateMetadata());

        Directory.Move(originalProjectPath, movedProjectPath);
        var movedScope = new ProjectApplicationWorkspaceScope(
            originalScope.ProjectId,
            originalScope.ApplicationId,
            movedProjectPath,
            originalScope.ApplicationProjectRelativePath);
        var opened = await store.OpenAsync(movedScope, descriptor.SnapshotId, descriptor.ContentSha256);

        Assert.NotNull(opened);
        Assert.StartsWith(Path.GetFullPath(movedProjectPath), opened.ReleaseRootPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(originalProjectPath), opened.ManifestPath, StringComparison.OrdinalIgnoreCase);
        var releaseScope = new ProjectApplicationWorkspaceScope(
            opened.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);
        Assert.Equal(
            "portable-flow",
            File.ReadAllText(Path.Combine(GetApplicationSourcePath(releaseScope), "flows", "process-main", "flow.json")));
    }

    [Fact]
    public async Task ApplicationAAndBAreIsolatedAndWrongScopeIsRejected()
    {
        var projectPath = Path.Combine(_testRoot, "project-ab");
        var scopeA = new ProjectApplicationWorkspaceScope(
            "project.ab",
            "application.a",
            projectPath,
            ApplicationProjectPath("application.a"));
        var scopeB = new ProjectApplicationWorkspaceScope(
            "project.ab",
            "application.b",
            projectPath,
            ApplicationProjectPath("application.b"));
        WriteSourceFile(scopeA, "flows/shared/flow.json", "flow-from-a");
        WriteSourceFile(scopeB, "flows/shared/flow.json", "flow-from-b");
        WriteTopologyResources(scopeA, "topology.a", "layout.main");
        WriteTopologyResources(scopeB, "topology.b", "layout.main");
        var store = new FileSystemProjectReleaseArtifactStore();

        var releaseA = await store.PublishAsync(
            scopeA,
            "snapshot.a",
            PublishedAtUtc,
            CreateMetadata("topology.a"));
        var releaseB = await store.PublishAsync(
            scopeB,
            "snapshot.b",
            PublishedAtUtc.AddMinutes(1),
            CreateMetadata("topology.b"));
        var openedA = await store.OpenAsync(scopeA, releaseA.SnapshotId, releaseA.ContentSha256);
        var openedB = await store.OpenAsync(scopeB, releaseB.SnapshotId, releaseB.ContentSha256);

        Assert.NotNull(openedA);
        Assert.NotNull(openedB);
        Assert.NotEqual(openedA.ReleaseRootPath, openedB.ReleaseRootPath);
        Assert.Equal("topology.a", openedA.Metadata.TopologyId);
        Assert.Equal("topology.b", openedB.Metadata.TopologyId);
        Assert.Equal("flow-from-a", ReadReleasedFlow(openedA));
        Assert.Equal("flow-from-b", ReadReleasedFlow(openedB));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scopeB, releaseA.SnapshotId, releaseA.ContentSha256));
        Assert.Contains("scope is", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private ProjectApplicationWorkspaceScope CreateScope(string projectId, string applicationId)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            projectId,
            applicationId,
            Path.Combine(_testRoot, "project"),
            ApplicationProjectPath(applicationId));
        WriteTopologyResources(scope);
        return scope;
    }

    private static ProjectReleaseSourceMetadata CreateMetadata(string topologyId = "topology.main")
    {
        return new ProjectReleaseSourceMetadata(
            topologyId,
            ["layout.main"],
            CreateProductionMetadata(topologyId, ["block.main@1"]),
            [],
            [new ProjectReleaseCapabilityBinding("capability.main", "binding.main", "Simulator", "simulator.main", "station.eol", "station.eol")],
            [new ProjectReleaseTargetReference("System", "station.eol")],
            ["block.main@1"],
            []);
    }

    private static ProjectReleaseSourceMetadata CreateUnnormalizedMetadata()
    {
        return new ProjectReleaseSourceMetadata(
            " topology.main ",
            ["layout.b", " layout.a "],
            CreateProductionMetadata("topology.main", ["block.inspect@1", "block.move@2"]),
            [],
            [
                new ProjectReleaseCapabilityBinding("capability.b", "binding.b", " Simulator ", "simulator.b", "station.eol", "station.eol"),
                new ProjectReleaseCapabilityBinding(" capability.a ", "binding.a", "Simulator", "simulator.a", "station.eol", "station.eol")
            ],
            [
                new ProjectReleaseTargetReference("System", "station.eol"),
                new ProjectReleaseTargetReference(" Slot ", "slot.main")
            ],
            ["block.move@2", " block.inspect@1 "],
            []);
    }

    private static ProjectReleaseSourceMetadata WithOperation(
        ProjectReleaseSourceMetadata metadata,
        Func<ProjectReleaseOperation, ProjectReleaseOperation> update)
    {
        var operation = update(Assert.Single(metadata.ProductionLine.Operations));
        return metadata with
        {
            ProductionLine = metadata.ProductionLine with { Operations = [operation] }
        };
    }

    private static ProjectReleaseProductionLine CreateProductionMetadata(
        string topologyId,
        IReadOnlyCollection<string> blockVersionIds)
    {
        return new ProjectReleaseProductionLine(
            "line.main",
            "Main Line",
            topologyId,
            new ProjectReleaseProductModel("product.main", "MAINBOARD-A", "serialNumber"),
            "operation.eol",
            [
                new ProjectReleaseOperation(
                    "operation.eol",
                    "EOL",
                    "station.eol",
                    "process.main",
                    "configuration.main.v1",
                    "process.main@1.0.0",
                    "openlineops.flow-ir",
                    "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                    "{}",
                    blockVersionIds,
                    [new ProjectReleaseOperationResource(
                        "resource.station",
                        "Station",
                        "station.eol",
                        "Fixed",
                        [])],
                    [])
            ],
            Transitions: [],
            LineControllerAuthorizations: []);
    }

    private static string WriteSourceFile(
        ProjectApplicationWorkspaceScope scope,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            GetApplicationSourcePath(scope),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WriteTopologyResources(
        ProjectApplicationWorkspaceScope scope,
        string topologyId = "topology.main",
        params string[] layoutIds)
    {
        var resolvedLayoutIds = layoutIds.Length == 0 ? ["layout.main"] : layoutIds;
        var topologyDirectory = Path.Combine(GetApplicationSourcePath(scope), "topology");
        var layoutDirectory = Path.Combine(GetApplicationSourcePath(scope), "layouts");
        if (Directory.Exists(topologyDirectory))
        {
            Directory.Delete(topologyDirectory, recursive: true);
        }

        if (Directory.Exists(layoutDirectory))
        {
            Directory.Delete(layoutDirectory, recursive: true);
        }

        Directory.CreateDirectory(topologyDirectory);
        Directory.CreateDirectory(layoutDirectory);
        var productionLineDirectory = Path.Combine(
            GetApplicationSourcePath(scope),
            "production",
            "lines",
            "line.main");
        var engineeringProjectsDirectory = Path.Combine(
            GetApplicationSourcePath(scope),
            "configuration",
            "projects");
        var stationProfilesDirectory = Path.Combine(
            GetApplicationSourcePath(scope),
            "configuration",
            "station-profiles");
        Directory.CreateDirectory(productionLineDirectory);
        Directory.CreateDirectory(engineeringProjectsDirectory);
        Directory.CreateDirectory(stationProfilesDirectory);
        File.WriteAllText(
            Path.Combine(topologyDirectory, "topology.json"),
            $$"""
            {"schemaVersion":"openlineops.automation-topology","resourceKind":"OpenLineOps.AutomationTopology","applicationId":"{{scope.ApplicationId}}","topologyId":"{{topologyId}}"}
            """);
        foreach (var layoutId in resolvedLayoutIds)
        {
            File.WriteAllText(
                Path.Combine(layoutDirectory, $"{layoutId}.json"),
                $$"""
                {"schemaVersion":"openlineops.site-layout","resourceKind":"OpenLineOps.SiteLayout","applicationId":"{{scope.ApplicationId}}","layoutId":"{{layoutId}}"}
                """);
        }


        File.WriteAllText(
            Path.Combine(productionLineDirectory, "line.json"),
            $$"""
            {"schemaVersion":"openlineops.production-line","resourceKind":"OpenLineOps.ProductionLine","applicationId":"{{scope.ApplicationId}}","lineDefinitionId":"line.main"}
            """);
        File.WriteAllText(
            Path.Combine(engineeringProjectsDirectory, "project-main.json"),
            $$$"""
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"{{{scope.ApplicationId}}}","resourceKind":"project","resourceId":"engineering.main","snapshot":{"projectId":"engineering.main","workspaceId":"workspace.main","displayName":"Main","createdAtUtc":"2026-07-10T08:00:00+00:00","activeSnapshotId":"configuration.main.v1","snapshots":[{"snapshotId":"configuration.main.v1","projectId":"engineering.main","processDefinitionId":"process.main","processVersionId":"process.main@1.0.0","recipeId":"recipe.main","recipeVersionId":"recipe.main@1","stationProfileId":"station.profile.main","status":"Published","publishedAtUtc":"2026-07-10T08:00:00+00:00","deviceBindings":[]}]}}
            """);
        File.WriteAllText(
            Path.Combine(stationProfilesDirectory, "station-profile-main.json"),
            $$$"""
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"{{{scope.ApplicationId}}}","resourceKind":"station-profile","resourceId":"station.profile.main","snapshot":{"stationProfileId":"station.profile.main","stationSystemId":"station.eol","displayName":"EOL","deviceBindings":[]}}
            """);
    }

    private static string ReadReleasedFlow(OpenedProjectReleaseArtifact release)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            release.ProjectId,
            release.ApplicationId,
            release.SourceRootPath,
            release.ApplicationProjectRelativePath);
        return File.ReadAllText(Path.Combine(GetApplicationSourcePath(scope), "flows", "shared", "flow.json"));
    }

    private static string ApplicationProjectPath(string applicationId)
    {
        return $"applications/{applicationId}/{applicationId}.oloapp";
    }

    private static string GetApplicationSourcePath(ProjectApplicationWorkspaceScope scope)
    {
        return scope.ApplicationRootPath;
    }

    private static ProjectReleasePackageDependencyLock CreatePackageDependency(string packagePath)
    {
        var files = Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new ProjectReleasePackageFile(
                    Path.GetRelativePath(packagePath, path).Replace('\\', '/'),
                    bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var canonical = new StringBuilder();
        foreach (var file in files)
        {
            canonical.Append(file.RelativePath)
                .Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        var contentSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        var manifest = files.Single(file => file.RelativePath == "manifest.json");
        var entry = files.Single(file => file.RelativePath == "plugin.dll");
        return new ProjectReleasePackageDependencyLock(
            "capability.main",
            "binding.main",
            "PluginCommand",
            "plugin.motion",
            "station.eol",
            "station.eol",
            "plugin.motion",
            "plugin.motion",
            "1.2.3",
            contentSha256,
            manifest.Sha256,
            entry.Sha256,
            "1.0.0",
            "any",
            "openlineops.plugin-abi/1",
            $"packages/{contentSha256}",
            manifest.RelativePath,
            entry.RelativePath,
            [new ProjectReleasePackageCommandLock("Device", "motion.move", "capability.main", "Move")],
            files,
            packagePath);
    }
}
