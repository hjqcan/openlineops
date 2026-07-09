using System.Text.Json;

namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessBlocklyBlockDefinitionResponse(
    string BlockType,
    string Category,
    string DisplayName,
    JsonElement BlocklyJson,
    string PythonCodeTemplate,
    bool IsBuiltIn,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
