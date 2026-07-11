using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectReleaseSourceResolverTests
{
    [Theory]
    [InlineData("PluginCommand", true, true)]
    [InlineData("ProcessCommandProvider", true, false)]
    [InlineData("plugincommand", false, false)]
    [InlineData("processcommandprovider", false, false)]
    [InlineData("PLUGINCOMMAND", false, false)]
    [InlineData("PROCESSCOMMANDPROVIDER", false, false)]
    public void PluginProviderKindsRequireCanonicalCase(
        string providerKind,
        bool isPluginProvider,
        bool isDevicePluginProvider)
    {
        Assert.Equal(
            isPluginProvider,
            ProjectReleaseSourceResolver.IsPluginProvider(providerKind));
        Assert.Equal(
            isDevicePluginProvider,
            ProjectReleaseSourceResolver.IsDevicePluginProvider(providerKind));
    }

    [Fact]
    public void ResolveActionCapabilityTargetUsesRequiredCapabilityForSystemTarget()
    {
        var topology = new AutomationTopologyDetails(
            "topology.main",
            "Main",
            DateTimeOffset.UtcNow,
            [new AutomationSystemDetails(
                "system.axis",
                null,
                "Station",
                "Axis",
                "Axis",
                ["motion.axis"],
                [],
                new Dictionary<string, string>())],
            [new CapabilityContractDetails("motion.axis", "Move", "1", null, null, 30, "Normal")],
            [new DriverBindingDetails("binding.axis", "motion.axis", "PluginCommand", "plugin.axis")],
            [],
            []);
        var action = new FlowIrAction(
            "action.move",
            FlowIrActionKind.DeviceCommand,
            "Move",
            "motion.axis",
            "Move",
            new FlowIrTargetReference(FlowIrTargetReferenceKind.System, "system.axis"),
            "{}",
            new FlowIrExecutionPolicy(30_000, 0, FlowIrCancellationMode.Cooperative),
            null,
            new FlowIrSourceTrace(
                "process.main",
                "process.main@1",
                FlowIrSourceElementKind.ProcessNode,
                "node.move",
                null));

        var result = ProjectReleaseSourceResolver.ResolveActionCapabilityTarget(topology, action);

        Assert.True(result.IsSuccess);
        Assert.Equal("motion.axis", result.Value);
        Assert.NotEqual(action.Target.Reference, result.Value);
    }

    [Fact]
    public async Task ResolvePackageDependenciesLocksExactPackageForCompiledModuleTargetAction()
    {
        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            "openlineops-release-package-resolution",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(packageRoot);
            await File.WriteAllTextAsync(
                Path.Combine(packageRoot, "manifest.json"),
                """
                {
                  "id": "plugin.axis",
                  "name": "Axis Provider",
                  "version": "3.2.1",
                  "kind": "ProcessNode",
                  "entryAssembly": "plugin.axis.dll",
                  "entryType": "Plugin.Axis",
                  "contractVersion": "1.0.0",
                  "runtimeIdentifier": "win-x64",
                  "abiVersion": "openlineops.plugin-abi/1",
                  "capabilities": [ "motion.axis" ],
                  "processCommands": [
                    {
                      "id": "axis.move.v3",
                      "capability": "motion.axis",
                      "commandName": "Move",
                      "timeoutMilliseconds": 30000,
                      "maxRetries": 0
                    }
                  ]
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "plugin.axis.dll"), "axis-binary-v3");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "dependency.bin"), "axis-sidecar-v3");
            var topology = new AutomationTopologyDetails(
                "topology.main",
                "Main",
                DateTimeOffset.UtcNow,
                [new AutomationSystemDetails(
                    "system.axis", null, "Station", "Axis", "Axis",
                    ["motion.axis"], [], new Dictionary<string, string>())],
                [new CapabilityContractDetails("motion.axis", "Move", "1", null, null, 30, "Normal")],
                [new DriverBindingDetails(
                    "binding.axis",
                    "motion.axis",
                    "ProcessCommandProvider",
                    "plugin.axis")],
                [],
                []);
            var action = new FlowIrAction(
                "action.move",
                FlowIrActionKind.DeviceCommand,
                "Move",
                "motion.axis",
                "Move",
                new FlowIrTargetReference(FlowIrTargetReferenceKind.System, "system.axis"),
                "{}",
                new FlowIrExecutionPolicy(30_000, 0, FlowIrCancellationMode.Cooperative),
                null,
                new FlowIrSourceTrace(
                    "process.main",
                    "process.main@1",
                    FlowIrSourceElementKind.ProcessNode,
                    "node.move",
                    null));
            var document = new FlowIrDocument(
                FlowIrSchema.Current,
                "process.main",
                "process.main@1",
                "Main",
                "node.move",
                [new FlowIrNode(
                    "node.move",
                    FlowIrNodeKind.Command,
                    "Move",
                    [action],
                    action.Source)],
                [],
                []);
            var resolver = new ProjectReleaseSourceResolver(
                topologyRepository: null!,
                layoutRepository: null!,
                processRepository: null!,
                engineeringRepository: null!,
                blockRepository: null!,
                productionRepository: null!,
                flowIrCompiler: null!,
                flowIrSerializer: null!,
                clock: null!,
                packageCatalog: new FileSystemPluginPackageCatalog(packageRoot));

            var result = await resolver.ResolvePackageDependenciesAsync(
                topology,
                document,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            var dependency = Assert.Single(result.Value);
            Assert.Equal("plugin.axis", dependency.PluginId);
            Assert.Equal("3.2.1", dependency.PackageVersion);
            Assert.Equal("win-x64", dependency.RuntimeIdentifier);
            Assert.Equal(64, dependency.PackageContentSha256.Length);
            Assert.Contains(dependency.Files, file => file.RelativePath == "dependency.bin");
            Assert.Equal("axis.move.v3", Assert.Single(dependency.Commands).CommandDefinitionId);
        }
        finally
        {
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsyncMapsScopedRepositoryStorageFailureToDeterministicValidationError()
    {
        var resolver = new ProjectReleaseSourceResolver(
            new ThrowingTopologyRepository(),
            layoutRepository: null!,
            processRepository: null!,
            engineeringRepository: null!,
            blockRepository: null!,
            productionRepository: null!,
            flowIrCompiler: new ProcessFlowIrCompiler(),
            flowIrSerializer: new FlowIrCanonicalSerializer(),
            clock: null!);

        var result = await resolver.ResolveAsync(
            new ProjectApplicationWorkspaceScope(
                "project.main",
                "application.main",
                Path.Combine(Path.GetTempPath(), "openlineops-release-source-error"),
                "applications/application.main/application.main.oloapp"),
            "topology.main",
            "line.main");

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Projects.ReleaseSourceInvalid", result.Error.Code);
        Assert.Equal("Topology source is unreadable.", result.Error.Message);
    }

    private sealed class ThrowingTopologyRepository : IProjectAutomationTopologyRepository
    {
        public ValueTask<AutomationTopology?> GetByIdAsync(
            ProjectApplicationWorkspaceScope scope,
            AutomationTopologyId topologyId,
            CancellationToken cancellationToken = default)
        {
            throw new IOException("Topology source is unreadable.");
        }

        public ValueTask SaveAsync(
            ProjectApplicationWorkspaceScope scope,
            AutomationTopology topology,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
