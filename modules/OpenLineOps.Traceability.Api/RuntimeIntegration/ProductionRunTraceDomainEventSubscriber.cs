using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Traceability.Application.Records;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class ProductionRunTraceDomainEventSubscriber :
    IRuntimeDomainEventSubscriber,
    IProductionRunTerminalOutboxHandler
{
    private readonly IRuntimeSessionRepository _runtimeSessionRepository;
    private readonly ITraceRecordService _traceRecordService;

    public ProductionRunTraceDomainEventSubscriber(
        IRuntimeSessionRepository runtimeSessionRepository,
        ITraceRecordService traceRecordService)
    {
        _runtimeSessionRepository = runtimeSessionRepository;
        _traceRecordService = traceRecordService;
    }

    public async ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var terminalEvent in domainEvents.OfType<ProductionRunTerminalDomainEvent>())
        {
            await HandleSnapshotAsync(terminalEvent.Run, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask HandleAsync(
        ProductionRunSnapshot run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return HandleSnapshotAsync(run, cancellationToken);
    }

    private async ValueTask HandleSnapshotAsync(
        ProductionRunSnapshot run,
        CancellationToken cancellationToken)
    {
        if (run.Status is not (ProductionRunStatus.Completed
            or ProductionRunStatus.Failed
            or ProductionRunStatus.Canceled)
            || run.CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production run terminal event {run.RunId} does not contain terminal state.");
        }

        var stages = new List<CreateTraceStageExecutionRequest>(run.Stages.Count);
        foreach (var stage in run.Stages.OrderBy(stage => stage.Sequence))
        {
            RuntimeSession? session = null;
            if (stage.RuntimeSessionId is not null)
            {
                session = await _runtimeSessionRepository
                    .GetByIdAsync(stage.RuntimeSessionId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidateStageEvidence(run, stage, session);
            stages.Add(ToStageRequest(run, stage, session));
        }

        var result = await _traceRecordService.CreateAsync(
            new CreateTraceRecordRequest(
                run.RunId.Value,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId,
                run.ProductionLineDefinitionId,
                run.DutIdentity.ModelId,
                run.DutIdentity.InputKey,
                run.DutIdentity.Value,
                run.BatchId,
                run.FixtureId,
                run.DeviceId,
                run.ActorId,
                run.Status.ToString(),
                run.Status switch
                {
                    ProductionRunStatus.Completed => "Passed",
                    ProductionRunStatus.Failed => "Failed",
                    ProductionRunStatus.Canceled => "Aborted",
                    _ => throw new InvalidOperationException($"Unsupported terminal run status {run.Status}.")
                },
                run.CreatedAtUtc,
                run.StartedAtUtc,
                run.CompletedAtUtc.Value,
                run.FailureCode,
                run.FailureReason,
                stages,
                [
                    new CreateAuditEntryRequest(
                        run.RunId.Value,
                        run.ActorId,
                        $"ProductionRun.{run.Status}",
                        $"Trace record generated from terminal production run {run.RunId}.",
                        run.CompletedAtUtc.Value)
                ]),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure
            && !string.Equals(
                result.Error.Code,
                "Conflict.Traceability.RecordAlreadyExists",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    private static CreateTraceStageExecutionRequest ToStageRequest(
        ProductionRunSnapshot run,
        ProductionStageRunSnapshot stage,
        RuntimeSession? session)
    {
        var completedAtUtc = session is { IsTerminal: true, CompletedAtUtc: { } sessionCompletedAtUtc }
            ? sessionCompletedAtUtc
            : stage.CompletedAtUtc ?? run.CompletedAtUtc!.Value;
        var commands = session?.Commands.Select(ToCommandRequest).ToArray() ?? [];
        var measurements = session?.Commands
            .Select(command => ToMeasurementRequest(command, run.DeviceId, completedAtUtc))
            .ToArray() ?? [];
        var incidents = session?.Incidents
            .Select(incident => new CreateTraceIncidentRequest(
                incident.Id.Value,
                incident.Severity.ToString(),
                incident.Code,
                incident.Message,
                incident.OccurredAtUtc))
            .ToArray() ?? [];

        return new CreateTraceStageExecutionRequest(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            stage.StationId.Value,
            stage.ProcessDefinitionId.Value,
            stage.ProcessVersionId.Value,
            stage.ConfigurationSnapshotId.Value,
            stage.RecipeSnapshotId.Value,
            stage.RuntimeSessionId?.Value,
            session?.Status.ToString(),
            stage.Status.ToString(),
            stage.StartedAtUtc,
            completedAtUtc,
            stage.FailureCode,
            stage.FailureReason,
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount,
            commands,
            measurements,
            [],
            incidents);
    }

    private static CreateTraceCommandRequest ToCommandRequest(RuntimeCommand command)
    {
        return new CreateTraceCommandRequest(
            command.Id.Value,
            command.StepId.Value,
            command.ActionId.Value,
            command.TargetKind,
            command.TargetId,
            command.TargetCapability.Value,
            command.CommandName,
            command.Status.ToString(),
            command.SemanticOutcome?.ToString(),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static CreateMeasurementRecordRequest ToMeasurementRequest(
        RuntimeCommand command,
        string? deviceId,
        DateTimeOffset stageCompletedAtUtc)
    {
        return new CreateMeasurementRecordRequest(
            command.Id.Value,
            command.CommandName,
            null,
            command.ResultPayload ?? command.FailureReason ?? command.Status.ToString(),
            null,
            deviceId,
            command.Id.Value,
            command.ActionId.Value,
            command.TargetKind,
            command.TargetId,
            command.Status.ToString(),
            command.SemanticOutcome switch
            {
                RuntimeCommandSemanticOutcome.Passed => true,
                RuntimeCommandSemanticOutcome.Failed => false,
                RuntimeCommandSemanticOutcome.Aborted => null,
                _ => null
            },
            command.CompletedAtUtc ?? stageCompletedAtUtc);
    }

    private static void ValidateStageEvidence(
        ProductionRunSnapshot run,
        ProductionStageRunSnapshot stage,
        RuntimeSession? session)
    {
        if (stage.RuntimeSessionId is null)
        {
            if (session is not null
                || stage.CompletedStepCount != 0
                || stage.CommandCount != 0
                || stage.IncidentCount != 0)
            {
                throw new InvalidOperationException(
                    $"Production stage {stage.StageId} contains evidence without a runtime session.");
            }

            return;
        }

        if (session is null)
        {
            if (stage.Status == ProductionStageRunStatus.Completed
                || stage.CompletedStepCount != 0
                || stage.CommandCount != 0
                || stage.IncidentCount != 0)
            {
                throw new InvalidOperationException(
                    $"Runtime session {stage.RuntimeSessionId} for production stage {stage.StageId} was not found.");
            }

            return;
        }

        var metadata = session.TraceMetadata;
        Require(session.Id == stage.RuntimeSessionId.Value, stage, "runtime session id");
        Require(metadata.ProductionRunId == run.RunId, stage, "production run id");
        RequireEqual(metadata.ProductionLineDefinitionId, run.ProductionLineDefinitionId, stage, "production line");
        RequireEqual(metadata.ProductionStageId, stage.StageId, stage, "stage id");
        Require(metadata.StageSequence == stage.Sequence, stage, "stage sequence");
        RequireEqual(metadata.WorkstationId, stage.WorkstationId, stage, "workstation");
        RequireEqual(metadata.ProjectId, run.ProjectId, stage, "project");
        RequireEqual(metadata.ApplicationId, run.ApplicationId, stage, "application");
        RequireEqual(metadata.ProjectSnapshotId, run.ProjectSnapshotId, stage, "project snapshot");
        RequireEqual(metadata.TopologyId, run.TopologyId, stage, "topology");
        RequireEqual(metadata.DutIdentity.ModelId, run.DutIdentity.ModelId, stage, "DUT model");
        RequireEqual(metadata.DutIdentity.InputKey, run.DutIdentity.InputKey, stage, "DUT identity input");
        RequireEqual(metadata.DutIdentity.Value, run.DutIdentity.Value, stage, "DUT identity value");
        RequireEqual(metadata.ActorId, run.ActorId, stage, "actor");
        RequireEqual(metadata.BatchId, run.BatchId, stage, "batch");
        RequireEqual(metadata.FixtureId, run.FixtureId, stage, "fixture");
        RequireEqual(metadata.DeviceId, run.DeviceId, stage, "device");
        Require(session.StationId == stage.StationId, stage, "station");
        Require(session.ProcessDefinitionId == stage.ProcessDefinitionId, stage, "process definition");
        Require(session.ProcessVersionId == stage.ProcessVersionId, stage, "process version");
        Require(session.ConfigurationSnapshotId == stage.ConfigurationSnapshotId, stage, "configuration snapshot");
        Require(session.RecipeSnapshotId == stage.RecipeSnapshotId, stage, "recipe snapshot");
        Require(
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed) == stage.CompletedStepCount,
            stage,
            "completed step count");
        Require(session.Commands.Count == stage.CommandCount, stage, "command count");
        Require(session.Incidents.Count == stage.IncidentCount, stage, "incident count");
    }

    private static void Require(bool condition, ProductionStageRunSnapshot stage, string field)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Production stage {stage.StageId} runtime evidence differs in {field}.");
        }
    }

    private static void RequireEqual(
        string? actual,
        string? expected,
        ProductionStageRunSnapshot stage,
        string field)
    {
        Require(string.Equals(actual, expected, StringComparison.Ordinal), stage, field);
    }
}
