using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Api.Models;
using OpenLineOps.Processes.Application.Scripting;
using RegisterApiBlockRequest = OpenLineOps.Processes.Api.Models.RegisterProcessBlocklyBlockDefinitionRequest;
using RegisterApplicationBlockRequest = OpenLineOps.Processes.Application.Scripting.RegisterProcessBlocklyBlockDefinitionRequest;

namespace OpenLineOps.Processes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.ProcessesV1)]
[Route(OpenLineOpsApiRoutes.ProcessBlocklyBlocks)]
public sealed class ProcessBlocklyBlocksController : ControllerBase
{
    private readonly IProcessBlocklyBlockCatalog _catalog;

    public ProcessBlocklyBlocksController(IProcessBlocklyBlockCatalog catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("{blockType}/versions")]
    [ProducesResponseType<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionResponse>>> ListVersionsAsync(
        string blockType,
        CancellationToken cancellationToken)
    {
        var result = await _catalog
            .ListVersionsAsync(blockType, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpPost]
    [ProducesResponseType<ProcessBlocklyBlockDefinitionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProcessBlocklyBlockDefinitionResponse>> RegisterAsync(
        RegisterApiBlockRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _catalog
            .RegisterAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        return Created($"/api/process-blocks/{response.BlockType}", response);
    }

    internal static RegisterApplicationBlockRequest ToApplicationRequest(
        RegisterApiBlockRequest request)
    {
        return new RegisterApplicationBlockRequest(
            request.BlockType!,
            request.Category!,
            request.DisplayName!,
            request.BlocklyJson.GetRawText(),
            request.RuntimeActionContractSchemaVersion!,
            request.RuntimeActionContract.GetRawText());
    }

    internal static ProcessBlocklyBlockDefinitionResponse ToResponse(
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

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }

    internal static Dictionary<string, string[]> Validate(RegisterApiBlockRequest? request)
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
