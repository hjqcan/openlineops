using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed record PythonScriptExecutionScopeRequest(
    string ScriptLanguage,
    string ScriptSourceCode,
    string? ScriptVersion,
    string? InputPayload,
    string ProductionInputsJson,
    string SessionId,
    string ProductionRunId,
    string ProductionLineDefinitionId,
    string OperationId,
    int OperationAttempt,
    string StationSystemId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string ConfigurationSnapshotId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string NodeId,
    string CommandId,
    string ActionId,
    string TargetCapability,
    string TargetKind,
    string TargetId,
    string CommandName)
{
    public static PythonScriptExecutionScopeRequest FromRuntimeRequest(
        RuntimeScriptExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new PythonScriptExecutionScopeRequest(
            request.ScriptLanguage,
            request.ScriptSourceCode,
            request.ScriptVersion,
            request.InputPayload,
            ProductionContextDocument.WriteResolvedValues(
                request.CommandContext.ProductionInputs).GetRawText(),
            request.CommandContext.SessionId.Value.ToString(),
            request.CommandContext.ProductionRunId.Value.ToString("D"),
            request.CommandContext.ProductionLineDefinitionId,
            request.CommandContext.OperationId,
            request.CommandContext.OperationAttempt,
            request.CommandContext.StationSystemId,
            request.CommandContext.ProductionUnitIdentity.ModelId,
            request.CommandContext.ProductionUnitIdentity.InputKey,
            request.CommandContext.ProductionUnitIdentity.Value,
            request.CommandContext.ConfigurationSnapshotId.Value,
            request.CommandContext.ProjectId,
            request.CommandContext.ApplicationId,
            request.CommandContext.ProjectSnapshotId,
            request.CommandContext.NodeId.Value,
            request.CommandContext.CommandId.Value.ToString("D"),
            request.CommandContext.ActionId.Value,
            request.CommandContext.TargetCapability.Value,
            request.CommandContext.TargetKind,
            request.CommandContext.TargetId,
            request.CommandContext.CommandName);
    }
}
