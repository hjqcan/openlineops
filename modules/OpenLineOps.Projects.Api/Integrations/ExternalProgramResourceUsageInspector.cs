using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Application.ExternalPrograms;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ExternalProgramResourceUsageInspector : IExternalProgramResourceUsageInspector
{
    private readonly IProjectProcessDefinitionRepository _processRepository;
    private readonly IProjectProcessBlocklyBlockCatalog _blockCatalog;
    private readonly IProcessFlowIrCompiler _compiler;

    public ExternalProgramResourceUsageInspector(
        IProjectProcessDefinitionRepository processRepository,
        IProjectProcessBlocklyBlockCatalog blockCatalog,
        IProcessFlowIrCompiler compiler)
    {
        _processRepository = processRepository;
        _blockCatalog = blockCatalog;
        _compiler = compiler;
    }

    public async ValueTask<bool> IsReferencedAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var blocks = await _blockCatalog.ListAsync(
                scope.ProjectId,
                scope.ApplicationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (blocks.IsFailure)
        {
            throw new InvalidDataException(
                $"Application Blockly catalog cannot be read: {blocks.Error.Message}");
        }

        var processes = await _processRepository.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = _compiler.Compile(process, blocks.Value);
            if (compilation.IsFailure)
            {
                throw new InvalidDataException(
                    $"Flow {process.Id} cannot be inspected: {compilation.Error.Message}");
            }

            foreach (var reference in compilation.Value.Document.Nodes
                         .SelectMany(node => node.Actions)
                         .Select(action => ExternalProgramResourceContract.ReadReference(action.InputPayload)))
            {
                if (reference.IsMalformed)
                {
                    throw new InvalidDataException(
                        $"Flow {process.Id} contains a malformed external program resource reference.");
                }

                if (string.Equals(reference.ResourceId, resourceId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
