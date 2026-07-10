using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Tests;

public sealed class ProjectProcessDefinitionCanonicalTokenTests
{
    [Fact]
    public async Task AuthoringRequiresExactCanonicalNodeKindToken()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project",
            "app",
            Path.GetTempPath(),
            "applications/app/app.oloapp");
        var service = new ProjectProcessDefinitionService(
            new ScopeResolver(scope),
            new Repository(),
            new FixedClock(),
            new ScriptValidator());

        var canonical = await service.CreateAsync(
            "project",
            "app",
            CreateRequest("process.canonical", "Start"));
        var caseChanged = await service.CreateAsync(
            "project",
            "app",
            CreateRequest("process.case-changed", "start"));

        Assert.True(canonical.IsSuccess, canonical.Error.Message);
        Assert.True(caseChanged.IsFailure);
        Assert.Equal("Validation.Processes.InvalidNodeKind", caseChanged.Error.Code);
        Assert.Contains("case-sensitive", caseChanged.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AuthoringRejectsExplicitBlankLoopPolicy(string loopPolicy)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project",
            "app",
            Path.GetTempPath(),
            "applications/app/app.oloapp");
        var service = new ProjectProcessDefinitionService(
            new ScopeResolver(scope),
            new Repository(),
            new FixedClock(),
            new ScriptValidator());

        var result = await service.CreateAsync(
            "project",
            "app",
            CreateRequest("process.blank-loop-policy", "Start", loopPolicy));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.InvalidTransitionLoopPolicy", result.Error.Code);
    }

    private static CreateProcessDefinitionRequest CreateRequest(
        string id,
        string startKind,
        string? loopPolicy = null)
    {
        return new CreateProcessDefinitionRequest(
            id,
            $"{id}@1.0.0",
            id,
            [
                new CreateProcessNodeRequest(
                    "start", startKind, "Start", null, null, null, null, null, null, null, null, null),
                new CreateProcessNodeRequest(
                    "end", "End", "End", null, null, null, null, null, null, null, null, null)
            ],
            [new CreateProcessTransitionRequest("start-end", "start", "end", null, loopPolicy, null)]);
    }

    private sealed class ScopeResolver(ProjectApplicationWorkspaceScope scope)
        : IProjectApplicationWorkspaceScopeResolver
    {
        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectApplicationWorkspaceScope?>(
                projectId == scope.ProjectId && applicationId == scope.ApplicationId ? scope : null);
    }

    private sealed class Repository : IProjectProcessDefinitionRepository
    {
        private readonly Dictionary<string, ProcessDefinition> _items = new(StringComparer.Ordinal);

        public ValueTask SaveAsync(
            ProjectApplicationWorkspaceScope scope,
            ProcessDefinition definition,
            CancellationToken cancellationToken = default)
        {
            _items[definition.Id.Value] = definition;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProcessDefinition?> GetByIdAsync(
            ProjectApplicationWorkspaceScope scope,
            ProcessDefinitionId definitionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.GetValueOrDefault(definitionId.Value));

        public ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<ProcessDefinition>>(_items.Values.ToArray());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class ScriptValidator : IProcessScriptDefinitionValidator
    {
        public ValueTask<ProcessScriptValidationReport> ValidateAsync(
            OpenLineOps.Processes.Domain.Nodes.ProcessNode node,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ProcessScriptValidationReport.Valid);
    }
}
