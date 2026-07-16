using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace OpenLineOps.Agent.Tests;

public sealed partial class StagedAgentRabbitMqProcessE2ETests
{
    private const string ProductionClosureHandoffVariable =
        "OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH";
    private const string PostgresConnectionStringVariable =
        "OPENLINEOPS_POSTGRES_CONNECTION_STRING";
    private const string StudioTwoAgentEvidenceVariable =
        "OPENLINEOPS_STUDIO_TWO_AGENT_EVIDENCE_PATH";
    private const string StudioTwoAgentFormalGateVariable =
        "OPENLINEOPS_STUDIO_TWO_AGENT_FORMAL_GATE";
    private const string StudioTwoAgentAccountSuffixVariable =
        "OPENLINEOPS_STUDIO_TWO_AGENT_ACCOUNT_SUFFIX";

    private static readonly UTF8Encoding StudioUtf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false);

    internal sealed record StudioStationFixture(
        string StationSystemId,
        string StationId,
        string PackageContentSha256,
        string PackagePath,
        string PackageFileSha256,
        string DeploymentCatalogPath,
        string DeploymentCatalogSha256,
        string SigningKeyId,
        string OperationId,
        string SlotId);

    internal sealed record StudioByteIdentity(long SizeBytes, string Sha256);

    internal sealed record StudioApplicationPortabilityEvidence(
        string SourceProjectId,
        string TargetProjectId,
        string ApplicationId,
        long FileCount,
        long TotalSizeBytes,
        string SourceBeforeCopyTreeSha256,
        string CopiedTreeSha256,
        string AfterImportTreeSha256,
        string AfterPublishTreeSha256,
        string AfterExecutionTreeSha256,
        string SourceAfterExecutionTreeSha256,
        bool Unchanged)
    {
        public string TreeSha256 => SourceBeforeCopyTreeSha256;
    }

    internal sealed record StudioImmutableRunTraceEvidence(
        StudioByteIdentity Before,
        StudioByteIdentity After,
        bool Unchanged,
        DateTimeOffset TerminalCompletedAtUtc,
        DateTimeOffset UnloadAtUtc);

    internal sealed record StudioProductionFixture(
        string HandoffPath,
        string PrivateExecutionRoot,
        string ProjectRoot,
        string ProjectFilePath,
        string SummaryPath,
        string SummarySha256,
        string EvidenceManifestPath,
        string EvidenceManifestSha256,
        string PublicEvidenceRoot,
        string ProjectId,
        string ApplicationId,
        string ProjectSnapshotId,
        string ProductionLineDefinitionId,
        string TopologyId,
        string ProductModelId,
        string IdentityInputKey,
        string SigningPublicKeyPath,
        string SigningPublicKeySha256,
        StudioStationFixture EntryStation,
        StudioStationFixture DownstreamStation,
        StudioApplicationPortabilityEvidence ApplicationPortability,
        StudioImmutableRunTraceEvidence ImmutableRunTrace);

    internal sealed class StudioProductionFixtureLease : IAsyncDisposable
    {
        private bool _disposed;

        private StudioProductionFixtureLease(StudioProductionFixture fixture)
        {
            Fixture = fixture;
        }

        public StudioProductionFixture Fixture { get; }

        public static StudioProductionFixtureLease Open(string handoffPath) =>
            new(LoadStudioProductionFixture(handoffPath));

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            var failures = new List<Exception>();
            try
            {
                DeleteStudioPrivateExecutionRoot(Fixture.PrivateExecutionRoot);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (File.Exists(Fixture.HandoffPath))
                {
                    File.Delete(Fixture.HandoffPath);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Private Studio production handoff cleanup failed.",
                    failures);
            }

            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static StudioProductionFixture LoadStudioProductionFixture(string handoffPath)
    {
        var canonicalHandoffPath = StudioRequiredCanonicalFile(
            handoffPath,
            ProductionClosureHandoffVariable);
        ValidateStudioPrivateHandoffPath(canonicalHandoffPath);
        if (!string.Equals(
                Path.GetFileName(canonicalHandoffPath),
                "production-closure-handoff.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private Studio handoff must be named production-closure-handoff.json.");
        }

        using var handoff = StudioReadJson(canonicalHandoffPath, "private production handoff");
        var root = handoff.RootElement;
        StudioRequireExactProperties(
            root,
            "private production handoff",
            "schema",
            "schemaVersion",
            "createdAtUtc",
            "privateExecutionRoot",
            "sourceProjectPath",
            "projectPath",
            "summaryPath",
            "summarySizeBytes",
            "summarySha256",
            "evidenceManifestPath",
            "evidenceManifestSizeBytes",
            "evidenceManifestSha256",
            "projectId",
            "applicationId",
            "topologyId",
            "productionLineDefinitionId",
            "projectSnapshotId",
            "immutableRunTrace");
        StudioRequireExactString(
            root,
            "schema",
            "openlineops.production-closure-private-handoff");
        if (StudioRequiredInt64(root, "schemaVersion") != 1)
        {
            throw new InvalidDataException("Private production handoff schemaVersion must be 1.");
        }

        _ = StudioRequiredUtcTimestamp(root, "createdAtUtc");
        var privateRoot = StudioRequiredCanonicalDirectory(
            StudioRequiredString(root, "privateExecutionRoot"),
            "privateExecutionRoot");
        ValidateStudioPrivateRoot(privateRoot);
        var sourceProjectRoot = StudioRequiredCanonicalDirectory(
            StudioRequiredString(root, "sourceProjectPath"),
            "sourceProjectPath");
        StudioRequireDirectChild(privateRoot, sourceProjectRoot, "sourceProjectPath");
        if (!string.Equals(Path.GetFileName(sourceProjectRoot), "project-source", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private Studio source Project must be the project-source direct child.");
        }
        var projectRoot = StudioRequiredCanonicalDirectory(
            StudioRequiredString(root, "projectPath"),
            "projectPath");
        StudioRequireDirectChild(privateRoot, projectRoot, "projectPath");
        if (!string.Equals(Path.GetFileName(projectRoot), "project", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Private Studio project must be the project direct child.");
        }

        var projectFiles = Directory.EnumerateFiles(
                projectRoot,
                "*.oloproj",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToArray();
        if (projectFiles.Length != 1)
        {
            throw new InvalidDataException(
                "Private Studio project must contain exactly one direct-child .oloproj file.");
        }

        var summaryPath = StudioRequiredCanonicalFile(
            StudioRequiredString(root, "summaryPath"),
            "summaryPath");
        var summarySizeBytes = StudioRequiredInt64(root, "summarySizeBytes");
        var summarySha256 = StudioRequiredSha256(root, "summarySha256");
        StudioVerifyFileIdentity(summaryPath, summarySizeBytes, summarySha256, "summary");
        var evidenceManifestPath = StudioRequiredCanonicalFile(
            StudioRequiredString(root, "evidenceManifestPath"),
            "evidenceManifestPath");
        var evidenceManifestSizeBytes = StudioRequiredInt64(root, "evidenceManifestSizeBytes");
        var evidenceManifestSha256 = StudioRequiredSha256(root, "evidenceManifestSha256");
        StudioVerifyFileIdentity(
            evidenceManifestPath,
            evidenceManifestSizeBytes,
            evidenceManifestSha256,
            "production evidence manifest");
        var evidenceRoot = Path.GetDirectoryName(summaryPath)
                           ?? throw new InvalidDataException("Studio summary has no parent directory.");
        StudioRequireDirectChild(evidenceRoot, evidenceManifestPath, "evidenceManifestPath");
        if (!string.Equals(
                Path.GetFileName(evidenceManifestPath),
                "evidence-manifest.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Studio evidence manifest has a non-canonical name.");
        }

        var publicEvidence = LoadStudioPublicEvidenceManifest(
            evidenceRoot,
            evidenceManifestPath,
            summaryPath,
            summarySizeBytes,
            summarySha256);
        using var summary = StudioReadJson(summaryPath, "packaged production closure summary");
        var summaryRoot = summary.RootElement;
        StudioRejectForbiddenPublicSummaryContent(summaryRoot, "summary");
        StudioRequireExactProperties(
            summaryRoot,
            "packaged production closure summary",
            "schema",
            "status",
            "startedAtUtc",
            "completedAtUtc",
            "packagedExecutable",
            "packagedBinaries",
            "artifactRoot",
            "projectPath",
            "projectId",
            "applicationId",
            "topologyId",
            "productionLineDefinitionId",
            "projectSnapshotId",
            "applicationPortability",
            "frozenRelease",
            "externalProgramTrial",
            "studioAuthoring",
            "scenarios",
            "restart",
            "diagnostics",
            "failure");
        StudioRequireExactString(
            summaryRoot,
            "schema",
            "openlineops.production-closure-e2e");
        StudioRequireExactString(summaryRoot, "status", "passed");
        StudioRequireExactString(summaryRoot, "artifactRoot", ".");
        var projectId = StudioRequiredString(root, "projectId");
        var applicationId = StudioRequiredString(root, "applicationId");
        var topologyId = StudioRequiredString(root, "topologyId");
        var lineId = StudioRequiredString(root, "productionLineDefinitionId");
        var snapshotId = StudioRequiredString(root, "projectSnapshotId");
        StudioRequireExactString(summaryRoot, "projectId", projectId);
        StudioRequireExactString(summaryRoot, "applicationId", applicationId);
        StudioRequireExactString(summaryRoot, "topologyId", topologyId);
        StudioRequireExactString(summaryRoot, "productionLineDefinitionId", lineId);
        StudioRequireExactString(summaryRoot, "projectSnapshotId", snapshotId);
        StudioRequireExactString(summaryRoot, "projectPath", "private-runtime/project");
        StudioRequireExactString(
            summaryRoot,
            "packagedExecutable",
            "packaged-desktop/OpenLineOps.exe");
        var packagedBinaries = StudioRequiredObject(summaryRoot, "packagedBinaries");
        StudioRequireExactProperties(
            packagedBinaries,
            "packaged binary evidence",
            "before",
            "after",
            "unchangedDuringRun");
        if (StudioRequiredProperty(packagedBinaries, "unchangedDuringRun").ValueKind
            != JsonValueKind.True)
        {
            throw new InvalidDataException(
                "Packaged production closure must prove unchanged staged binaries.");
        }
        var packagedBefore = StudioRequiredObject(packagedBinaries, "before");
        var packagedAfter = StudioRequiredObject(packagedBinaries, "after");
        StudioRequireExactProperties(
            packagedBefore,
            "packaged binaries before",
            "desktopExecutable",
            "runtimeApiExecutable");
        StudioRequireExactProperties(
            packagedAfter,
            "packaged binaries after",
            "desktopExecutable",
            "runtimeApiExecutable");
        foreach (var (propertyName, expectedPath) in new[]
                 {
                     ("desktopExecutable", "packaged-desktop/OpenLineOps.exe"),
                     ("runtimeApiExecutable", "packaged-desktop/runtime/api/OpenLineOps.Api.exe")
                 })
        {
            var beforeIdentity = StudioRequiredObject(packagedBefore, propertyName);
            var afterIdentity = StudioRequiredObject(packagedAfter, propertyName);
            StudioRequireExactProperties(
                beforeIdentity,
                $"packaged binary before {propertyName}",
                "path",
                "sha256",
                "sizeBytes",
                "modifiedAtUtc");
            StudioRequireExactProperties(
                afterIdentity,
                $"packaged binary after {propertyName}",
                "path",
                "sha256",
                "sizeBytes",
                "modifiedAtUtc");
            StudioRequireExactString(beforeIdentity, "path", expectedPath);
            StudioRequireExactString(afterIdentity, "path", expectedPath);
            var beforeSha256 = StudioRequiredSha256(beforeIdentity, "sha256");
            var afterSha256 = StudioRequiredSha256(afterIdentity, "sha256");
            var beforeSizeBytes = StudioRequiredInt64(beforeIdentity, "sizeBytes");
            var afterSizeBytes = StudioRequiredInt64(afterIdentity, "sizeBytes");
            var beforeModifiedAtUtc = StudioRequiredUtcTimestamp(
                beforeIdentity,
                "modifiedAtUtc");
            var afterModifiedAtUtc = StudioRequiredUtcTimestamp(
                afterIdentity,
                "modifiedAtUtc");
            if (!string.Equals(beforeSha256, afterSha256, StringComparison.Ordinal)
                || beforeSizeBytes != afterSizeBytes
                || beforeModifiedAtUtc != afterModifiedAtUtc)
            {
                throw new InvalidDataException(
                    $"Packaged binary '{propertyName}' changed during the production closure.");
            }
        }

        var applicationPortability = LoadStudioApplicationPortability(
            StudioRequiredObject(summaryRoot, "applicationPortability"),
            projectId,
            applicationId);
        VerifyStudioApplicationTrees(
            sourceProjectRoot,
            projectRoot,
            applicationPortability);

        var scenarios = StudioRequiredObject(summaryRoot, "scenarios");
        StudioRequireExactProperties(
            scenarios,
            "packaged production closure scenarios",
            "concurrentPipeline",
            "vendorPassed",
            "vendorFailedRework",
            "operatorCancel",
            "vendorCrash",
            "recovery");
        StudioRequireExactProperties(
            StudioRequiredObject(scenarios, "concurrentPipeline"),
            "concurrent pipeline scenario",
            "status",
            "unitA",
            "unitB",
            "observedAtUtc",
            "assertion",
            "lineState",
            "screenshots");
        StudioRequireExactProperties(
            StudioRequiredObject(scenarios, "vendorPassed"),
            "vendor Passed scenario",
            "status",
            "run",
            "trace",
            "immutableRunTrace",
            "materialLifecycle",
            "artifacts",
            "artifactDownloads",
            "verifiedSaveActionCount",
            "verifiedArtifactSave",
            "screenshots");
        StudioRequireExactProperties(
            StudioRequiredObject(scenarios, "vendorFailedRework"),
            "vendor Failed Rework scenario",
            "status",
            "unit",
            "run",
            "trace",
            "assertion",
            "screenshots");
        StudioRequireExactProperties(
            StudioRequiredObject(scenarios, "operatorCancel"),
            "operator Cancel scenario",
            "status",
            "unit",
            "run",
            "vendorProcessesBeforeCancel",
            "processTreeTerminated",
            "trace",
            "screenshots");
        StudioRequireExactProperties(
            StudioRequiredObject(scenarios, "vendorCrash"),
            "vendor Crash scenario",
            "status",
            "unit",
            "run",
            "trace",
            "incidents",
            "screenshots");
        StudioRequireExactProperties(
            StudioRequiredObject(scenarios, "recovery"),
            "recovery scenario",
            "status",
            "unit",
            "interruptedOperationRunId",
            "backendPidTerminated",
            "vendorProcessesBeforeCrash",
            "recoveryRequired",
            "terminal",
            "noAutomaticReplay",
            "recoveryDecisions",
            "trace",
            "screenshots");

        var handoffImmutableRunTrace = LoadStudioImmutableRunTrace(
            StudioRequiredObject(root, "immutableRunTrace"),
            "private production handoff immutable Run Trace");
        var summaryImmutableRunTrace = LoadStudioImmutableRunTrace(
            StudioRequiredObject(
                StudioRequiredObject(
                    scenarios,
                    "vendorPassed"),
                "immutableRunTrace"),
            "packaged production summary immutable Run Trace");
        if (handoffImmutableRunTrace != summaryImmutableRunTrace)
        {
            throw new InvalidDataException(
                "Private handoff immutable Run Trace differs from its manifest-bound public summary.");
        }

        var frozen = StudioRequiredObject(summaryRoot, "frozenRelease");
        StudioRequireExactString(
            frozen,
            "manifestSchema",
            "openlineops.project-release-artifact");
        _ = LoadStudioPublicFile(
            evidenceRoot,
            publicEvidence,
            StudioRequiredObject(frozen, "releaseManifest"),
            "frozen release manifest");
        var signingKey = LoadStudioPublicFile(
            evidenceRoot,
            publicEvidence,
            StudioRequiredObject(frozen, "signingPublicKey"),
            "signing public key");

        var packages = StudioRequiredArray(frozen, "stationPackages")
            .EnumerateArray()
            .Select(item => LoadStudioPackageEvidence(evidenceRoot, publicEvidence, item))
            .ToArray();
        var catalogs = StudioRequiredArray(frozen, "deploymentCatalogs")
            .EnumerateArray()
            .Select(item => LoadStudioCatalogEvidence(
                evidenceRoot,
                publicEvidence,
                item,
                projectId,
                applicationId,
                snapshotId,
                lineId))
            .ToArray();
        if (packages.Length != 2
            || catalogs.Length != 2
            || packages.Select(item => item.StationSystemId)
                .Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new InvalidDataException(
                "Packaged Studio release must contain exactly two distinct Station packages and catalogs.");
        }

        var entryDeployment = StudioRequiredObject(frozen, "entryStationDeployment");
        var entryStationSystemId = StudioRequiredString(
            entryDeployment,
            "stationSystemId");
        var entryStationId = StudioRequiredString(entryDeployment, "stationId");
        var entryPackageContentSha256 = StudioRequiredSha256(
            entryDeployment,
            "packageContentSha256");
        var parsedStations = packages.Select(package => ParseStudioStationPackage(
                package,
                catalogs,
                projectId,
                applicationId,
                snapshotId,
                lineId,
                topologyId,
                string.Equals(
                    package.StationSystemId,
                    entryStationSystemId,
                    StringComparison.Ordinal)
                    ? entryStationId
                    : package.StationSystemId))
            .ToArray();
        var entryStation = parsedStations.Single(station => string.Equals(
            station.StationSystemId,
            entryStationSystemId,
            StringComparison.Ordinal));
        if (!string.Equals(
                entryStation.PackageContentSha256,
                entryPackageContentSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Entry Station package differs from the frozen entry deployment.");
        }

        var downstreamStation = parsedStations.Single(station => !string.Equals(
            station.StationSystemId,
            entryStationSystemId,
            StringComparison.Ordinal));
        if (!string.Equals(
                entryStation.ProductModelId,
                downstreamStation.ProductModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                entryStation.IdentityInputKey,
                downstreamStation.IdentityInputKey,
                StringComparison.Ordinal)
            || !string.Equals(
                entryStation.SigningKeyId,
                downstreamStation.SigningKeyId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Two Station packages do not describe one product model and signing identity.");
        }

        return new StudioProductionFixture(
            canonicalHandoffPath,
            privateRoot,
            projectRoot,
            projectFiles[0],
            summaryPath,
            summarySha256,
            evidenceManifestPath,
            evidenceManifestSha256,
            evidenceRoot,
            projectId,
            applicationId,
            snapshotId,
            lineId,
            topologyId,
            entryStation.ProductModelId,
            entryStation.IdentityInputKey,
            signingKey.Path,
            signingKey.Sha256,
            entryStation.ToPublic(),
            downstreamStation.ToPublic(),
            applicationPortability,
            summaryImmutableRunTrace);
    }

    private static StudioApplicationPortabilityEvidence LoadStudioApplicationPortability(
        JsonElement element,
        string expectedTargetProjectId,
        string expectedApplicationId)
    {
        StudioRequireExactProperties(
            element,
            "Application portability evidence",
            "status",
            "sourceProjectId",
            "targetProjectId",
            "applicationId",
            "fileCount",
            "totalSizeBytes",
            "sourceBeforeCopyTreeSha256",
            "copiedTreeSha256",
            "afterImportTreeSha256",
            "afterPublishTreeSha256",
            "afterExecutionTreeSha256",
            "sourceAfterExecutionTreeSha256",
            "unchanged");
        StudioRequireExactString(element, "status", "passed");
        var sourceProjectId = StudioRequiredString(element, "sourceProjectId");
        var targetProjectId = StudioRequiredString(element, "targetProjectId");
        var applicationId = StudioRequiredString(element, "applicationId");
        if (string.Equals(sourceProjectId, targetProjectId, StringComparison.Ordinal)
            || !string.Equals(targetProjectId, expectedTargetProjectId, StringComparison.Ordinal)
            || !string.Equals(applicationId, expectedApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application portability evidence does not describe one copied Application across two Projects.");
        }

        var fileCount = StudioRequiredInt64(element, "fileCount");
        var totalSizeBytes = StudioRequiredInt64(element, "totalSizeBytes");
        var hashes = new[]
        {
            StudioRequiredSha256(element, "sourceBeforeCopyTreeSha256"),
            StudioRequiredSha256(element, "copiedTreeSha256"),
            StudioRequiredSha256(element, "afterImportTreeSha256"),
            StudioRequiredSha256(element, "afterPublishTreeSha256"),
            StudioRequiredSha256(element, "afterExecutionTreeSha256"),
            StudioRequiredSha256(element, "sourceAfterExecutionTreeSha256")
        };
        var unchanged = StudioRequiredProperty(element, "unchanged").ValueKind
                        == JsonValueKind.True;
        if (fileCount <= 0
            || totalSizeBytes <= 0
            || !unchanged
            || hashes.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidDataException(
                "Application portability evidence must prove an unchanged non-empty file tree before copy, after import, after publish, and after execution.");
        }

        return new StudioApplicationPortabilityEvidence(
            sourceProjectId,
            targetProjectId,
            applicationId,
            fileCount,
            totalSizeBytes,
            hashes[0],
            hashes[1],
            hashes[2],
            hashes[3],
            hashes[4],
            hashes[5],
            unchanged);
    }

    private static void VerifyStudioApplicationTrees(
        string sourceProjectRoot,
        string targetProjectRoot,
        StudioApplicationPortabilityEvidence evidence)
    {
        var source = ComputeStudioApplicationTree(sourceProjectRoot, evidence.ApplicationId);
        var target = ComputeStudioApplicationTree(targetProjectRoot, evidence.ApplicationId);
        if (source != target
            || source.FileCount != evidence.FileCount
            || source.TotalSizeBytes != evidence.TotalSizeBytes
            || !string.Equals(source.TreeSha256, evidence.TreeSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private source and target Application trees differ from the manifest-bound portability evidence.");
        }
    }

    private static StudioApplicationTreeIdentity ComputeStudioApplicationTree(
        string projectRoot,
        string expectedApplicationId)
    {
        var applicationsRoot = Path.Combine(projectRoot, "applications");
        var applicationProjectFiles = EnumerateStudioTreeFiles(applicationsRoot)
            .Where(static file => file.EndsWith(".oloapp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (applicationProjectFiles.Length != 1)
        {
            throw new InvalidDataException(
                "Portable Project must contain exactly one Application project file.");
        }

        using (var applicationDocument = StudioReadJson(
                   applicationProjectFiles[0],
                   "portable Application project file"))
        {
            StudioRequireExactString(
                applicationDocument.RootElement,
                "applicationId",
                expectedApplicationId);
        }

        var applicationRoot = Path.GetDirectoryName(applicationProjectFiles[0])
                              ?? throw new InvalidDataException(
                                  "Portable Application project file has no parent directory.");
        var files = EnumerateStudioTreeFiles(applicationRoot)
            .Select(file => new FileInfo(file))
            .OrderBy(
                file => Path.GetRelativePath(applicationRoot, file.FullName)
                    .Replace('\\', '/'),
                StringComparer.Ordinal)
            .ToArray();
        using var treeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalSizeBytes = 0;
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(applicationRoot, file.FullName)
                .Replace('\\', '/');
            using var stream = file.OpenRead();
            var fileSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            totalSizeBytes = checked(totalSizeBytes + file.Length);
            AppendStudioTreeHash(treeHash, relativePath);
            AppendStudioTreeHash(treeHash, "\0");
            AppendStudioTreeHash(treeHash, file.Length.ToString(CultureInfo.InvariantCulture));
            AppendStudioTreeHash(treeHash, "\0");
            AppendStudioTreeHash(treeHash, fileSha256);
            AppendStudioTreeHash(treeHash, "\n");
        }

        return new StudioApplicationTreeIdentity(
            files.LongLength,
            totalSizeBytes,
            Convert.ToHexStringLower(treeHash.GetHashAndReset()));
    }

    private static List<string> EnumerateStudioTreeFiles(string root)
    {
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists
            || (rootDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Portable Application tree root is missing or is a reparse point.");
        }

        var files = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Portable Application tree cannot contain a reparse point.");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
                else if (entry is FileInfo file)
                {
                    files.Add(file.FullName);
                }
                else
                {
                    throw new InvalidDataException(
                        "Portable Application tree contains an unsupported filesystem entry.");
                }
            }
        }

        return files;
    }

    private static void AppendStudioTreeHash(IncrementalHash hash, string value)
    {
        hash.AppendData(StudioUtf8WithoutBom.GetBytes(value));
    }

    private sealed record StudioApplicationTreeIdentity(
        long FileCount,
        long TotalSizeBytes,
        string TreeSha256);

    private static StudioImmutableRunTraceEvidence LoadStudioImmutableRunTrace(
        JsonElement element,
        string label)
    {
        StudioRequireExactProperties(
            element,
            label,
            "before",
            "after",
            "unchanged",
            "terminalCompletedAtUtc",
            "unloadAtUtc");
        var beforeElement = StudioRequiredObject(element, "before");
        var afterElement = StudioRequiredObject(element, "after");
        StudioRequireExactProperties(beforeElement, $"{label} before", "sha256", "sizeBytes");
        StudioRequireExactProperties(afterElement, $"{label} after", "sha256", "sizeBytes");
        var before = new StudioByteIdentity(
            StudioRequiredInt64(beforeElement, "sizeBytes"),
            StudioRequiredSha256(beforeElement, "sha256"));
        var after = new StudioByteIdentity(
            StudioRequiredInt64(afterElement, "sizeBytes"),
            StudioRequiredSha256(afterElement, "sha256"));
        var unchanged = StudioRequiredProperty(element, "unchanged").ValueKind
                        == JsonValueKind.True;
        var terminalCompletedAtUtc = StudioRequiredUtcTimestamp(
            element,
            "terminalCompletedAtUtc");
        var unloadAtUtc = StudioRequiredUtcTimestamp(element, "unloadAtUtc");
        if (!unchanged || before != after || unloadAtUtc <= terminalCompletedAtUtc)
        {
            throw new InvalidDataException(
                $"{label} must prove identical terminal Trace bytes before and after a later final unload.");
        }

        return new StudioImmutableRunTraceEvidence(
            before,
            after,
            unchanged,
            terminalCompletedAtUtc,
            unloadAtUtc);
    }

    private static void StudioRejectForbiddenPublicSummaryContent(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (name.Equals("logs", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("commandLine", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("resultPayload", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("textValue", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("message", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("failureReason", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("password", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("raw", StringComparison.OrdinalIgnoreCase)
                       && name.EndsWith("Base64", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Public production closure summary contains forbidden property '{name}'.");
                }

                StudioRejectForbiddenPublicSummaryContent(
                    property.Value,
                    $"{path}.{name}");
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                StudioRejectForbiddenPublicSummaryContent(item, $"{path}[{index++}]");
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = element.GetString() ?? string.Empty;
        var drivePath = value.Length >= 3
                        && char.IsAsciiLetter(value[0])
                        && value[1] == ':'
                        && value[2] is '\\' or '/';
        var credentialUri = Uri.TryCreate(value, UriKind.Absolute, out var uri)
                            && !string.IsNullOrEmpty(uri.UserInfo);
        if (drivePath
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("../", StringComparison.Ordinal)
            || value.Contains("..\\", StringComparison.Ordinal)
            || value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || credentialUri)
        {
            throw new InvalidDataException(
                $"Public production closure summary contains unsafe string content at '{path}'.");
        }
    }

    private static Dictionary<string, StudioPublicFile> LoadStudioPublicEvidenceManifest(
        string evidenceRoot,
        string manifestPath,
        string summaryPath,
        long summarySizeBytes,
        string summarySha256)
    {
        using var manifest = StudioReadJson(manifestPath, "production evidence manifest");
        var root = manifest.RootElement;
        StudioRequireExactProperties(
            root,
            "production evidence manifest",
            "schema",
            "schemaVersion",
            "generatedAtUtc",
            "files");
        StudioRequireExactString(
            root,
            "schema",
            "openlineops.production-closure-evidence-manifest");
        if (StudioRequiredInt64(root, "schemaVersion") != 1)
        {
            throw new InvalidDataException("Production evidence manifest schemaVersion must be 1.");
        }

        _ = StudioRequiredUtcTimestamp(root, "generatedAtUtc");
        var files = new Dictionary<string, StudioPublicFile>(StringComparer.Ordinal);
        foreach (var item in StudioRequiredArray(root, "files").EnumerateArray())
        {
            StudioRequireExactProperties(
                item,
                "production evidence file",
                "relativePath",
                "sizeBytes",
                "sha256");
            var relativePath = StudioRequiredRelativePath(item, "relativePath");
            var file = ResolveStudioPublicFile(
                evidenceRoot,
                relativePath,
                StudioRequiredInt64(item, "sizeBytes"),
                StudioRequiredSha256(item, "sha256"));
            if (!files.TryAdd(relativePath, file))
            {
                throw new InvalidDataException(
                    $"Production evidence manifest duplicates '{relativePath}'.");
            }
        }

        var summary = files.GetValueOrDefault("summary.json")
                      ?? throw new InvalidDataException(
                          "Production evidence manifest does not bind summary.json.");
        if (!StudioPathEquals(summary.Path, summaryPath)
            || summary.SizeBytes != summarySizeBytes
            || !string.Equals(summary.Sha256, summarySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Production evidence manifest summary identity differs from the private handoff.");
        }

        return files;
    }

    private static StudioPublicFile LoadStudioPublicFile(
        string evidenceRoot,
        IReadOnlyDictionary<string, StudioPublicFile> evidence,
        JsonElement element,
        string label)
    {
        var relativePath = StudioRequiredRelativePath(element, "relativePath");
        var expectedSize = StudioRequiredInt64(element, "sizeBytes");
        var expectedSha256 = StudioRequiredSha256(element, "sha256");
        var file = evidence.GetValueOrDefault(relativePath)
                   ?? throw new InvalidDataException(
                       $"{label} is absent from the public evidence manifest.");
        var resolved = ResolveStudioPublicFile(
            evidenceRoot,
            relativePath,
            expectedSize,
            expectedSha256);
        if (!StudioPathEquals(file.Path, resolved.Path)
            || file.SizeBytes != resolved.SizeBytes
            || !string.Equals(file.Sha256, resolved.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} identity is inconsistent.");
        }

        return file;
    }

    private static StudioPackageEvidence LoadStudioPackageEvidence(
        string evidenceRoot,
        IReadOnlyDictionary<string, StudioPublicFile> evidence,
        JsonElement element)
    {
        var stationSystemId = StudioRequiredString(element, "stationSystemId");
        var contentSha256 = StudioRequiredSha256(element, "packageContentSha256");
        var file = LoadStudioPublicFile(evidenceRoot, evidence, element, "Station package");
        if (!string.Equals(
                file.RelativePath,
                $"public-release/station-packages/{contentSha256}.olopkg",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station package public path is not content-addressed.");
        }

        return new StudioPackageEvidence(
            stationSystemId,
            contentSha256,
            file.Path,
            file.SizeBytes,
            file.Sha256);
    }

    private static StudioCatalogEvidence LoadStudioCatalogEvidence(
        string evidenceRoot,
        IReadOnlyDictionary<string, StudioPublicFile> evidence,
        JsonElement element,
        string projectId,
        string applicationId,
        string snapshotId,
        string lineId)
    {
        var stationSystemId = StudioRequiredString(element, "stationSystemId");
        var packageContentSha256 = StudioRequiredSha256(
            element,
            "packageContentSha256");
        var file = LoadStudioPublicFile(
            evidenceRoot,
            evidence,
            element,
            "Station deployment catalog");
        using var document = StudioReadJson(file.Path, "Station deployment catalog");
        var root = document.RootElement;
        StudioRequireExactString(root, "schema", "openlineops.station-package-deployment");
        StudioRequireExactString(root, "projectId", projectId);
        StudioRequireExactString(root, "applicationId", applicationId);
        StudioRequireExactString(root, "projectSnapshotId", snapshotId);
        StudioRequireExactString(root, "productionLineDefinitionId", lineId);
        StudioRequireExactString(root, "stationSystemId", stationSystemId);
        StudioRequireExactString(root, "packageContentSha256", packageContentSha256);
        return new StudioCatalogEvidence(
            stationSystemId,
            packageContentSha256,
            file.Path,
            file.Sha256);
    }

    private static StudioParsedStation ParseStudioStationPackage(
        StudioPackageEvidence package,
        IReadOnlyCollection<StudioCatalogEvidence> catalogs,
        string projectId,
        string applicationId,
        string snapshotId,
        string lineId,
        string topologyId,
        string stationId)
    {
        var catalog = catalogs.Single(item => string.Equals(
                item.StationSystemId,
                package.StationSystemId,
                StringComparison.Ordinal)
            && string.Equals(
                item.PackageContentSha256,
                package.PackageContentSha256,
                StringComparison.Ordinal));
        using var archive = ZipFile.OpenRead(package.Path);
        RejectUnsafeStudioArchive(archive);
        using var packageManifest = ReadStudioArchiveJson(archive, "package.manifest.json");
        var manifest = packageManifest.RootElement;
        StudioRequireExactString(manifest, "format", "openlineops.station-package");
        StudioRequireExactString(manifest, "projectId", projectId);
        StudioRequireExactString(manifest, "applicationId", applicationId);
        StudioRequireExactString(manifest, "projectSnapshotId", snapshotId);
        StudioRequireExactString(manifest, "productionLineDefinitionId", lineId);
        StudioRequireExactString(manifest, "stationSystemId", package.StationSystemId);
        StudioRequireExactString(manifest, "contentSha256", package.PackageContentSha256);
        using var signature = ReadStudioArchiveJson(archive, "package.signature.json");
        StudioRequireExactString(signature.RootElement, "algorithm", "RSA-PSS-SHA256");
        var signingKeyId = StudioRequiredString(signature.RootElement, "keyId");
        _ = Convert.FromBase64String(
            StudioRequiredString(signature.RootElement, "signature"));

        var lineEntry = archive.Entries.Single(entry => entry.FullName.EndsWith(
            $"/production/lines/{lineId}/line.json",
            StringComparison.Ordinal));
        using var line = ReadStudioArchiveJson(lineEntry);
        var lineRoot = line.RootElement;
        StudioRequireExactString(lineRoot, "schemaVersion", "openlineops.production-line");
        StudioRequireExactString(lineRoot, "applicationId", applicationId);
        StudioRequireExactString(lineRoot, "lineDefinitionId", lineId);
        StudioRequireExactString(lineRoot, "topologyId", topologyId);
        var productModel = StudioRequiredObject(lineRoot, "productModel");
        var productModelId = StudioRequiredString(productModel, "productModelId");
        var identityInputKey = StudioRequiredString(productModel, "identityInputKey");
        var operation = StudioRequiredArray(lineRoot, "operations")
            .EnumerateArray()
            .Single(item => string.Equals(
                StudioRequiredString(item, "stationSystemId"),
                package.StationSystemId,
                StringComparison.Ordinal));
        var operationId = StudioRequiredString(operation, "operationId");
        var slot = StudioRequiredArray(operation, "resources")
            .EnumerateArray()
            .Single(item => string.Equals(
                StudioRequiredString(item, "kind"),
                "Slot",
                StringComparison.Ordinal));
        StudioRequireExactString(slot, "resolution", "Fixed");
        var slotId = StudioRequiredString(slot, "topologyTargetId");
        return new StudioParsedStation(
            package.StationSystemId,
            stationId,
            package.PackageContentSha256,
            package.Path,
            package.FileSha256,
            catalog.Path,
            catalog.FileSha256,
            signingKeyId,
            operationId,
            slotId,
            productModelId,
            identityInputKey);
    }

    private static void RejectUnsafeStudioArchive(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name)
                || name.Contains('\\', StringComparison.Ordinal)
                || name.StartsWith('/')
                || name.Split('/').Any(segment => segment is "" or "." or "..")
                || !names.Add(name)
                || (entry.ExternalAttributes & 0xF0000000) == 0xA0000000)
            {
                throw new InvalidDataException(
                    $"Station package contains unsafe archive entry '{name}'.");
            }
        }
    }

    private static JsonDocument ReadStudioArchiveJson(
        ZipArchive archive,
        string entryName)
    {
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidDataException(
                        $"Station package is missing '{entryName}'.");
        return ReadStudioArchiveJson(entry);
    }

    private static JsonDocument ReadStudioArchiveJson(ZipArchiveEntry entry)
    {
        if (entry.Length <= 0 || entry.Length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException(
                $"Station package JSON '{entry.FullName}' has an invalid bounded size.");
        }

        using var stream = entry.Open();
        var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        StudioRejectDuplicateProperties(document.RootElement, entry.FullName);
        return document;
    }

    private static JsonDocument StudioReadJson(string path, string label)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
        StudioRejectDuplicateProperties(document.RootElement, label);
        return document;
    }

    private static void StudioRejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"{path} contains duplicate property '{property.Name}'.");
                }

                StudioRejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                StudioRejectDuplicateProperties(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    private static void StudioRequireExactProperties(
        JsonElement element,
        string label,
        params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be one JSON object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var required = expected.ToImmutableHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(required))
        {
            throw new InvalidDataException(
                $"{label} properties differ from its strict schema. actual={string.Join(',', actual.Order())}");
        }
    }

    private static JsonElement StudioRequiredProperty(
        JsonElement element,
        string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
            ? value
            : throw new InvalidDataException(
                $"Required JSON property '{propertyName}' is absent.");

    private static JsonElement StudioRequiredObject(JsonElement element, string propertyName)
    {
        var value = StudioRequiredProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"'{propertyName}' must be one JSON object.");
    }

    private static JsonElement StudioRequiredArray(JsonElement element, string propertyName)
    {
        var value = StudioRequiredProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"'{propertyName}' must be one JSON array.");
    }

    private static string StudioRequiredString(JsonElement element, string propertyName)
    {
        var value = StudioRequiredProperty(element, propertyName);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text)
               && string.Equals(text, text.Trim(), StringComparison.Ordinal)
            ? text
            : throw new InvalidDataException($"'{propertyName}' must be canonical text.");
    }

    private static string StudioRequiredSha256(JsonElement element, string propertyName)
    {
        var value = StudioRequiredString(element, propertyName);
        return value.Length == 64
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidDataException(
                $"'{propertyName}' must be lowercase hexadecimal SHA-256.");
    }

    private static long StudioRequiredInt64(JsonElement element, string propertyName)
    {
        var value = StudioRequiredProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out var result)
               && result > 0
            ? result
            : throw new InvalidDataException($"'{propertyName}' must be a positive Int64.");
    }

    private static DateTimeOffset StudioRequiredUtcTimestamp(
        JsonElement element,
        string propertyName)
    {
        var text = StudioRequiredString(element, propertyName);
        return DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out var timestamp)
               && timestamp.Offset == TimeSpan.Zero
            ? timestamp
            : throw new InvalidDataException($"'{propertyName}' must be an ISO 8601 UTC timestamp.");
    }

    private static void StudioRequireExactString(
        JsonElement element,
        string propertyName,
        string expected)
    {
        var actual = StudioRequiredString(element, propertyName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"'{propertyName}' must be exactly '{expected}', not '{actual}'.");
        }
    }

    private static string StudioRequiredRelativePath(
        JsonElement element,
        string propertyName)
    {
        var relativePath = StudioRequiredString(element, propertyName);
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Contains('\\', StringComparison.Ordinal)
               && relativePath.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..")
            ? relativePath
            : throw new InvalidDataException(
                $"'{propertyName}' must be a canonical slash-separated relative path.");
    }

    private static StudioPublicFile ResolveStudioPublicFile(
        string evidenceRoot,
        string relativePath,
        long expectedSizeBytes,
        string expectedSha256)
    {
        var canonicalRoot = Path.GetFullPath(evidenceRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            canonicalRoot);
        if (!path.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Public evidence escaped its root: {relativePath}");
        }

        StudioVerifyFileIdentity(path, expectedSizeBytes, expectedSha256, relativePath);
        return new StudioPublicFile(relativePath, path, expectedSizeBytes, expectedSha256);
    }

    private static void StudioVerifyFileIdentity(
        string path,
        long expectedSizeBytes,
        string expectedSha256,
        string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.Length != expectedSizeBytes
            || !string.Equals(StudioSha256File(path), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} bytes do not match frozen evidence.");
        }
    }

    private static string StudioSha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string StudioRequiredCanonicalFile(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{label} must be one canonical absolute file path.");
        }

        var path = Path.GetFullPath(value);
        if (!StudioPathEquals(path, value) || !File.Exists(path))
        {
            throw new FileNotFoundException($"{label} does not exist or is not canonical.", path);
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label} cannot be a reparse point.");
        }

        return path;
    }

    private static string StudioRequiredCanonicalDirectory(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException(
                $"{label} must be one canonical absolute directory path.");
        }

        var path = Path.GetFullPath(value);
        if (!StudioPathEquals(path, value) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"{label} does not exist or is not canonical: {path}");
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label} cannot be a reparse point.");
        }

        return path;
    }

    private static void ValidateStudioPrivateRoot(string privateRoot)
    {
        var expectedBase = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-production-closure-e2e");
        if (!Path.GetFullPath(privateRoot).StartsWith(
                expectedBase + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || StudioPathEquals(privateRoot, expectedBase))
        {
            throw new InvalidDataException(
                "Private Studio execution root is outside its canonical temporary base.");
        }

        RejectStudioExistingPathAndAncestorsReparsePoints(
            expectedBase,
            "private Studio execution base");
        RejectStudioExistingPathAndAncestorsReparsePoints(
            privateRoot,
            "private Studio execution root");
    }

    private static void ValidateStudioPrivateHandoffPath(string handoffPath)
    {
        var expectedBase = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-production-closure-handoffs");
        var canonicalHandoff = Path.GetFullPath(handoffPath);
        var relative = Path.GetRelativePath(expectedBase, canonicalHandoff);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(relative)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || segments.Length != 2
            || segments[0].Length != 32
            || segments[0].Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !string.Equals(
                segments[1],
                "production-closure-handoff.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private Studio handoff is outside its controlled temporary base.");
        }

        RejectStudioExistingPathAndAncestorsReparsePoints(
            expectedBase,
            "private Studio handoff base");
        RejectStudioExistingPathAndAncestorsReparsePoints(
            canonicalHandoff,
            "private Studio handoff path");
    }

    private static void RejectStudioExistingPathAndAncestorsReparsePoints(
        string path,
        string label)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"{label} cannot traverse a reparse point.");
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || StudioPathEquals(parent, current))
            {
                break;
            }

            current = parent;
        }
    }

    private static void StudioRequireDirectChild(string root, string path, string label)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!StudioPathEquals(Path.GetDirectoryName(Path.GetFullPath(path))!, canonicalRoot))
        {
            throw new InvalidDataException($"{label} must be a direct child of '{canonicalRoot}'.");
        }
    }

    private static bool StudioPathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void DeleteStudioPrivateExecutionRoot(string privateRoot)
    {
        ValidateStudioPrivateRoot(privateRoot);
        if (!Directory.Exists(privateRoot))
        {
            return;
        }

        RejectStudioTreeReparsePoints(privateRoot, "private Studio execution root");
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     privateRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(privateRoot, File.GetAttributes(privateRoot) & ~FileAttributes.ReadOnly);
        Directory.Delete(privateRoot, recursive: true);
    }

    private static void RejectStudioTreeReparsePoints(string root, string label)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            RejectStudioReparsePoint(directory, label);
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                RejectStudioReparsePoint(item, label);
                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private sealed record StudioPublicFile(
        string RelativePath,
        string Path,
        long SizeBytes,
        string Sha256);

    private sealed record StudioPackageEvidence(
        string StationSystemId,
        string PackageContentSha256,
        string Path,
        long SizeBytes,
        string FileSha256);

    private sealed record StudioCatalogEvidence(
        string StationSystemId,
        string PackageContentSha256,
        string Path,
        string FileSha256);

    private sealed record StudioParsedStation(
        string StationSystemId,
        string StationId,
        string PackageContentSha256,
        string PackagePath,
        string PackageFileSha256,
        string DeploymentCatalogPath,
        string DeploymentCatalogSha256,
        string SigningKeyId,
        string OperationId,
        string SlotId,
        string ProductModelId,
        string IdentityInputKey)
    {
        public StudioStationFixture ToPublic() => new(
            StationSystemId,
            StationId,
            PackageContentSha256,
            PackagePath,
            PackageFileSha256,
            DeploymentCatalogPath,
            DeploymentCatalogSha256,
            SigningKeyId,
            OperationId,
            SlotId);
    }
}
