using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class FileSystemProjectProcessDefinitionRepository : IProjectProcessDefinitionRepository
{
    public async ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        ProcessDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var document = await ProjectProcessSourceMapper
            .FromAggregateAsync(scope, definition, cancellationToken)
            .ConfigureAwait(false);
        var flowPath = ProjectProcessResourcePath.GetFlowPath(scope, definition.Id.Value);

        // Artifacts are content-addressed and written by the mapper first. The
        // atomic flow.json replacement is the commit pointer for the whole flow.
        await ProjectProcessResourceFileStore
            .SaveJsonAsync(flowPath, document, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ProcessDefinition?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        ProcessDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var flowPath = ProjectProcessResourcePath.GetFlowPath(scope, definitionId.Value);
        var document = await ProjectProcessResourceFileStore
            .LoadJsonAsync<ProjectProcessFlowDocument>(flowPath, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (!string.Equals(document.ProcessDefinitionId, definitionId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project process resource '{flowPath}' contains id {document.ProcessDefinitionId}, not {definitionId.Value}.");
        }

        return await ProjectProcessSourceMapper
            .ToAggregateAsync(scope, Path.GetDirectoryName(flowPath)!, document, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var flowsDirectory = ProjectProcessResourcePath.GetFlowsDirectory(scope);
        if (!Directory.Exists(flowsDirectory))
        {
            return [];
        }

        var definitions = new List<ProcessDefinition>();
        foreach (var flowPath in Directory.EnumerateFiles(
                     flowsDirectory,
                     "flow.json",
                     SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(flowsDirectory, flowPath);
            if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length != 2)
            {
                continue;
            }

            var document = await ProjectProcessResourceFileStore
                .LoadJsonAsync<ProjectProcessFlowDocument>(flowPath, cancellationToken)
                .ConfigureAwait(false);
            if (document is null)
            {
                throw new InvalidDataException($"Project process resource '{flowPath}' is empty.");
            }

            definitions.Add(await ProjectProcessSourceMapper
                .ToAggregateAsync(scope, Path.GetDirectoryName(flowPath)!, document, cancellationToken)
                .ConfigureAwait(false));
        }

        return definitions;
    }
}
