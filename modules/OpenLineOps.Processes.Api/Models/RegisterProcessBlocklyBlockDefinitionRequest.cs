using System.Text.Json;

namespace OpenLineOps.Processes.Api.Models;

public sealed record RegisterProcessBlocklyBlockDefinitionRequest(
    string? BlockType,
    string? Category,
    string? DisplayName,
    JsonElement BlocklyJson,
    string? RuntimeActionContractSchemaVersion,
    JsonElement RuntimeActionContract);
