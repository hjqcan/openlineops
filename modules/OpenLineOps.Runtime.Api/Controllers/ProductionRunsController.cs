using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.ProductionRuns)]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class ProductionRunsController(
    IProductionRunRepository repository,
    IProductionRunCoordinator coordinator) : ControllerBase
{
    [HttpGet("{productionRunId}")]
    [ProducesResponseType<ProductionRunReadModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductionRunReadModel>> GetByIdAsync(
        string productionRunId,
        CancellationToken cancellationToken)
    {
        if (!TryParseRunId(productionRunId, out var runId))
        {
            return BadRequest();
        }

        var entry = await repository.GetByIdAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound()
            : Ok(ProductionRunReadModelMapper.ToReadModel(entry.Run.ToSnapshot()));
    }

    [HttpPost("{productionRunId}/commands/{command}")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<ProductionRunReadModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionRunReadModel>> CommandAsync(
        string productionRunId,
        string command,
        ProductionRunCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRunId(productionRunId, out var runId)
            || !TryParseCommand(command, out var parsedCommand))
        {
            return BadRequest();
        }

        ProductionRunCommandRequest applicationRequest;
        try
        {
            applicationRequest = new ProductionRunCommandRequest(
                parsedCommand,
                User.GetRequiredActorId(),
                request.Reason,
                request.OperationId,
                ToRecoveryDecision(parsedCommand, request));
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }

        var result = await coordinator.CommandAsync(runId, applicationRequest, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(ProductionRunReadModelMapper.ToReadModel(result.Value));
        }

        var problem = new ProblemDetails { Title = result.Error.Code, Detail = result.Error.Message };
        if (result.Error.Code.StartsWith("NotFound.", StringComparison.Ordinal))
        {
            problem.Status = StatusCodes.Status404NotFound;
            return NotFound(problem);
        }

        if (result.Error.Code.StartsWith("Validation.", StringComparison.Ordinal))
        {
            problem.Status = StatusCodes.Status400BadRequest;
            return BadRequest(problem);
        }

        problem.Status = StatusCodes.Status409Conflict;
        return Conflict(problem);
    }

    private static bool TryParseRunId(string value, out ProductionRunId runId)
    {
        if (Guid.TryParseExact(value, "D", out var parsed)
            && parsed != Guid.Empty
            && string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            runId = new ProductionRunId(parsed);
            return true;
        }

        runId = default;
        return false;
    }

    private static bool TryParseCommand(string value, out ProductionRunCommand command) =>
        Enum.TryParse(value, ignoreCase: false, out command)
        && Enum.IsDefined(command)
        && string.Equals(command.ToString(), value, StringComparison.Ordinal);

    private ProductionRecoveryDecision? ToRecoveryDecision(
        ProductionRunCommand command,
        ProductionRunCommandApiRequest request)
    {
        if (request.RecoveryDecision is null)
        {
            return null;
        }

        var body = request.RecoveryDecision;
        if (!Guid.TryParseExact(body.DecisionId, "D", out var decisionId)
            || decisionId == Guid.Empty
            || !string.Equals(decisionId.ToString("D"), body.DecisionId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Recovery Decision id must be one non-empty lowercase D-format UUID.",
                nameof(request));
        }

        if (!DateTimeOffset.TryParseExact(
                body.DecidedAtUtc,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var decidedAtUtc)
            || decidedAtUtc.Offset != TimeSpan.Zero
            || !string.Equals(
                decidedAtUtc.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture),
                body.DecidedAtUtc,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Recovery Decision timestamp must use canonical ISO 8601 UTC milliseconds.",
                nameof(request));
        }

        var kind = command switch
        {
            ProductionRunCommand.Reconcile => ProductionRecoveryDecisionKind.Reconcile,
            ProductionRunCommand.Retry => ProductionRecoveryDecisionKind.Retry,
            ProductionRunCommand.Abort => ProductionRecoveryDecisionKind.Abort,
            ProductionRunCommand.Scrap => ProductionRecoveryDecisionKind.Scrap,
            _ => throw new ArgumentException(
                $"{command} cannot carry a Recovery Decision.",
                nameof(request))
        };
        ResultJudgement? judgement = body.ObservedJudgement is null
            ? null
            : ParseCanonicalEnum<ResultJudgement>(
                body.ObservedJudgement,
                "Recovery Decision observed judgement");
        var outputs = (body.ObservedOutputs
                ?? new Dictionary<string, ProductionContextValueApiRequest>())
            .ToDictionary(
                static output => output.Key,
                output =>
                {
                    var value = output.Value
                        ?? throw new ArgumentException(
                            $"Recovery Decision output {output.Key} cannot be null.",
                            nameof(request));
                    return new ProductionContextValue(
                        ParseCanonicalEnum<ProductionContextValueKind>(
                            value.Kind,
                            $"Recovery Decision output {output.Key} kind"),
                        value.CanonicalValue);
                },
                StringComparer.Ordinal);

        return new ProductionRecoveryDecision(
            decisionId,
            kind,
            User.GetRequiredActorId(),
            request.Reason
                ?? throw new ArgumentException(
                    "A Recovery Decision requires an operator reason.",
                    nameof(request)),
            body.EvidenceReference,
            decidedAtUtc,
            body.OperationRunId,
            body.OperationId,
            judgement,
            outputs);
    }

    private static TEnum ParseCanonicalEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException($"{fieldName} '{value}' is invalid.");
}
