using OpenLineOps.Processes.Domain.Nodes;

namespace OpenLineOps.Processes.Application.Scripting;

public interface IProcessScriptDefinitionValidator
{
    ValueTask<ProcessScriptValidationReport> ValidateAsync(
        ProcessNode node,
        CancellationToken cancellationToken = default);
}
