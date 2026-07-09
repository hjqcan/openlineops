using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Traceability.Application.Records;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class TraceRecordRuntimeDomainEventSubscriber : IRuntimeDomainEventSubscriber
{
    private readonly IRuntimeSessionRepository _runtimeSessionRepository;
    private readonly ITraceRecordService _traceRecordService;

    public TraceRecordRuntimeDomainEventSubscriber(
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

        foreach (var completedEvent in domainEvents
                     .OfType<RuntimeSessionStatusChangedDomainEvent>()
                     .Where(domainEvent => domainEvent.ToStatus == RuntimeSessionStatus.Completed))
        {
            await CreateTraceRecordAsync(completedEvent.SessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask CreateTraceRecordAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var session = await _runtimeSessionRepository
            .GetByIdAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null
            || session.Status != RuntimeSessionStatus.Completed
            || session.StartedAtUtc is null
            || session.CompletedAtUtc is null
            || !session.TraceMetadata.CanCreateTraceRecord)
        {
            return;
        }

        var result = await _traceRecordService
            .CreateCompletedAsync(ToCreateTraceRecordRequest(session), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure
            && !string.Equals(result.Error.Code, "Conflict.Traceability.RecordAlreadyExists", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    private static CreateTraceRecordRequest ToCreateTraceRecordRequest(RuntimeSession session)
    {
        var completedAtUtc = session.CompletedAtUtc!.Value;

        return new CreateTraceRecordRequest(
            TraceRecordId: session.Id.Value,
            RuntimeSessionId: session.Id.Value,
            SerialNumber: session.TraceMetadata.SerialNumber,
            BatchId: session.TraceMetadata.BatchId,
            StationId: session.StationId.Value,
            FixtureId: session.TraceMetadata.FixtureId,
            ProcessDefinitionId: session.ProcessDefinitionId.Value,
            ProcessVersionId: session.ProcessVersionId.Value,
            ConfigurationSnapshotId: session.ConfigurationSnapshotId.Value,
            RecipeSnapshotId: session.RecipeSnapshotId.Value,
            DeviceId: session.TraceMetadata.DeviceId,
            Judgement: null,
            StartedAtUtc: session.StartedAtUtc!.Value,
            CompletedAtUtc: completedAtUtc,
            RecordedBy: session.TraceMetadata.ActorId,
            Measurements: session.Commands
                .Where(command => command.Status == RuntimeCommandStatus.Completed)
                .Select(command => ToMeasurementRecordRequest(command, session.TraceMetadata.DeviceId!, completedAtUtc))
                .ToArray(),
            Artifacts: [],
            AuditEntries:
            [
                new CreateAuditEntryRequest(
                    AuditEntryId: null,
                    ActorId: session.TraceMetadata.ActorId,
                    Action: "RuntimeSession.Completed",
                    Detail: "Trace record generated from runtime session completion event.",
                    OccurredAtUtc: completedAtUtc)
            ]);
    }

    private static CreateMeasurementRecordRequest ToMeasurementRecordRequest(
        RuntimeCommand command,
        string deviceId,
        DateTimeOffset completedAtUtc)
    {
        return new CreateMeasurementRecordRequest(
            MeasurementRecordId: command.Id.Value,
            Name: command.CommandName,
            NumericValue: null,
            TextValue: command.ResultPayload ?? command.Status.ToString(),
            Unit: null,
            DeviceId: deviceId,
            RuntimeCommandId: command.Id.Value,
            Passed: true,
            MeasuredAtUtc: command.CompletedAtUtc ?? completedAtUtc);
    }
}
