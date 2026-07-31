using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class InProcessStationOperationDispatcher(
    IRuntimeSessionRunner sessionRunner,
    IRuntimeSessionRepository sessionRepository,
    InProcessStationOperationRegistry executions) : IStationOperationDispatcher
{
    public async ValueTask<StationOperationDispatchResult> DispatchAsync(
        StationOperationDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var execution = executions.Register(request.IdempotencyKey, cancellationToken);
        Result<RuntimeSessionRunResult> sessionResult;
        try
        {
            sessionResult = await sessionRunner.RunAsync(
                new StartRuntimeSessionRequest(
                    request.RuntimeSessionId,
                    request.ExecutionPlan.Definition.StationId,
                    request.ExecutionPlan.Definition.ConfigurationSnapshotId,
                    request.ExecutionPlan.Definition.RecipeSnapshotId,
                    request.ExecutionPlan.FrozenExecutableProcess,
                    request.Inputs,
                    CreateTraceMetadata(request)),
                execution.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution.CancelRequested)
        {
            const string code = "Runtime.OperationCanceledBeforeSessionStart";
            const string reason =
                "Station operation cancellation was already durable before the in-process session started.";
            var canceledAtUtc = DateTimeOffset.UtcNow;
            return new StationOperationDispatchResult(
                ExecutionStatus.Canceled,
                ResultJudgement.Aborted,
                OperationExecutionEvidenceFactory.FromCoordinatorCancellation(
                    request,
                    canceledAtUtc,
                    code,
                    reason),
                null,
                0,
                0,
                0,
                canceledAtUtc,
                code,
                reason);
        }
        if (sessionResult.IsFailure)
        {
            var failedAtUtc = DateTimeOffset.UtcNow;
            return new StationOperationDispatchResult(
                ExecutionStatus.Failed,
                ResultJudgement.Unknown,
                OperationExecutionEvidenceFactory.FromCoordinatorFailure(
                    request,
                    failedAtUtc,
                    sessionResult.Error.Code,
                    sessionResult.Error.Message,
                    0),
                null,
                0,
                0,
                0,
                failedAtUtc,
                sessionResult.Error.Code,
                sessionResult.Error.Message);
        }

        var session = await sessionRepository.GetByIdAsync(
                sessionResult.Value.SessionId,
                CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Runtime Session {sessionResult.Value.SessionId} completed without durable state.");
        var completedAtUtc = session.CompletedAtUtc
            ?? throw new InvalidDataException(
                $"Terminal Runtime Session {session.Id} has no completion timestamp.");
        var completedSteps = session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed);
        var executionEvidence = OperationExecutionEvidenceFactory.FromRuntimeSession(request, session);
        if (session.Status == RuntimeSessionStatus.Completed)
        {
            var outputs = ProductionContextOutputReader.ReadExplicitMany(session.Commands
                .Where(static command =>
                    command.Status == ExecutionStatus.Completed)
                .Select(static command => command.ResultPayload));
            return new StationOperationDispatchResult(
                ExecutionStatus.Completed,
                ResolveJudgement(session.Commands),
                executionEvidence,
                outputs,
                completedSteps,
                session.Commands.Count,
                session.Incidents.Count,
                completedAtUtc);
        }

        var status = session.Status switch
        {
            RuntimeSessionStatus.Canceled or RuntimeSessionStatus.Stopped => ExecutionStatus.Canceled,
            _ => ExecutionStatus.Failed
        };
        return new StationOperationDispatchResult(
            status,
            status == ExecutionStatus.Canceled
                ? ResultJudgement.Aborted
                : ResultJudgement.Unknown,
            executionEvidence,
            null,
            completedSteps,
            session.Commands.Count,
            session.Incidents.Count,
            completedAtUtc,
            "Runtime.OperationSessionFailed",
            $"Runtime Session {session.Id} ended as {session.Status}.");
    }

    private static ResultJudgement ResolveJudgement(IReadOnlyCollection<RuntimeCommand> commands)
    {
        var judgements = commands
            .Where(static command => command.ResultJudgement is not null)
            .Select(static command => command.ResultJudgement!.Value)
            .ToArray();
        if (judgements.Contains(ResultJudgement.Aborted))
        {
            return ResultJudgement.Aborted;
        }

        if (judgements.Contains(ResultJudgement.Failed))
        {
            return ResultJudgement.Failed;
        }

        if (judgements.Contains(ResultJudgement.Unknown))
        {
            return ResultJudgement.Unknown;
        }

        return judgements.Contains(ResultJudgement.Passed)
            ? ResultJudgement.Passed
            : ResultJudgement.NotApplicable;
    }

    private static RuntimeSessionTraceMetadata CreateTraceMetadata(
        StationOperationDispatchRequest request)
    {
        var run = request.Run;
        var operation = request.Operation;
        return new RuntimeSessionTraceMetadata(
            run.RunId,
            run.ProductionUnitId,
            run.ProductionLineDefinitionId,
            operation.Definition.OperationId,
            operation.OperationRunId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            run.ProductionUnitIdentity,
            run.LotId,
            run.CarrierId,
            operation.Definition.ResourceRequirements.FirstOrDefault(requirement =>
                requirement.Kind == ResourceKind.Fixture)?.ResourceId,
            operation.Definition.ResourceRequirements.FirstOrDefault(requirement =>
                requirement.Kind == ResourceKind.Device)?.ResourceId,
            run.ActorId,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            request.ResourceLeases.Select(ResourceLeaseFenceEvidence.FromLease));
    }
}
