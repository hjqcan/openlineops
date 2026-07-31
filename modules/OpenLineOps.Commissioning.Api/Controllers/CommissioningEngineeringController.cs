using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Commissioning.Api.Mapping;
using OpenLineOps.Commissioning.Api.Models;
using OpenLineOps.Commissioning.Application.Contracts;
using OpenLineOps.Commissioning.Application.Services;
using OpenLineOps.Commissioning.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Commissioning.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Commissioning)]
[Route("api/stations/{stationId}/commissioning/sessions")]
[Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
public sealed class CommissioningEngineeringController(
    ICommissioningService commissioningService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>> StartAsync(
        string stationId,
        StartCommissioningSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await commissioningService.StartAsync(
                    new StartCommissioningSessionCommand(
                        request.SessionId,
                        stationId,
                        User.GetRequiredActorId(),
                        request.AuthorizedRole,
                        request.LeaseId,
                        request.FencingToken,
                        TimeSpan.FromSeconds(request.DurationSeconds),
                        ParseCapabilities(request.Capabilities)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(result.Error);
            }

            var response = CommissioningApiMapper.ToResponse(result.Value);
            return Created(
                $"/api/stations/{response.StationId}/commissioning/sessions/"
                + response.SessionId,
                response);
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpGet("{sessionId}")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommissioningSessionResponse>> GetAsync(
        string stationId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await GetStationSessionAsync(
                        stationId,
                        sessionId,
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("{sessionId}/lease-renewal")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>> RenewLeaseAsync(
        string stationId,
        string sessionId,
        RenewCommissioningLeaseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAsync(
                    stationId,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await commissioningService.RenewLeaseAsync(
                        new RenewCommissioningLeaseCommand(
                            sessionId,
                            User.GetRequiredActorId(),
                            request.FencingToken,
                            TimeSpan.FromSeconds(request.DurationSeconds)),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("{sessionId}/diagnostics/access")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommissioningSessionResponse>> RecordDiagnosticAccessAsync(
        string stationId,
        string sessionId,
        CommissioningSubjectRequest request,
        CancellationToken cancellationToken) =>
        MutateSubjectAsync(
            stationId,
            sessionId,
            request.FencingToken,
            request.SubjectId,
            commissioningService.RecordDiagnosticAccessAsync,
            cancellationToken);

    [HttpPost("{sessionId}/signal-monitoring")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommissioningSessionResponse>> StartSignalMonitoringAsync(
        string stationId,
        string sessionId,
        CommissioningSubjectRequest request,
        CancellationToken cancellationToken) =>
        MutateSubjectAsync(
            stationId,
            sessionId,
            request.FencingToken,
            request.SubjectId,
            commissioningService.StartSignalMonitoringAsync,
            cancellationToken);

    [HttpPost("{sessionId}/manual-commands/authorization")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>>
        AuthorizeManualCommandAsync(
            string stationId,
            string sessionId,
            AuthorizeCommissioningManualCommandRequest request,
            CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAtFenceAsync(
                    stationId,
                    sessionId,
                    request.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await commissioningService.AuthorizeManualCommandAsync(
                        new AuthorizeCommissioningManualCommand(
                            sessionId,
                            User.GetRequiredActorId(),
                            request.CommandId,
                            ParseExact<StationMode>(
                                request.StationMode,
                                nameof(request.StationMode)),
                            ParseExact<CommissioningActionSafetyClass>(
                                request.SafetyClass,
                                nameof(request.SafetyClass)),
                            request.DebugActionWhitelisted),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("{sessionId}/flow-steps/authorization")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<CommissioningSessionResponse>> AuthorizeFlowStepAsync(
        string stationId,
        string sessionId,
        CommissioningSubjectRequest request,
        CancellationToken cancellationToken) =>
        MutateSubjectAsync(
            stationId,
            sessionId,
            request.FencingToken,
            request.SubjectId,
            commissioningService.AuthorizeFlowStepAsync,
            cancellationToken);

    [HttpPost("{sessionId}/breakpoints")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>> SetBreakpointAsync(
        string stationId,
        string sessionId,
        SetCommissioningBreakpointRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAtFenceAsync(
                    stationId,
                    sessionId,
                    request.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await commissioningService.SetBreakpointAsync(
                        new SetCommissioningBreakpointCommand(
                            sessionId,
                            User.GetRequiredActorId(),
                            request.NodeId,
                            ParseExact<StationMode>(
                                request.StationMode,
                                nameof(request.StationMode)),
                            request.DebugActionWhitelisted),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("{sessionId}/recovery/disposition")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>> ResolveRecoveryAsync(
        string stationId,
        string sessionId,
        ResolveCommissioningRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAsync(
                    stationId,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsSuccess
                && stationSession.Value.FencingToken != request.FencingToken)
            {
                stationSession = StaleFence(
                    stationSession.Value,
                    request.FencingToken);
            }

            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await commissioningService.ResolveRecoveryAsync(
                        new ResolveCommissioningRecoveryCommand(
                            sessionId,
                            User.GetRequiredActorId(),
                            ParseExact<CommissioningRecoveryDisposition>(
                                request.Disposition,
                                nameof(request.Disposition))),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("{sessionId}/completion")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>> CompleteAsync(
        string stationId,
        string sessionId,
        CompleteCommissioningSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAtFenceAsync(
                    stationId,
                    sessionId,
                    request.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await commissioningService.CompleteAsync(
                        sessionId,
                        User.GetRequiredActorId(),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("{sessionId}/abort")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>> AbortAsync(
        string stationId,
        string sessionId,
        AbortCommissioningSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAtFenceAsync(
                    stationId,
                    sessionId,
                    request.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await commissioningService.AbortAsync(
                        new AbortCommissioningSessionCommand(
                            sessionId,
                            User.GetRequiredActorId(),
                            request.Reason),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    private async Task<ActionResult<CommissioningSessionResponse>> MutateSubjectAsync(
        string stationId,
        string sessionId,
        long fencingToken,
        string subjectId,
        Func<
            CommissioningSubjectCommand,
            CancellationToken,
            ValueTask<Result<CommissioningSessionDetails>>> mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationSession = await GetStationSessionAtFenceAsync(
                    stationId,
                    sessionId,
                    fencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationSession.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(stationSession.Error);
            }

            return ToActionResult(
                await mutation(
                        new CommissioningSubjectCommand(
                            sessionId,
                            User.GetRequiredActorId(),
                            subjectId),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    private async ValueTask<Result<CommissioningSessionDetails>>
        GetStationSessionAtFenceAsync(
            string stationId,
            string sessionId,
            long fencingToken,
            CancellationToken cancellationToken)
    {
        var result = await GetStationSessionAsync(
                stationId,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure || result.Value.FencingToken == fencingToken)
        {
            return result;
        }

        return StaleFence(result.Value, fencingToken);
    }

    private static Result<CommissioningSessionDetails> StaleFence(
        CommissioningSessionDetails session,
        long fencingToken)
    {
        return Result.Failure<CommissioningSessionDetails>(
            ApplicationError.Conflict(
                "Commissioning.StaleFencingToken",
                $"Commissioning command fencing token {fencingToken} does not match "
                + $"the active lease token {session.FencingToken}."));
    }

    private async ValueTask<Result<CommissioningSessionDetails>>
        GetStationSessionAsync(
            string stationId,
            string sessionId,
            CancellationToken cancellationToken)
    {
        var result = await commissioningService
            .GetAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure
            || string.Equals(
                result.Value.StationId,
                stationId,
                StringComparison.Ordinal))
        {
            return result;
        }

        return Result.Failure<CommissioningSessionDetails>(
            ApplicationError.NotFound(
                "Commissioning.SessionNotFound",
                $"Commissioning session {sessionId} was not found for station {stationId}."));
    }

    private static CommissioningCapability[] ParseCapabilities(
        IReadOnlyCollection<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count == 0)
        {
            throw new ArgumentException(
                "At least one commissioning capability is required.",
                nameof(capabilities));
        }

        var parsed = capabilities
            .Select(capability => ParseExact<CommissioningCapability>(
                capability,
                nameof(capabilities)))
            .ToArray();
        if (parsed.Distinct().Count() != parsed.Length)
        {
            throw new ArgumentException(
                "Commissioning capabilities cannot be duplicated.",
                nameof(capabilities));
        }

        return parsed;
    }

    private static TEnum ParseExact<TEnum>(string value, string parameterName)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
                ? parsed
                : throw new ArgumentException(
                    $"Value '{value}' is not a supported {typeof(TEnum).Name}.",
                    parameterName);
    }

    private static ActionResult<CommissioningSessionResponse> ToActionResult(
        Result<CommissioningSessionDetails> result)
    {
        return result.IsSuccess
            ? new OkObjectResult(CommissioningApiMapper.ToResponse(result.Value))
            : CommissioningApiMapper.ToProblem(result.Error);
    }

    private ActionResult<CommissioningSessionResponse> Validation(
        ArgumentException exception)
    {
        return BadRequest(CommissioningApiMapper.Problem(
            StatusCodes.Status400BadRequest,
            "Validation.Commissioning.Request",
            exception.Message));
    }
}
