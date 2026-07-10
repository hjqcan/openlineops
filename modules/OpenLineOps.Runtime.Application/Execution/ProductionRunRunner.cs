using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionRunRunner : IProductionRunRunner
{
    private readonly IProductionRunRepository _runRepository;
    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IRuntimeDomainEventPublisher _domainEventPublisher;
    private readonly IRuntimeIdProvider _idProvider;
    private readonly IClock _clock;

    public ProductionRunRunner(
        IProductionRunRepository runRepository,
        IRuntimeSessionRepository sessionRepository,
        IRuntimeSessionRunner sessionRunner,
        IRuntimeDomainEventPublisher domainEventPublisher,
        IRuntimeIdProvider idProvider,
        IClock clock)
    {
        _runRepository = runRepository;
        _sessionRepository = sessionRepository;
        _sessionRunner = sessionRunner;
        _domainEventPublisher = domainEventPublisher;
        _idProvider = idProvider;
        _clock = clock;
    }

    public async ValueTask<Result<ProductionRunRunResult>> RunAsync(
        StartProductionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return Result.Failure<ProductionRunRunResult>(validationError);
        }

        var run = ProductionRun.Create(
            request.RunId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            request.DutIdentity,
            request.BatchId,
            request.FixtureId,
            request.DeviceId,
            request.ActorId,
            _clock.UtcNow,
            request.Stages.Select(ToDefinition));
        var revision = 0L;

        try
        {
            if (!await TryPersistNewAndPublishAsync(run, cancellationToken).ConfigureAwait(false))
            {
                var existing = await _runRepository
                    .GetByIdAsync(request.RunId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null && HasSameImmutableIdentity(existing.Run, request))
                {
                    return Result.Success(new ProductionRunRunResult(existing.Run.ToSnapshot()));
                }

                return Result.Failure<ProductionRunRunResult>(ApplicationError.Conflict(
                    "Runtime.ProductionRunIdIdentityMismatch",
                    $"Production run id {request.RunId} already belongs to a different immutable run identity."));
            }

            var startResult = run.Start(_clock.UtcNow);
            if (!startResult.Succeeded)
            {
                return ToApplicationFailure(startResult);
            }

            revision = await PersistAndPublishAsync(run, revision, cancellationToken)
                .ConfigureAwait(false);

            foreach (var stagePlan in request.Stages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sessionId = _idProvider.NewSessionId();
                var stageStartResult = run.StartStage(stagePlan.StageId, sessionId, _clock.UtcNow);
                if (!stageStartResult.Succeeded)
                {
                    return ToApplicationFailure(stageStartResult);
                }

                // The run-to-session link is durable before any device or script execution begins.
                revision = await PersistAndPublishAsync(run, revision, cancellationToken)
                    .ConfigureAwait(false);

                Result<RuntimeSessionRunResult> sessionResult;
                try
                {
                    sessionResult = await _sessionRunner.RunAsync(
                        new StartRuntimeSessionRequest(
                            sessionId,
                            stagePlan.StationId,
                            stagePlan.ConfigurationSnapshotId,
                            stagePlan.RecipeSnapshotId,
                            stagePlan.FrozenExecutableProcess,
                            CreateSessionTraceMetadata(run, stagePlan)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    const string code = "Runtime.ProductionStageExecutionCanceledUnexpectedly";
                    const string reason =
                        "Runtime session boundary canceled without a Production Run cancellation request.";
                    var recovered = await RecoverSessionBoundaryAsync(
                            sessionId,
                            code,
                            reason)
                        .ConfigureAwait(false);
                    if (recovered is not null)
                    {
                        sessionResult = Result.Success(recovered);
                    }
                    else
                    {
                        var failure = run.FailStage(
                            stagePlan.StageId,
                            code,
                            reason,
                            0,
                            0,
                            0,
                            _clock.UtcNow);
                        if (!failure.Succeeded)
                        {
                            return ToApplicationFailure(failure);
                        }

                        revision = await PersistAndPublishAsync(
                                run,
                                revision,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                                                   and not OutOfMemoryException)
                {
                    const string code = "Runtime.ProductionStageExecutionFault";
                    var reason =
                        $"Runtime session boundary threw {exception.GetType().FullName ?? exception.GetType().Name}.";
                    var recovered = await RecoverSessionBoundaryAsync(
                            sessionId,
                            code,
                            reason)
                        .ConfigureAwait(false);
                    if (recovered is not null)
                    {
                        sessionResult = Result.Success(recovered);
                    }
                    else
                    {
                        var failure = run.FailStage(
                            stagePlan.StageId,
                            code,
                            reason,
                            0,
                            0,
                            0,
                            _clock.UtcNow);
                        if (!failure.Succeeded)
                        {
                            return ToApplicationFailure(failure);
                        }

                        revision = await PersistAndPublishAsync(
                                run,
                                revision,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        break;
                    }
                }

                if (sessionResult.IsFailure)
                {
                    var failResult = run.FailStage(
                        stagePlan.StageId,
                        sessionResult.Error.Code,
                        sessionResult.Error.Message,
                        0,
                        0,
                        0,
                        _clock.UtcNow);
                    if (!failResult.Succeeded)
                    {
                        return ToApplicationFailure(failResult);
                    }

                    revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                }

                var sessionStatus = sessionResult.Value.Status;
                if (sessionStatus == RuntimeSessionStatus.Completed)
                {
                    var completeResult = run.CompleteStage(
                        stagePlan.StageId,
                        sessionResult.Value.CompletedSteps,
                        sessionResult.Value.CommandCount,
                        sessionResult.Value.IncidentCount,
                        _clock.UtcNow);
                    if (!completeResult.Succeeded)
                    {
                        return ToApplicationFailure(completeResult);
                    }

                    revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                if (sessionStatus is RuntimeSessionStatus.Canceled or RuntimeSessionStatus.Stopped)
                {
                    var cancelResult = run.Cancel(
                        $"Runtime session {sessionResult.Value.SessionId} ended as {sessionStatus}.",
                        sessionResult.Value.CompletedSteps,
                        sessionResult.Value.CommandCount,
                        sessionResult.Value.IncidentCount,
                        _clock.UtcNow);
                    if (!cancelResult.Succeeded)
                    {
                        return ToApplicationFailure(cancelResult);
                    }

                    revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                }

                var stageFailure = run.FailStage(
                    stagePlan.StageId,
                    "Runtime.ProductionStageSessionFailed",
                    $"Runtime session {sessionResult.Value.SessionId} ended as {sessionStatus}.",
                    sessionResult.Value.CompletedSteps,
                    sessionResult.Value.CommandCount,
                    sessionResult.Value.IncidentCount,
                    _clock.UtcNow);
                if (!stageFailure.Succeeded)
                {
                    return ToApplicationFailure(stageFailure);
                }

                revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!run.IsTerminal)
            {
                var cancelResult = run.Cancel(
                    "Production run execution was canceled.",
                    0,
                    0,
                    0,
                    _clock.UtcNow);
                if (cancelResult.Succeeded)
                {
                    revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }

        return Result.Success(new ProductionRunRunResult(run.ToSnapshot()));
    }

    private async ValueTask<long> PersistAndPublishAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var domainEvents = run.DomainEvents.ToArray();
        var nextRevision = await _runRepository
            .SaveAsync(run, expectedRevision, cancellationToken)
            .ConfigureAwait(false);

        if (domainEvents.Length > 0)
        {
            await _domainEventPublisher.PublishAsync(domainEvents, cancellationToken).ConfigureAwait(false);
            run.ClearDomainEvents();
        }

        return nextRevision;
    }

    private async ValueTask<RuntimeSessionRunResult?> RecoverSessionBoundaryAsync(
        RuntimeSessionId sessionId,
        string failureCode,
        string failureReason)
    {
        var session = await _sessionRepository
            .GetByIdAsync(sessionId, CancellationToken.None)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        if (!session.IsTerminal)
        {
            var transition = session.Fail(_clock.UtcNow, failureCode, failureReason);
            if (!transition.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not terminalize Runtime session {session.Id} after boundary failure: "
                    + $"{transition.Code} {transition.Message}");
            }

            var events = session.DomainEvents.ToArray();
            await _sessionRepository.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            if (events.Length > 0)
            {
                await _domainEventPublisher
                    .PublishAsync(events, CancellationToken.None)
                    .ConfigureAwait(false);
                session.ClearDomainEvents();
            }
        }

        return new RuntimeSessionRunResult(
            session.Id,
            session.ConfigurationSnapshotId,
            session.Status,
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed),
            session.Commands.Count,
            session.Incidents.Count);
    }

    private async ValueTask<bool> TryPersistNewAndPublishAsync(
        ProductionRun run,
        CancellationToken cancellationToken)
    {
        var domainEvents = run.DomainEvents.ToArray();
        if (!await _runRepository.TryAddAsync(run, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (domainEvents.Length > 0)
        {
            await _domainEventPublisher.PublishAsync(domainEvents, cancellationToken).ConfigureAwait(false);
            run.ClearDomainEvents();
        }

        return true;
    }

    private static RuntimeSessionTraceMetadata CreateSessionTraceMetadata(
        ProductionRun run,
        ProductionStageExecutionPlan stagePlan)
    {
        return new RuntimeSessionTraceMetadata(
            run.Id,
            run.ProductionLineDefinitionId,
            stagePlan.StageId,
            stagePlan.Sequence,
            stagePlan.WorkstationId,
            run.DutIdentity,
            run.BatchId,
            run.FixtureId,
            run.DeviceId,
            run.ActorId,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId);
    }

    private static ProductionStageRunDefinition ToDefinition(ProductionStageExecutionPlan plan)
    {
        return new ProductionStageRunDefinition(
            plan.StageId,
            plan.Sequence,
            plan.WorkstationId,
            plan.StationId,
            plan.FrozenExecutableProcess.ProcessDefinitionId,
            plan.FrozenExecutableProcess.ProcessVersionId,
            plan.ConfigurationSnapshotId,
            plan.RecipeSnapshotId);
    }

    private static ApplicationError? Validate(StartProductionRunRequest request)
    {
        if (request.Stages.Count == 0)
        {
            return ApplicationError.Validation(
                "Runtime.ProductionRunHasNoStages",
                "A production run must contain at least one stage execution plan.");
        }

        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Stages.Count; index++)
        {
            var stage = request.Stages[index];
            var expectedSequence = index + 1;
            if (stage.Sequence != expectedSequence)
            {
                return ApplicationError.Validation(
                    "Runtime.ProductionStageSequenceInvalid",
                    $"Stage execution plans must be ordered contiguously from 1; expected {expectedSequence}.");
            }

            if (!stageIds.Add(stage.StageId))
            {
                return ApplicationError.Validation(
                    "Runtime.ProductionStageIdDuplicate",
                    $"Production stage id {stage.StageId} is duplicated.");
            }

            if (!string.Equals(
                stage.ProductionLineDefinitionId,
                request.ProductionLineDefinitionId,
                StringComparison.Ordinal))
            {
                return ApplicationError.Validation(
                    "Runtime.ProductionStageLineMismatch",
                    $"Production stage {stage.StageId} belongs to line "
                    + $"{stage.ProductionLineDefinitionId}, not {request.ProductionLineDefinitionId}.");
            }
        }

        return null;
    }

    private static bool HasSameImmutableIdentity(
        ProductionRun existing,
        StartProductionRunRequest request)
    {
        if (!string.Equals(existing.ProjectId, request.ProjectId, StringComparison.Ordinal)
            || !string.Equals(existing.ApplicationId, request.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(
                existing.ProjectSnapshotId,
                request.ProjectSnapshotId,
                StringComparison.Ordinal)
            || !string.Equals(existing.TopologyId, request.TopologyId, StringComparison.Ordinal)
            || !string.Equals(
                existing.ProductionLineDefinitionId,
                request.ProductionLineDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.DutIdentity.ModelId,
                request.DutIdentity.ModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.DutIdentity.InputKey,
                request.DutIdentity.InputKey,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.DutIdentity.Value,
                request.DutIdentity.Value,
                StringComparison.Ordinal)
            || !string.Equals(existing.ActorId, request.ActorId, StringComparison.Ordinal)
            || !string.Equals(existing.BatchId, request.BatchId, StringComparison.Ordinal)
            || !string.Equals(existing.FixtureId, request.FixtureId, StringComparison.Ordinal)
            || !string.Equals(existing.DeviceId, request.DeviceId, StringComparison.Ordinal)
            || existing.Stages.Count != request.Stages.Count)
        {
            return false;
        }

        return existing.Stages
            .OrderBy(stage => stage.Sequence)
            .Zip(request.Stages.OrderBy(stage => stage.Sequence))
            .All(pair => string.Equals(pair.First.StageId, pair.Second.StageId, StringComparison.Ordinal)
                && pair.First.Sequence == pair.Second.Sequence
                && string.Equals(
                    pair.First.WorkstationId,
                    pair.Second.WorkstationId,
                    StringComparison.Ordinal)
                && pair.First.StationId == pair.Second.StationId
                && pair.First.ProcessDefinitionId
                    == pair.Second.FrozenExecutableProcess.ProcessDefinitionId
                && pair.First.ProcessVersionId
                    == pair.Second.FrozenExecutableProcess.ProcessVersionId
                && pair.First.ConfigurationSnapshotId == pair.Second.ConfigurationSnapshotId
                && pair.First.RecipeSnapshotId == pair.Second.RecipeSnapshotId);
    }

    private static Result<ProductionRunRunResult> ToApplicationFailure(RuntimeOperationResult result)
    {
        return Result.Failure<ProductionRunRunResult>(
            ApplicationError.Conflict(result.Code, result.Message));
    }
}
