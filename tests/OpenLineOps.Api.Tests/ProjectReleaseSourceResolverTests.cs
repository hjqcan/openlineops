using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectReleaseSourceResolverTests
{
    [Fact]
    public async Task ResolveAsyncMapsScopedRepositoryStorageFailureToDeterministicValidationError()
    {
        var resolver = new ProjectReleaseSourceResolver(
            new ThrowingTopologyRepository(),
            layoutRepository: null!,
            processRepository: null!,
            engineeringRepository: null!,
            blockRepository: null!,
            flowIrCompiler: new ProcessFlowIrCompiler(),
            flowIrSerializer: new FlowIrCanonicalSerializer(),
            clock: null!);

        var result = await resolver.ResolveAsync(
            new ProjectApplicationWorkspaceScope(
                "project.main",
                "application.main",
                Path.Combine(Path.GetTempPath(), "openlineops-release-source-error")),
            "topology.main",
            "process.main",
            "configuration.main");

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
