using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.ContentProtection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Transport;
using RabbitMQ.Client;

namespace OpenLineOps.Agent.Tests;

public sealed class StagedAgentRabbitMqProcessE2ETests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    private const string AgentBundleRootVariable = "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT";
    private const string SamplePluginRootVariable = "OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT";
    private const string BrokerUriVariable = "OPENLINEOPS_RABBITMQ_URI";
    private const string EvidencePathVariable =
        "OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH";
    private const string ApplicationId = "application.rabbitmq-e2e";
    private const string FlowDefinitionId = "process.rabbitmq-e2e";
    private const string FlowVersionId = "process.rabbitmq-e2e@1";
    private const string LineDefinitionId = "line.rabbitmq-e2e";
    private const string OperationId = "operation.rabbitmq-e2e";
    private const string StationSystemId = "station.rabbitmq-e2e";
    private const string TopologyId = "topology.rabbitmq-e2e";
    private const string SigningKeyId = "staged-rabbitmq-e2e-signing";

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StagedAgentRoundTripsSignedPluginAndDeduplicatesAcrossRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prerequisites = ResolvePrerequisites();
        if (prerequisites is null)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var agentId = $"agent.rabbitmq-e2e.{suffix}";
        var stationId = $"station.rabbitmq-e2e.{suffix}";
        var coordinatorId = $"coordinator-rabbitmq-e2e-{suffix}";
        var root = Path.Combine(Path.GetTempPath(), $"olo-staged-agent-rmq-{suffix}");
        var dataRoot = Path.Combine(root, "agent-data");
        var distributionRoot = Path.Combine(root, "package-distribution");
        var runtimeWorkRoot = Path.Combine(root, "runtime-work");
        var packageCacheRoot = Path.Combine(root, "package-cache");
        Directory.CreateDirectory(root);

        WindowsAgentProcess? agent = null;
        CancellationTokenSource? resultInboxStop = null;
        Task? resultInbox = null;
        RabbitMqStationCoordinatorTransport? coordinator = null;
        BrokerTopology? topology = null;
        SqliteStationJobStore? stationStore = null;
        try
        {
            var package = await BuildSignedPluginPackageAsync(
                root,
                distributionRoot,
                prerequisites.SamplePluginRoot);
            var safetyExecutable = PrepareSafetyNoOp(root);
            var request = CreateRequest(
                agentId,
                stationId,
                package.PackageContentSha256,
                suffix);
            var leaseChange = StationDispatchMessageIdentity.CreateLeaseGranted(
                request,
                Assert.Single(request.ResourceFences));
            var coordinationStore = new InMemoryStationJobCoordinationStore();
            Assert.True(await coordinationStore.TryEnqueueAsync(request, [leaseChange]));

            var transportOptions = new StationCoordinatorTransportOptions
            {
                BrokerUri = prerequisites.BrokerUri.AbsoluteUri,
                RequireTls = IsTls(prerequisites.BrokerUri),
                CoordinatorId = coordinatorId
            };
            topology = await BrokerTopology.CreateAsync(
                prerequisites.BrokerUri,
                transportOptions,
                agentId,
                stationId);
            coordinator = new RabbitMqStationCoordinatorTransport(
                transportOptions,
                coordinationStore);
            resultInboxStop = new CancellationTokenSource();
            resultInbox = coordinator.RunResultInboxAsync(
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                resultInboxStop.Token);

            var environment = CreateAgentEnvironment(
                prerequisites,
                root,
                dataRoot,
                distributionRoot,
                packageCacheRoot,
                runtimeWorkRoot,
                package,
                safetyExecutable,
                agentId,
                stationId,
                suffix);
            agent = WindowsAgentProcess.Start(
                Path.Combine(prerequisites.AgentBundleRoot, "OpenLineOps.Agent.exe"),
                prerequisites.AgentBundleRoot,
                environment);
            await topology.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(30));

            await coordinator.PublishAsync(leaseChange);
            await coordinator.PublishAsync(request);
            var completion = await WaitForCompletionAsync(
                coordinationStore,
                request,
                agent,
                TimeSpan.FromMinutes(2));
            AssertCompletion(request, completion);

            var sqlitePath = Path.Combine(dataRoot, "station-agent.sqlite");
            stationStore = new SqliteStationJobStore(
                $"Data Source={sqlitePath};Pooling=False");
            await WaitUntilAsync(
                async () => (await stationStore.ListPendingOutboxAsync(
                    100,
                    DateTimeOffset.UtcNow)).Count == 0,
                agent,
                TimeSpan.FromSeconds(30),
                "Agent result Outbox did not drain.");
            var persisted = await stationStore.GetByIdempotencyKeyAsync(request.IdempotencyKey)
                            ?? throw new InvalidDataException(
                                "The staged Agent did not persist its Station Job Inbox entry.");
            var initialRevision = persisted.Revision;
            IReadOnlyCollection<StationJobEventInboxItem> initialEvents = [];
            await WaitUntilAsync(
                async () =>
                {
                    initialEvents = await coordinationStore.ListEventsAsync(request.JobId);
                    return CountRuntimeFinishedExecutions(initialEvents) == 1
                           && initialEvents.Any(item =>
                               item.Kind == nameof(StationJobAccepted))
                           && initialEvents.Any(item =>
                               item.Kind == nameof(StationJobProgressed));
                },
                agent,
                TimeSpan.FromSeconds(30),
                "Coordinator did not persist the complete ordered Agent event timeline.");
            var initialExecutionCount = CountRuntimeFinishedExecutions(initialEvents);
            Assert.Equal(1, initialExecutionCount);
            Assert.Contains(initialEvents, item => item.Kind == nameof(StationJobAccepted));
            Assert.Contains(initialEvents, item => item.Kind == nameof(StationJobProgressed));

            await coordinator.PublishAsync(leaseChange);
            await coordinator.PublishAsync(request);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await AssertNoDuplicateAsync(
                stationStore,
                coordinationStore,
                request,
                initialRevision,
                initialEvents.Count,
                initialExecutionCount);

            var firstAgentPid = agent.Id;
            var firstExitCode = await agent.StopCleanlyAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, firstExitCode);
            agent.Dispose();
            agent = null;

            agent = WindowsAgentProcess.Start(
                Path.Combine(prerequisites.AgentBundleRoot, "OpenLineOps.Agent.exe"),
                prerequisites.AgentBundleRoot,
                environment);
            var restartedAgentPid = agent.Id;
            Assert.NotEqual(firstAgentPid, restartedAgentPid);
            await topology.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(30));
            await coordinator.PublishAsync(leaseChange);
            await coordinator.PublishAsync(request);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await AssertNoDuplicateAsync(
                stationStore,
                coordinationStore,
                request,
                initialRevision,
                initialEvents.Count,
                initialExecutionCount);

            var restartExitCode = await agent.StopCleanlyAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, restartExitCode);
            agent.Dispose();
            agent = null;
            stationStore.Dispose();
            stationStore = null;

            WriteEvidence(
                prerequisites,
                package,
                request,
                completion,
                sqlitePath,
                initialEvents,
                firstAgentPid,
                restartedAgentPid,
                firstExitCode,
                restartExitCode,
                initialRevision,
                initialExecutionCount);
        }
        finally
        {
            if (agent is not null)
            {
                agent.Kill();
                agent.Dispose();
            }

            stationStore?.Dispose();

            if (resultInboxStop is not null)
            {
                resultInboxStop.Cancel();
            }

            if (resultInbox is not null)
            {
                await ObserveCancellationAsync(resultInbox);
            }

            resultInboxStop?.Dispose();
            if (coordinator is not null)
            {
                await coordinator.DisposeAsync();
            }

            if (topology is not null)
            {
                await topology.DeleteQueuesAsync();
                await topology.DisposeAsync();
            }

            DeleteWorkRoot(root, packageCacheRoot);
        }
    }

    private static StagedPrerequisites? ResolvePrerequisites()
    {
        var bundle = Environment.GetEnvironmentVariable(AgentBundleRootVariable);
        var plugin = Environment.GetEnvironmentVariable(SamplePluginRootVariable);
        var broker = Environment.GetEnvironmentVariable(BrokerUriVariable);
        if (bundle is null && plugin is null && broker is null)
        {
            return null;
        }

        var bundleRoot = RequiredDirectory(bundle, AgentBundleRootVariable);
        var pluginRoot = RequiredDirectory(plugin, SamplePluginRootVariable);
        if (!Uri.TryCreate(RequiredText(broker, BrokerUriVariable), UriKind.Absolute, out var brokerUri)
            || brokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException(
                $"{BrokerUriVariable} must be an absolute amqp or amqps URI.");
        }

        foreach (var fileName in new[]
                 {
                     "OpenLineOps.Agent.exe",
                     "OpenLineOps.StationRuntime.exe",
                     "OpenLineOps.PluginHost.exe",
                     "OpenLineOps.ScriptWorker.exe",
                     "OpenLineOps.LeastPrivilegeLauncher.exe"
                 })
        {
            _ = RequiredDirectFile(bundleRoot, fileName);
        }

        _ = RequiredDirectFile(pluginRoot, "manifest.json");
        _ = RequiredDirectFile(pluginRoot, "OpenLineOps.SamplePlugins.LoopbackDevice.dll");
        return new StagedPrerequisites(bundleRoot, pluginRoot, brokerUri);
    }

    private static async ValueTask<PublishedPackage> BuildSignedPluginPackageAsync(
        string root,
        string distributionRoot,
        string samplePluginRoot)
    {
        var scope = CreateApplication(root);
        var packageDependency = CreateSamplePluginDependency(root, samplePluginRoot);
        var flow = CreatePluginFlow();
        var metadata = CreatePluginMetadata(flow, packageDependency);
        var publishedAtUtc = DateTimeOffset.UtcNow;
        var release = await new FileSystemProjectReleaseArtifactStore().PublishAsync(
            scope,
            "snapshot.rabbitmq-e2e",
            publishedAtUtc,
            metadata);

        using var signingKey = RSA.Create(3072);
        var privateKeyPath = Path.Combine(root, "station-signing-private.pem");
        var publicKeyPath = Path.Combine(root, "station-signing-public.pem");
        await File.WriteAllTextAsync(privateKeyPath, signingKey.ExportRSAPrivateKeyPem());
        await File.WriteAllTextAsync(publicKeyPath, signingKey.ExportSubjectPublicKeyInfoPem());
        var publisher = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distributionRoot,
                Path.Combine(root, "deployment-catalog"),
                SigningKeyId,
                privateKeyPath));
        var stationPackage = Assert.Single((await publisher.PublishAsync(
            new ProjectReleaseStationPackageRequest(
                release,
                metadata,
                publishedAtUtc))).Packages);
        return new PublishedPackage(
            stationPackage.PackageContentSha256,
            stationPackage.PackagePath,
            publicKeyPath);
    }

    private static ProjectApplicationWorkspaceScope CreateApplication(string root)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.rabbitmq-e2e",
            ApplicationId,
            Path.Combine(root, "project"),
            $"applications/{ApplicationId}/application.oloapp");
        Write(scope, "application.oloapp", "{}");
        Write(
            scope,
            "topology/topology.json",
            """
            {"schemaVersion":"openlineops.automation-topology","resourceKind":"OpenLineOps.AutomationTopology","applicationId":"application.rabbitmq-e2e","topologyId":"topology.rabbitmq-e2e"}
            """);
        Write(
            scope,
            "layouts/layout.main.json",
            """
            {"schemaVersion":"openlineops.site-layout","resourceKind":"OpenLineOps.SiteLayout","applicationId":"application.rabbitmq-e2e","layoutId":"layout.main"}
            """);
        Write(
            scope,
            $"production/lines/{LineDefinitionId}/line.json",
            """
            {"schemaVersion":"openlineops.production-line","resourceKind":"OpenLineOps.ProductionLine","applicationId":"application.rabbitmq-e2e","lineDefinitionId":"line.rabbitmq-e2e"}
            """);
        Write(
            scope,
            $"configuration/projects/{ResourceFileName("project", "engineering.rabbitmq-e2e")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.rabbitmq-e2e","resourceKind":"project","resourceId":"engineering.rabbitmq-e2e","snapshot":{"projectId":"engineering.rabbitmq-e2e","workspaceId":"workspace.rabbitmq-e2e","displayName":"RabbitMQ E2E","createdAtUtc":"2026-07-13T00:00:00+00:00","activeSnapshotId":"configuration.rabbitmq-e2e","snapshots":[{"snapshotId":"configuration.rabbitmq-e2e","projectId":"engineering.rabbitmq-e2e","processDefinitionId":"process.rabbitmq-e2e","processVersionId":"process.rabbitmq-e2e@1","recipeId":"recipe.rabbitmq-e2e","recipeVersionId":"recipe.rabbitmq-e2e@1","stationProfileId":"station.profile.rabbitmq-e2e","status":"Published","publishedAtUtc":"2026-07-13T00:00:00+00:00","deviceBindings":[{"deviceBindingId":"binding.loopback","ownerSystemId":"station.rabbitmq-e2e","capabilityId":"device.loopback","deviceKey":"loopback-device-01"}]}]}}
            """);
        Write(
            scope,
            $"configuration/station-profiles/{ResourceFileName("station-profile", "station.profile.rabbitmq-e2e")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.rabbitmq-e2e","resourceKind":"station-profile","resourceId":"station.profile.rabbitmq-e2e","snapshot":{"stationProfileId":"station.profile.rabbitmq-e2e","stationSystemId":"station.rabbitmq-e2e","displayName":"RabbitMQ E2E Station","deviceBindings":[{"deviceBindingId":"binding.loopback","ownerSystemId":"station.rabbitmq-e2e","capabilityId":"device.loopback","deviceKey":"loopback-device-01"}]}}
            """);
        return scope;
    }

    private static ProjectReleasePackageDependencyLock CreateSamplePluginDependency(
        string root,
        string samplePluginRoot)
    {
        var packageRoot = Path.Combine(root, "sample-plugin-package");
        Directory.CreateDirectory(packageRoot);
        File.Copy(
            RequiredDirectFile(samplePluginRoot, "manifest.json"),
            Path.Combine(packageRoot, "manifest.json"));
        File.Copy(
            RequiredDirectFile(
                samplePluginRoot,
                "OpenLineOps.SamplePlugins.LoopbackDevice.dll"),
            Path.Combine(packageRoot, "OpenLineOps.SamplePlugins.LoopbackDevice.dll"));
        var files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new ProjectReleasePackageFile(
                    Path.GetRelativePath(packageRoot, path).Replace('\\', '/'),
                    bytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
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

        var contentSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new ProjectReleasePackageDependencyLock(
            "device.loopback",
            "binding.loopback",
            ProjectReleaseRuntimeProviderKinds.PluginCommand,
            "openlineops.samples.loopback-device",
            StationSystemId,
            StationSystemId,
            "openlineops.samples.loopback-device",
            "openlineops.samples.loopback-device",
            "0.1.0",
            contentSha256,
            files.Single(file => file.RelativePath == "manifest.json").Sha256,
            files.Single(file =>
                file.RelativePath == "OpenLineOps.SamplePlugins.LoopbackDevice.dll").Sha256,
            "1.0.0",
            "any",
            "openlineops.plugin-abi/1",
            $"packages/{contentSha256}",
            "manifest.json",
            "OpenLineOps.SamplePlugins.LoopbackDevice.dll",
            [new ProjectReleasePackageCommandLock(
                "Device",
                "loopback.echo",
                "device.loopback",
                "Echo")],
            files,
            packageRoot);
    }

    private static FlowIrCanonicalArtifact CreatePluginFlow()
    {
        var source = new FlowIrSourceTrace(
            FlowDefinitionId,
            FlowVersionId,
            FlowIrSourceElementKind.ProcessNode,
            OperationId,
            null);
        var action = new FlowIrAction(
            $"{OperationId}:action:1",
            FlowIrActionKind.DeviceCommand,
            "Invoke frozen loopback plugin",
            "device.loopback",
            "Echo",
            new FlowIrTargetReference(FlowIrTargetReferenceKind.System, StationSystemId),
            "staged-rabbitmq-e2e",
            new FlowIrExecutionPolicy(5_000, 0, FlowIrCancellationMode.Cooperative),
            null,
            source);
        var document = new FlowIrDocument(
            FlowIrSchema.Current,
            FlowDefinitionId,
            FlowVersionId,
            "Staged RabbitMQ plugin operation",
            "start",
            [
                new FlowIrNode(
                    "start",
                    FlowIrNodeKind.Start,
                    "Start",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "start",
                        null)),
                new FlowIrNode(
                    OperationId,
                    FlowIrNodeKind.Command,
                    "Invoke frozen loopback plugin",
                    [action],
                    source),
                new FlowIrNode(
                    "end",
                    FlowIrNodeKind.End,
                    "End",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "end",
                        null))
            ],
            [
                new FlowIrTransition(
                    "start-to-plugin",
                    "start",
                    OperationId,
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "start-to-plugin",
                        null)),
                new FlowIrTransition(
                    "plugin-to-end",
                    OperationId,
                    "end",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "plugin-to-end",
                        null))
            ],
            ImmutableArray<FlowIrBlockDependency>.Empty);
        var result = new FlowIrCanonicalSerializer().Serialize(document);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error.Message);
    }

    private static ProjectReleaseSourceMetadata CreatePluginMetadata(
        FlowIrCanonicalArtifact flow,
        ProjectReleasePackageDependencyLock packageDependency) => new(
        TopologyId,
        ["layout.main"],
        new ProjectReleaseProductionLine(
            LineDefinitionId,
            "RabbitMQ E2E Line",
            TopologyId,
            new ProjectReleaseProductModel("product.rabbitmq-e2e", "BOARD", "serialNumber"),
            OperationId,
            [new ProjectReleaseOperation(
                OperationId,
                "RabbitMQ E2E Plugin Operation",
                StationSystemId,
                FlowDefinitionId,
                "configuration.rabbitmq-e2e",
                FlowVersionId,
                flow.SchemaVersion,
                flow.Sha256,
                flow.CanonicalJson,
                [],
                [new ProjectReleaseOperationResource(
                    "resource.station-rabbitmq-e2e",
                    "Station",
                    StationSystemId,
                    "Fixed",
                    [])],
                [new ProjectReleaseAuthorizedAction(
                    $"{OperationId}:action:1",
                    OperationId,
                    "DeviceCommand",
                    "device.loopback",
                    "Echo",
                    "System",
                    StationSystemId,
                    5_000,
                    null)])],
            [new ProjectReleaseRouteTransition(
                "operation-to-completed",
                OperationId,
                null,
                "Completed",
                "Sequence",
                null,
                null,
                null,
                null,
                null,
                null)],
            []),
        [],
        [new ProjectReleaseCapabilityBinding(
            "device.loopback",
            "binding.loopback",
            ProjectReleaseRuntimeProviderKinds.PluginCommand,
            "openlineops.samples.loopback-device",
            StationSystemId,
            StationSystemId)],
        [new ProjectReleaseTargetReference("System", StationSystemId)],
        [],
        [packageDependency]);

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> CreateAgentEnvironment(
        StagedPrerequisites prerequisites,
        string root,
        string dataRoot,
        string distributionRoot,
        string packageCacheRoot,
        string runtimeWorkRoot,
        PublishedPackage package,
        string safetyExecutable,
        string agentId,
        string stationId,
        string suffix)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string?)entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        foreach (var key in environment.Keys
                     .Where(key => key.StartsWith(
                         "OpenLineOps__Agent__",
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            environment.Remove(key);
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentSid = identity.User?.Value
                         ?? throw new InvalidOperationException(
                             "Current Windows identity has no SID for the staged Agent E2E.");
        Set("AgentId", agentId);
        Set("StationId", stationId);
        Set("DataDirectory", dataRoot);
        Set("BrokerUri", prerequisites.BrokerUri.AbsoluteUri);
        Set("RequireBrokerTls", IsTls(prerequisites.BrokerUri).ToString());
        Set("PrefetchCount", "1");
        Set("MaximumConcurrentJobs", "1");
        Set("PackageDistributionDirectory", distributionRoot);
        Set("PackageCacheDirectory", packageCacheRoot);
        Set("MaterialArrivalPackageContentSha256", package.PackageContentSha256);
        Set("MaterialArrivalPipeName", $"olo-rabbitmq-e2e-{suffix}");
        Set($"TrustedPackagePublicKeyFiles__{SigningKeyId}", package.PublicKeyPath);
        Set("RuntimeExecutablePath", "OpenLineOps.StationRuntime.exe");
        Set("PluginHostExecutablePath", "OpenLineOps.PluginHost.exe");
        Set("PythonScript__WorkerExecutablePath", "OpenLineOps.ScriptWorker.exe");
        Set("PythonScript__HostPythonRuntimeDllPath", RequiredHostPythonRuntimeDllPath());
        Set("PythonScript__Sandbox__RequireLeastPrivilegeExecution", "true");
        Set("PythonScript__Sandbox__IsolationMode", "LeastPrivilegeIdentity");
        Set("PythonScript__Sandbox__LeastPrivilegeIdentity", "RestrictedCurrentLowIntegrity");
        Set(
            "PythonScript__Sandbox__LeastPrivilegeLauncherExecutable",
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Set("PythonScript__Sandbox__LeastPrivilegeNoInteractivePrompt", "true");
        Set("PythonScript__Sandbox__LeastPrivilegeArgumentsTemplate", string.Empty);
        Set("RuntimeWorkingDirectory", runtimeWorkRoot);
        Set("ArtifactDirectory", Path.Combine(root, "artifacts"));
        Set("ArtifactExchangeDirectory", Path.Combine(root, "artifact-exchange"));
        Set("RuntimeTimeout", "00:00:30");
        Set("MaximumRuntimeOutputBytes", (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        Set("AllowedRestrictedExternalProgramHostSids__0", currentSid);
        Set("ExternalProgramAppContainerProfileNamespace", $"OpenLineOps.StagedRmq.{suffix}");
        Set("SafetyExecutablePath", safetyExecutable);
        Set("SafetyWorkingDirectory", Path.Combine(root, "safety-work"));
        Set("SafetyTimeout", "00:00:05");
        environment["Logging__LogLevel__Default"] = "Information";
        environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        return environment;

        void Set(string key, string value) =>
            environment[$"OpenLineOps__Agent__{key}"] = value;
    }

    private static StationJobRequested CreateRequest(
        string agentId,
        string stationId,
        string packageContentSha256,
        string suffix)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();
        return new StationJobRequested(
            Guid.NewGuid(),
            jobId,
            $"staged-rabbitmq-e2e/{suffix}/{jobId:N}",
            agentId,
            stationId,
            StationSystemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"{OperationId}@0001",
            1,
            "product.rabbitmq-e2e",
            "serialNumber",
            $"BOARD-{suffix[..8]}",
            "lot.rabbitmq-e2e",
            "carrier.rabbitmq-e2e",
            "project.rabbitmq-e2e",
            ApplicationId,
            "snapshot.rabbitmq-e2e",
            LineDefinitionId,
            TopologyId,
            "operator.rabbitmq-e2e",
            packageContentSha256,
            OperationId,
            FlowDefinitionId,
            FlowVersionId,
            "configuration.rabbitmq-e2e",
            "recipe.rabbitmq-e2e@1",
            [new StationResourceFence(
                "Station",
                StationSystemId,
                1,
                nowUtc.AddMinutes(10))],
            JsonSerializer.SerializeToElement(new { source = "staged-rabbitmq-e2e" }),
            nowUtc);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<StationJobCompleted> WaitForCompletionAsync(
        InMemoryStationJobCoordinationStore store,
        StationJobRequested request,
        WindowsAgentProcess agent,
        TimeSpan timeout)
    {
        StationJobCompleted? completion = null;
        await WaitUntilAsync(
            async () =>
            {
                completion = await store.GetCompletionAsync(request.IdempotencyKey);
                return completion is not null;
            },
            agent,
            timeout,
            "The staged Agent did not publish a terminal StationJobCompleted message.");
        return completion!;
    }

    private static void AssertCompletion(
        StationJobRequested request,
        StationJobCompleted completion)
    {
        Assert.Equal(request.JobId, completion.JobId);
        Assert.Equal(request.IdempotencyKey, completion.IdempotencyKey);
        Assert.Equal(request.RuntimeSessionId, completion.RuntimeSessionId);
        Assert.Equal(ExecutionStatus.Completed, completion.ExecutionStatus);
        Assert.Equal(ResultJudgement.NotApplicable, completion.Judgement);
        Assert.Null(completion.FailureCode);
        Assert.Null(completion.FailureReason);
        var command = Assert.Single(completion.Commands);
        Assert.Equal("Completed", command.Status);
        Assert.Equal("Echo", command.CommandName);
        Assert.NotNull(command.ResultPayload);
        using var payload = JsonDocument.Parse(command.ResultPayload);
        Assert.Equal(
            "loopback-device-01",
            payload.RootElement.GetProperty("deviceInstanceId").GetString());
        Assert.Equal(
            "staged-rabbitmq-e2e",
            payload.RootElement.GetProperty("echo").GetString());
    }

    private static async Task AssertNoDuplicateAsync(
        SqliteStationJobStore stationStore,
        InMemoryStationJobCoordinationStore coordinationStore,
        StationJobRequested request,
        long expectedRevision,
        int expectedEventCount,
        int expectedExecutionCount)
    {
        var persisted = await stationStore.GetByIdempotencyKeyAsync(request.IdempotencyKey)
                        ?? throw new InvalidDataException(
                            "The staged Agent lost its durable Station Job after redelivery.");
        Assert.Equal(expectedRevision, persisted.Revision);
        Assert.Equal(StationJobStatus.Completed, persisted.Job.Status);
        Assert.Equal(
            expectedEventCount,
            (await coordinationStore.ListEventsAsync(request.JobId)).Count);
        Assert.Equal(
            expectedExecutionCount,
            CountRuntimeFinishedExecutions(
                await coordinationStore.ListEventsAsync(request.JobId)));
        Assert.Empty(await stationStore.ListPendingOutboxAsync(100, DateTimeOffset.UtcNow));
    }

    private static int CountRuntimeFinishedExecutions(
        IReadOnlyCollection<StationJobEventInboxItem> events) => events.Count(item =>
    {
        if (!string.Equals(item.Kind, nameof(StationJobProgressed), StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(
            ReadProgressPhase(item),
            "runtime-finished",
            StringComparison.Ordinal);
    });

    private static string ReadProgressPhase(StationJobEventInboxItem item)
    {
        using var payload = JsonDocument.Parse(item.PayloadJson);
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("phase", out var phase)
            || phase.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(phase.GetString()))
        {
            throw new InvalidDataException(
                "Persisted staged Agent progress event has no canonical phase.");
        }

        return phase.GetString()!;
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        WindowsAgentProcess agent,
        TimeSpan timeout,
        string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (agent.HasExited)
            {
                throw new InvalidOperationException(
                    $"{failure} Agent exited early with code {agent.ExitCode}.");
            }

            if (await predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(failure);
    }

    private static string PrepareSafetyNoOp(string root)
    {
        var sourceRoot = Path.Combine(
            RepositoryRoot(),
            "tools",
            "OpenLineOps.VendorTestHelper",
            "bin",
            BuildConfiguration(),
            "net10.0");
        var sourceExecutable = RequiredDirectFile(
            sourceRoot,
            "OpenLineOps.VendorTestHelper.exe");
        var safetyRoot = Path.Combine(root, "safety-actuator");
        Directory.CreateDirectory(safetyRoot);
        foreach (var fileName in new[]
                 {
                     "OpenLineOps.VendorTestHelper.dll",
                     "OpenLineOps.VendorTestHelper.deps.json",
                     "OpenLineOps.VendorTestHelper.runtimeconfig.json"
                 })
        {
            File.Copy(
                RequiredDirectFile(sourceRoot, fileName),
                Path.Combine(safetyRoot, fileName));
        }

        var safetyExecutable = Path.Combine(safetyRoot, "OpenLineOps.SafetyNoOp.exe");
        File.Copy(sourceExecutable, safetyExecutable);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = safetyExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = safetyRoot,
            ArgumentList = { "emergency-stop", "staged-e2e-probe" }
        }) ?? throw new InvalidOperationException("Safety no-op validation process did not start.");
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "The independent staged E2E safety no-op actuator failed its startup probe.");
        }

        return safetyExecutable;
    }

    private static string RequiredHostPythonRuntimeDllPath()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-c \"import pathlib,sysconfig; print(pathlib.Path(sysconfig.get_config_var('BINDIR')).joinpath(sysconfig.get_config_var('LDLIBRARY')).resolve())\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Python runtime discovery process did not start.");
        if (!process.WaitForExit(5_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Staged Agent RabbitMQ E2E could not discover the Python runtime DLL.");
        }

        var discovered = process.StandardOutput.ReadLine();
        return !string.IsNullOrWhiteSpace(discovered) && File.Exists(discovered)
            ? Path.GetFullPath(discovered)
            : throw new InvalidOperationException(
                "Staged Agent RabbitMQ E2E requires an installed Python runtime DLL.");
    }

    private static void WriteEvidence(
        StagedPrerequisites prerequisites,
        PublishedPackage package,
        StationJobRequested request,
        StationJobCompleted completion,
        string sqlitePath,
        IReadOnlyCollection<StationJobEventInboxItem> events,
        int firstAgentPid,
        int restartedAgentPid,
        int firstExitCode,
        int restartExitCode,
        long revision,
        int runtimeFinishedExecutionCount)
    {
        var evidencePath = Environment.GetEnvironmentVariable(EvidencePathVariable);
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(
                new
                {
                    schema = "openlineops.staged-agent-rabbitmq-e2e-evidence",
                    schemaVersion = 1,
                    verifiedAtUtc = DateTimeOffset.UtcNow,
                    agentBundleRoot = prerequisites.AgentBundleRoot,
                    samplePluginRoot = prerequisites.SamplePluginRoot,
                    agentExecutable = Path.Combine(
                        prerequisites.AgentBundleRoot,
                        "OpenLineOps.Agent.exe"),
                    broker = new
                    {
                        prerequisites.BrokerUri.Host,
                        prerequisites.BrokerUri.Port,
                        tls = IsTls(prerequisites.BrokerUri)
                    },
                    packageContentSha256 = package.PackageContentSha256,
                    packagePath = package.PackagePath,
                    request.JobId,
                    request.IdempotencyKey,
                    request.AgentId,
                    request.StationId,
                    executionStatus = completion.ExecutionStatus.ToString(),
                    judgement = completion.Judgement.ToString(),
                    eventKinds = events.Select(item => item.Kind).ToArray(),
                    progressPhases = events
                        .Where(item => item.Kind == nameof(StationJobProgressed))
                        .Select(ReadProgressPhase)
                        .ToArray(),
                    sqlitePath,
                    firstAgentPid,
                    restartedAgentPid,
                    firstExitCode,
                    restartExitCode,
                    stationJobRevision = revision,
                    runtimeFinishedExecutionCount,
                    duplicateRedeliveryRejected = true,
                    duplicateAfterRestartRejected = true,
                    cleanShutdownVerified = firstExitCode == 0 && restartExitCode == 0
                },
                EvidenceJsonOptions));
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void DeleteWorkRoot(string root, string packageCacheRoot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        if (Directory.Exists(packageCacheRoot))
        {
            var protector = new ImmutableContentProtector();
            foreach (var contentDirectory in Directory.EnumerateDirectories(packageCacheRoot)
                         .ToArray())
            {
                var leaf = Path.GetFileName(contentDirectory);
                if (leaf.Length == 64
                    && leaf.All(character =>
                        character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    protector.DeleteProtectedInstallation(
                        packageCacheRoot,
                        contentDirectory);
                }
            }

            Directory.Delete(packageCacheRoot);
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }

    private static string RequiredDirectory(string? value, string variableName)
    {
        var text = RequiredText(value, variableName);
        if (!Path.IsPathFullyQualified(text))
        {
            throw new InvalidDataException(
                $"{variableName} must be a canonical absolute directory path.");
        }

        var path = Path.GetFullPath(text);
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException(
                $"Required staged directory does not exist: {path}");
    }

    private static string RequiredText(string? value, string variableName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"{variableName} must be canonical non-empty text.")
            : value;

    private static string RequiredDirectFile(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A staged E2E dependency must be one canonical direct-child file name.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(fileName, normalizedRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), normalizedRoot, comparison))
        {
            throw new InvalidDataException(
                $"Staged E2E file '{fileName}' resolves outside '{normalizedRoot}'.");
        }

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"Required staged E2E file does not exist: {path}",
                path);
    }

    private static bool IsTls(Uri brokerUri) =>
        string.Equals(brokerUri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   "OpenLineOps repository root could not be found.");
    }

    private static string BuildConfiguration() => AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase)
        ? "Release"
        : "Debug";

    private static void Write(
        ProjectApplicationWorkspaceScope scope,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            scope.ApplicationRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ResourceFileName(string kind, string resourceId)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(resourceId)))[..12];
        return $"{kind}-{resourceId}--{hash}.json";
    }

    private sealed record StagedPrerequisites(
        string AgentBundleRoot,
        string SamplePluginRoot,
        Uri BrokerUri);

    private sealed record PublishedPackage(
        string PackageContentSha256,
        string PackagePath,
        string PublicKeyPath);

    private sealed class BrokerTopology : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        private readonly string _jobQueue;
        private readonly string _resultQueue;
        private readonly string[] _safetyQueues;

        private BrokerTopology(
            IConnection connection,
            IChannel channel,
            string jobQueue,
            string resultQueue,
            string[] safetyQueues)
        {
            _connection = connection;
            _channel = channel;
            _jobQueue = jobQueue;
            _resultQueue = resultQueue;
            _safetyQueues = safetyQueues;
        }

        public static async ValueTask<BrokerTopology> CreateAsync(
            Uri brokerUri,
            StationCoordinatorTransportOptions options,
            string agentId,
            string stationId)
        {
            var factory = new ConnectionFactory
            {
                Uri = brokerUri,
                ClientProvidedName = $"openlineops-staged-e2e-topology-{agentId}"
            };
            var connection = await factory.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            try
            {
                await channel.ExchangeDeclareAsync(
                    options.JobExchange,
                    ExchangeType.Direct,
                    durable: true,
                    autoDelete: false);
                await channel.ExchangeDeclareAsync(
                    options.EventExchange,
                    ExchangeType.Topic,
                    durable: true,
                    autoDelete: false);
                var jobQueue = $"openlineops.station.{agentId}.{stationId}.jobs";
                await channel.QueueDeclareAsync(jobQueue, true, false, false);
                await channel.QueueBindAsync(
                    jobQueue,
                    options.JobExchange,
                    $"station.{agentId}.{stationId}");
                await channel.QueueBindAsync(
                    jobQueue,
                    options.JobExchange,
                    $"station.{agentId}.{stationId}.resource-lease-changed");
                var resultQueue =
                    $"openlineops.coordinator.{options.CoordinatorId}.station-results";
                await channel.QueueDeclareAsync(resultQueue, true, false, false);
                foreach (var kind in new[]
                         {
                             nameof(StationJobAccepted),
                             nameof(StationJobProgressed),
                             nameof(StationJobCompleted),
                             nameof(StationJobRecoveryRequired),
                             nameof(MaterialArrived)
                         })
                {
                    await channel.QueueBindAsync(
                        resultQueue,
                        options.EventExchange,
                        StationTransportRoute.EventPattern(kind));
                }

                var safetyQueues = new[]
                {
                    $"openlineops.station.{agentId}.{stationId}.emergency-stop",
                    $"openlineops.station.{agentId}.{stationId}.safe-stop",
                    $"openlineops.station.{agentId}.{stationId}.job-cancel"
                };
                return new BrokerTopology(
                    connection,
                    channel,
                    jobQueue,
                    resultQueue,
                    safetyQueues);
            }
            catch
            {
                await channel.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task WaitForAgentConsumerAsync(
            WindowsAgentProcess agent,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (agent.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent exited before consuming jobs with code {agent.ExitCode}.");
                }

                var state = await _channel.QueueDeclarePassiveAsync(_jobQueue);
                if (state.ConsumerCount > 0)
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                $"Staged Agent did not attach a consumer to '{_jobQueue}'.");
        }

        public async ValueTask DeleteQueuesAsync()
        {
            if (!_channel.IsOpen)
            {
                return;
            }

            foreach (var queue in _safetyQueues.Append(_jobQueue).Append(_resultQueue))
            {
                try
                {
                    await _channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
                }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException exception)
                    when (exception.ShutdownReason?.ReplyCode == 404)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsAgentProcess : IDisposable
    {
        private const uint CreateNewProcessGroup = 0x00000200;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CtrlBreakEvent = 1;

        private readonly Process _process;
        private readonly bool _ownsConsole;

        private WindowsAgentProcess(Process process, bool ownsConsole)
        {
            _process = process;
            _ownsConsole = ownsConsole;
        }

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public static WindowsAgentProcess Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment)
        {
            var ownsConsole = EnsureConsole();
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };
            var commandLine = new StringBuilder($"\"{executablePath}\"");
            var environmentBlock = BuildEnvironmentBlock(environment);
            var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
            try
            {
                if (!CreateProcess(
                        executablePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: false,
                        CreateNewProcessGroup | CreateUnicodeEnvironment,
                        environmentPointer,
                        workingDirectory,
                        ref startupInfo,
                        out var processInformation))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not start staged Agent '{executablePath}'.");
                }

                try
                {
                    return new WindowsAgentProcess(
                        Process.GetProcessById(checked((int)processInformation.ProcessId)),
                        ownsConsole);
                }
                finally
                {
                    _ = CloseHandle(processInformation.Thread);
                    _ = CloseHandle(processInformation.Process);
                }
            }
            catch
            {
                if (ownsConsole)
                {
                    _ = FreeConsole();
                }

                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(environmentPointer);
            }
        }

        public async Task<int> StopCleanlyAsync(TimeSpan timeout)
        {
            if (_process.HasExited)
            {
                return _process.ExitCode;
            }

            if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, checked((uint)_process.Id)))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not signal Ctrl+Break to the staged Agent process group.");
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The staged Agent did not stop cleanly after Ctrl+Break.");
            }

            return _process.ExitCode;
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }

        public void Dispose()
        {
            _process.Dispose();
            if (_ownsConsole)
            {
                _ = FreeConsole();
            }
        }

        private static bool EnsureConsole()
        {
            var processes = new uint[1];
            if (GetConsoleProcessList(processes, 1) != 0)
            {
                return false;
            }

            if (!AllocConsole())
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not allocate a console for staged Agent shutdown control.");
            }

            return true;
        }

        private static string BuildEnvironmentBlock(
            IReadOnlyDictionary<string, string> environment)
        {
            var builder = new StringBuilder();
            foreach (var (key, value) in environment.OrderBy(
                         pair => pair.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (key.Contains('=') || key.Contains('\0') || value.Contains('\0'))
                {
                    throw new InvalidDataException(
                        $"Environment entry '{key}' cannot be represented in a Windows block.");
                }

                builder.Append(key).Append('=').Append(value).Append('\0');
            }

            builder.Append('\0');
            return builder.ToString();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "CreateProcessW requires a writable null-terminated command-line buffer.")]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(
            [Out] uint[] processList,
            uint processCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }
    }
}
