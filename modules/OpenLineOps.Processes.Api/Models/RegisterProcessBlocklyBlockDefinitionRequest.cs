using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.Processes.Api.Models;

public sealed record RegisterProcessBlocklyBlockDefinitionRequest(
    string? BlockType,
    string? Category,
    string? DisplayName,
    JsonElement BlocklyJson,
    string? RuntimeActionContractSchemaVersion,
    JsonElement RuntimeActionContract)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; init; }
}
