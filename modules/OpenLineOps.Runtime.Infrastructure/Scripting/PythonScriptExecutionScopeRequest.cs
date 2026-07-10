using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed record PythonScriptExecutionScopeRequest(
    string ScriptLanguage,
    string ScriptSourceCode,
    string? ScriptVersion,
    string? InputPayload,
    string SessionId,
    string ProductionRunId,
    string ProductionLineDefinitionId,
    string ProductionStageId,
    int StageSequence,
    string WorkstationId,
    string DutModelId,
    string DutIdentityInputKey,
    string DutIdentityValue,
    string StationId,
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
            request.CommandContext.SessionId.Value.ToString(),
            request.CommandContext.ProductionRunId.Value.ToString("D"),
            request.CommandContext.ProductionLineDefinitionId,
            request.CommandContext.ProductionStageId,
            request.CommandContext.StageSequence,
            request.CommandContext.WorkstationId,
            request.CommandContext.DutIdentity.ModelId,
            request.CommandContext.DutIdentity.InputKey,
            request.CommandContext.DutIdentity.Value,
            request.CommandContext.StationId.Value,
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
