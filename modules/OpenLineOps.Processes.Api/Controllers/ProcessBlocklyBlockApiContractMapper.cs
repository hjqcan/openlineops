using System.Text.Json;
using OpenLineOps.Processes.Api.Models;
using OpenLineOps.Processes.Application.Scripting;
using RegisterApiBlockRequest = OpenLineOps.Processes.Api.Models.RegisterProcessBlocklyBlockDefinitionRequest;
using RegisterApplicationBlockRequest = OpenLineOps.Processes.Application.Scripting.RegisterProcessBlocklyBlockDefinitionRequest;

namespace OpenLineOps.Processes.Api.Controllers;

internal static class ProcessBlocklyBlockApiContractMapper
{
    public static RegisterApplicationBlockRequest ToApplicationRequest(RegisterApiBlockRequest request)
    {
        return new RegisterApplicationBlockRequest(
            request.BlockType!,
            request.Category!,
            request.DisplayName!,
            request.BlocklyJson.GetRawText(),
            request.RuntimeActionContractSchemaVersion!,
            request.RuntimeActionContract.GetRawText());
    }

    public static ProcessBlocklyBlockDefinitionResponse ToResponse(
        ProcessBlocklyBlockDefinitionDetails block)
    {
        return new ProcessBlocklyBlockDefinitionResponse(
            block.BlockType,
            block.Category,
            block.DisplayName,
            JsonSerializer.Deserialize<JsonElement>(block.BlocklyJson),
            block.IsBuiltIn,
            block.Version,
            block.CreatedAtUtc,
            block.UpdatedAtUtc,
            block.ExecutionMode,
            block.RuntimeActionContractSchemaVersion!,
            JsonSerializer.Deserialize<JsonElement>(block.RuntimeActionContractJson!),
            block.RuntimeActionContractSha256!);
    }

    public static Dictionary<string, string[]> Validate(RegisterApiBlockRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.BlockType), request.BlockType);
        AddRequired(errors, nameof(request.Category), request.Category);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(
            errors,
            nameof(request.RuntimeActionContractSchemaVersion),
            request.RuntimeActionContractSchemaVersion);

        if (request.BlocklyJson.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            errors[nameof(request.BlocklyJson)] = ["BlocklyJson is required."];
        }

        if (request.RuntimeActionContract.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            errors[nameof(request.RuntimeActionContract)] = ["RuntimeActionContract is required."];
        }

        if (request.UnknownProperties is { Count: > 0 })
        {
            errors[nameof(request.UnknownProperties)] =
            [
                $"Unknown properties are not allowed: {string.Join(", ", request.UnknownProperties.Keys.Order(StringComparer.Ordinal))}."
            ];
        }

        return errors;
    }

    private static void AddRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }
}
