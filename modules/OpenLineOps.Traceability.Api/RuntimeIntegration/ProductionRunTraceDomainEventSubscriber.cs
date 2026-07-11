using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Resources;
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
        if (!IsTerminal(run.ExecutionStatus) || run.CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production Run terminal event {run.RunId} does not contain terminal state.");
        }

        var operations = new List<CreateTraceOperationExecutionRequest>(run.Operations.Count);
        foreach (var operation in run.Operations
                     .OrderBy(candidate => candidate.StartedAtUtc)
                     .ThenBy(candidate => candidate.OperationRunId, StringComparer.Ordinal))
        {
            RuntimeSession? session = null;
            if (operation.RuntimeSessionId is not null)
            {
                session = await _runtimeSessionRepository
                    .GetByIdAsync(operation.RuntimeSessionId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidateOperationEvidence(run, operation, session);
            operations.Add(ToOperationRequest(run, operation, session));
        }

        var routeDecisions = run.RouteDecisions
            .Select(decision => new CreateTraceRouteDecisionRequest(
                decision.SourceOperationRunId,
                decision.TransitionId,
                decision.TargetOperationId,
                decision.SourceJudgement.ToString(),
                decision.Traversal,
                decision.DecidedAtUtc))
            .ToArray();
        var completedAtUtc = run.CompletedAtUtc.Value;
        var result = await _traceRecordService.CreateAsync(
            new CreateTraceRecordRequest(
                run.RunId.Value,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId,
                run.ProductionLineDefinitionId,
                run.ProductionUnitIdentity.ModelId,
                run.ProductionUnitIdentity.InputKey,
                run.ProductionUnitIdentity.Value,
                run.LotId,
                run.CarrierId,
                run.ActorId,
                run.ExecutionStatus.ToString(),
                run.Judgement.ToString(),
                run.Disposition.ToString(),
                run.CreatedAtUtc,
                run.StartedAtUtc,
                completedAtUtc,
                run.FailureCode,
                run.FailureReason,
                operations,
                routeDecisions,
                [
                    new CreateAuditEntryRequest(
                        run.RunId.Value,
                        run.ActorId,
                        $"ProductionRun.{run.ExecutionStatus}",
                        $"Trace record generated from terminal Production Run {run.RunId}.",
                        completedAtUtc)
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

    private static CreateTraceOperationExecutionRequest ToOperationRequest(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        RuntimeSession? session)
    {
        var completedAtUtc = operation.CompletedAtUtc
            ?? throw new InvalidOperationException(
                $"Terminal Operation Run {operation.OperationRunId} has no completion timestamp.");
        var deviceId = session?.TraceMetadata.DeviceId;
        var commands = session?.Commands.Select(ToCommandRequest).ToArray() ?? [];
        var measurements = session?.Commands
            .Select(command => ToMeasurementRequest(command, deviceId, completedAtUtc))
            .ToArray() ?? [];
        var incidents = session?.Incidents
            .Select(incident => new CreateTraceIncidentRequest(
                incident.Id.Value,
                incident.Severity.ToString(),
                incident.Code,
                incident.Message,
                incident.OccurredAtUtc))
            .ToArray() ?? [];
        var artifacts = session?.Commands
            .SelectMany(command => ToArtifactRequests(command, deviceId, completedAtUtc))
            .ToArray() ?? [];
        var outputs = operation.Outputs
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CreateTraceOperationOutputRequest(
                pair.Key,
                pair.Value.Kind.ToString(),
                ToCanonicalJson(pair.Value)))
            .ToArray();
        var fencingTokens = operation.FencingTokens
            .OrderBy(pair => pair.Key.Kind)
            .ThenBy(pair => pair.Key.ResourceId, StringComparer.Ordinal)
            .Select(pair => new CreateTraceResourceFencingTokenRequest(
                pair.Key.Kind.ToString(),
                pair.Key.ResourceId,
                pair.Value))
            .ToArray();

        return new CreateTraceOperationExecutionRequest(
            operation.OperationRunId,
            operation.Definition.OperationId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            operation.RuntimeSessionId?.Value,
            session?.Status.ToString(),
            operation.ExecutionStatus.ToString(),
            operation.Judgement.ToString(),
            operation.StartedAtUtc,
            completedAtUtc,
            operation.FailureCode,
            operation.FailureReason,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            commands,
            measurements,
            artifacts,
            incidents,
            outputs,
            fencingTokens);
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
            command.ResultJudgement?.ToString(),
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
        DateTimeOffset operationCompletedAtUtc)
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
            command.ResultJudgement switch
            {
                ResultJudgement.Passed => true,
                ResultJudgement.Failed => false,
                _ => null
            },
            command.CompletedAtUtc ?? operationCompletedAtUtc);
    }

    private static IEnumerable<CreateArtifactRecordRequest> ToArtifactRequests(
        RuntimeCommand command,
        string? deviceId,
        DateTimeOffset operationCompletedAtUtc)
    {
        var evidence = RuntimeCommandEvidencePayload.Read(command.ResultPayload);
        if (evidence is null)
        {
            return [];
        }

        var capturedAtUtc = command.CompletedAtUtc ?? operationCompletedAtUtc;
        return evidence.Artifacts.Select(artifact => new CreateArtifactRecordRequest(
            CreateArtifactRecordId(command.Id.Value, artifact.StorageKey),
            artifact.Name,
            artifact.Kind,
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            deviceId,
            capturedAtUtc));
    }

    private static Guid CreateArtifactRecordId(Guid commandId, string storageKey)
    {
        var input = Encoding.UTF8.GetBytes(commandId.ToString("N") + ":" + storageKey);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string ToCanonicalJson(ProductionContextValue value)
    {
        return value.Kind is ProductionContextValueKind.Text or ProductionContextValueKind.DateTimeUtc
            ? JsonSerializer.Serialize(value.CanonicalValue)
            : value.CanonicalValue;
    }

    private static void ValidateOperationEvidence(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        RuntimeSession? session)
    {
        if (!IsTerminal(operation.ExecutionStatus) || operation.CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production Run {run.RunId} contains non-terminal Operation Run {operation.OperationRunId}.");
        }

        if (operation.RuntimeSessionId is null)
        {
            Require(
                session is null
                && operation.ExecutionStatus == ExecutionStatus.Canceled
                && operation.StartedAtUtc is null
                && operation.CompletedStepCount == 0
                && operation.CommandCount == 0
                && operation.IncidentCount == 0
                && operation.Outputs.Count == 0
                && operation.FencingTokens.Count == 0,
                operation,
                "pre-dispatch cancellation evidence");
            return;
        }

        if (session is null)
        {
            throw new InvalidOperationException(
                $"Runtime Session {operation.RuntimeSessionId} for Operation Run "
                + $"{operation.OperationRunId} was not found.");
        }

        var metadata = session.TraceMetadata;
        Require(session.IsTerminal && session.CompletedAtUtc is not null, operation, "terminal Runtime Session");
        Require(session.Id == operation.RuntimeSessionId.Value, operation, "Runtime Session id");
        Require(metadata.ProductionRunId == run.RunId, operation, "Production Run id");
        RequireEqual(
            metadata.ProductionLineDefinitionId,
            run.ProductionLineDefinitionId,
            operation,
            "production line");
        RequireEqual(metadata.OperationId, operation.Definition.OperationId, operation, "Operation id");
        Require(metadata.OperationAttempt == operation.Attempt, operation, "Operation attempt");
        RequireEqual(
            metadata.StationSystemId,
            operation.Definition.StationSystemId,
            operation,
            "Station System");
        RequireEqual(metadata.ProjectId, run.ProjectId, operation, "project");
        RequireEqual(metadata.ApplicationId, run.ApplicationId, operation, "Application");
        RequireEqual(metadata.ProjectSnapshotId, run.ProjectSnapshotId, operation, "project snapshot");
        RequireEqual(metadata.TopologyId, run.TopologyId, operation, "topology");
        RequireEqual(
            metadata.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.ModelId,
            operation,
            "product model");
        RequireEqual(
            metadata.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.InputKey,
            operation,
            "Production Unit identity input");
        RequireEqual(
            metadata.ProductionUnitIdentity.Value,
            run.ProductionUnitIdentity.Value,
            operation,
            "Production Unit identity value");
        RequireEqual(metadata.ActorId, run.ActorId, operation, "actor");
        RequireEqual(metadata.LotId, run.LotId, operation, "lot");
        RequireEqual(metadata.CarrierId, run.CarrierId, operation, "Carrier");
        RequireEqual(
            metadata.FixtureId,
            FindResourceId(operation.Definition, ResourceKind.Fixture),
            operation,
            "fixture resource");
        RequireEqual(
            metadata.DeviceId,
            FindResourceId(operation.Definition, ResourceKind.Device),
            operation,
            "device resource");
        Require(session.StationId == operation.Definition.StationId, operation, "station");
        Require(
            session.ProcessDefinitionId == operation.Definition.ProcessDefinitionId,
            operation,
            "process definition");
        Require(
            session.ProcessVersionId == operation.Definition.ProcessVersionId,
            operation,
            "process version");
        Require(
            session.ConfigurationSnapshotId == operation.Definition.ConfigurationSnapshotId,
            operation,
            "configuration snapshot");
        Require(
            session.RecipeSnapshotId == operation.Definition.RecipeSnapshotId,
            operation,
            "recipe snapshot");
        Require(
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed)
                == operation.CompletedStepCount,
            operation,
            "completed step count");
        Require(session.Commands.Count == operation.CommandCount, operation, "command count");
        Require(session.Incidents.Count == operation.IncidentCount, operation, "incident count");
        Require(
            operation.FencingTokens.Count == operation.Definition.ResourceRequirements.Count
            && operation.Definition.ResourceRequirements.All(requirement =>
                operation.FencingTokens.TryGetValue(requirement, out var token) && token > 0),
            operation,
            "resource fencing tokens");
    }

    private static string? FindResourceId(OperationRunDefinition definition, ResourceKind kind) =>
        definition.ResourceRequirements.FirstOrDefault(requirement => requirement.Kind == kind)?.ResourceId;

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private static void Require(bool condition, OperationRunSnapshot operation, string field)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Operation Run {operation.OperationRunId} Runtime evidence differs in {field}.");
        }
    }

    private static void RequireEqual(
        string? actual,
        string? expected,
        OperationRunSnapshot operation,
        string field)
    {
        Require(string.Equals(actual, expected, StringComparison.Ordinal), operation, field);
    }
}
