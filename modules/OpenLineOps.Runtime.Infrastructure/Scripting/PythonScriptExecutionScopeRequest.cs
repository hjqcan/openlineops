using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed record PythonScriptExecutionScopeRequest(
    string ScriptLanguage,
    string ScriptSourceCode,
    string? ScriptVersion,
    string? InputPayload,
    string SessionId,
    string StationId,
    string ConfigurationSnapshotId,
    string NodeId,
    string CommandId)
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
            request.CommandContext.StationId.Value,
            request.CommandContext.ConfigurationSnapshotId.Value,
            request.CommandContext.NodeId.Value,
            request.CommandContext.CommandId.Value.ToString());
    }
}
