using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.Processes.Api.Models;

public sealed record CreateProcessNodeRequest(
    string? NodeId,
    string? Kind,
    string? DisplayName,
    string? RequiredCapability,
    string? CommandName,
    int? TimeoutSeconds,
    string? InputPayload,
    string? BlocklyWorkspaceJson,
    string? ScriptSourceCode,
    string? ScriptVersion)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; init; }
}
