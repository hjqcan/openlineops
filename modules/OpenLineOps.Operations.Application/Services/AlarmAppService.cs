using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Results;
using OpenLineOps.Operations.Application.Contract.Services;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Operations;
using OpenLineOps.Operations.Domain.Repositories;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Application.Services;

public sealed class AlarmAppService : IAlarmAppService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IAlarmRepository _repository;
    private readonly TimeProvider _timeProvider;

    public AlarmAppService(IAlarmRepository repository)
        : this(repository, TimeProvider.System)
    {
    }

    public AlarmAppService(
        IAlarmRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AlarmDefinitionCommandResult> RegisterDefinitionAsync(
        RegisterAlarmDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(
            "register-definition",
            request.Id,
            request.StationId,
            request.Source,
            request.Severity,
            request.Title,
            request.Description,
            request.IsLatching,
            request.RequiresBuzzer,
            request.MaximumShelfSeconds,
            request.EscalationDelaySeconds,
            request.EscalationAction,
            request.CreatedBy);

        var replay = await _repository
            .GetDefinitionByCommandIdAsync(request.CommandId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return string.Equals(
                    replay.CommandFingerprint,
                    fingerprint,
                    StringComparison.Ordinal)
                ? new AlarmDefinitionCommandResult(
                    true,
                    "Operations.Accepted",
                    "Alarm definition registration replayed.",
                    ToDetails(replay),
                    Replayed: true)
                : DefinitionConflict(
                    "Operations.Alarm.IdempotencyConflict",
                    "The command ID is already bound to different definition content.");
        }

        var existing = await _repository
            .GetDefinitionAsync(request.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return DefinitionConflict(
                "Operations.Alarm.DefinitionAlreadyExists",
                "Alarm definitions are immutable; create a new stable definition ID.");
        }

        var definition = AlarmDefinition.Create(
            request.Id,
            request.StationId,
            request.Source,
            request.Severity,
            request.Title,
            request.Description,
            request.IsLatching,
            request.RequiresBuzzer,
            request.MaximumShelfSeconds,
            request.EscalationDelaySeconds,
            request.EscalationAction,
            request.CreatedBy,
            UtcNow(),
            request.CommandId,
            fingerprint);
        _repository.AddDefinition(definition);

        var commitOutcome = await _repository
            .CommitAlarmChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        return commitOutcome switch
        {
            AlarmCommitOutcome.Committed => new AlarmDefinitionCommandResult(
                true,
                "Operations.Accepted",
                "Alarm definition registered.",
                ToDetails(definition)),
            AlarmCommitOutcome.UniqueConstraintConflict => DefinitionConflict(
                "Operations.Alarm.IdempotencyConflict",
                "Definition ID or registration command was committed concurrently."),
            AlarmCommitOutcome.ConcurrencyConflict => DefinitionConflict(
                "Operations.Alarm.VersionConflict",
                "Definition registration conflicted with concurrent persistence."),
            _ => DefinitionConflict(
                "Operations.Alarm.NotPersisted",
                "Alarm definition registration did not persist any changes.")
        };
    }

    public async Task<AlarmDefinitionDetails?> GetDefinitionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var definition = await _repository
            .GetDefinitionAsync(id, cancellationToken)
            .ConfigureAwait(false);

        return definition is null
            ? null
            : ToDetails(definition);
    }

    public async Task<AlarmDetails> RaiseAsync(
        RaiseAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await RaiseCommandAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Alarm is null)
        {
            throw new InvalidOperationException($"{result.Code}: {result.Message}");
        }

        return result.Alarm;
    }

    public async Task<RaiseAlarmCommandResult> RaiseCommandAsync(
        RaiseAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var commandId = OptionalCommandId(request.CommandId, $"raise:{request.Id}");
        var raisedAtUtc = request.RaisedAtUtc ?? UtcNow();
        var fingerprint = Fingerprint(
            "raise",
            request.Id,
            request.StationId,
            request.Source,
            request.SourceId,
            request.Severity,
            request.Title,
            request.Description,
            raisedAtUtc,
            request.DefinitionId,
            request.ActorId);

        var replay = await _repository
            .GetFactByCommandIdAsync(commandId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.CommandFingerprint, fingerprint, StringComparison.Ordinal)
                || replay.Action != AlarmLifecycleAction.Raised
                || !string.Equals(replay.AlarmId, request.Id, StringComparison.Ordinal))
            {
                return RaiseConflict(
                    "Operations.Alarm.IdempotencyConflict",
                    "The command ID is already bound to different alarm content.");
            }

            var replayedAlarm = await GetAsync(request.Id, cancellationToken).ConfigureAwait(false);
            return replayedAlarm is null
                ? RaiseConflict(
                    "Operations.Alarm.AuditProjectionMismatch",
                    "The alarm fact exists but its current projection is missing.")
                : new RaiseAlarmCommandResult(
                    true,
                    "Operations.Accepted",
                    "Alarm raise replayed.",
                    replayedAlarm,
                    Replayed: true);
        }

        Alarm aggregate;
        var existingAlarm = await _repository
            .GetByIdAsync(new AlarmId(request.Id), cancellationToken)
            .ConfigureAwait(false);
        if (existingAlarm is not null)
        {
            return RaiseConflict(
                "Operations.Alarm.AlreadyExists",
                "An alarm instance with this stable ID already exists.");
        }

        if (string.IsNullOrWhiteSpace(request.DefinitionId))
        {
            aggregate = Alarm.Raise(
                new AlarmId(request.Id),
                request.StationId,
                request.Source,
                request.SourceId,
                request.Severity,
                request.Title,
                request.Description,
                raisedAtUtc);
        }
        else
        {
            var definition = await _repository
                .GetDefinitionAsync(request.DefinitionId, cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
            {
                return RaiseConflict(
                    "Operations.Alarm.DefinitionNotFound",
                    "The requested alarm definition was not found.");
            }

            if (!string.Equals(definition.StationId, request.StationId, StringComparison.Ordinal)
                || !string.Equals(definition.Source, request.Source, StringComparison.Ordinal)
                || definition.Severity != request.Severity
                || !string.Equals(definition.Title, request.Title, StringComparison.Ordinal)
                || !string.Equals(definition.Description, request.Description, StringComparison.Ordinal))
            {
                return RaiseConflict(
                    "Operations.Alarm.DefinitionMismatch",
                    "Alarm instance metadata must exactly match its immutable definition.");
            }

            aggregate = Alarm.Raise(
                new AlarmId(request.Id),
                definition,
                request.SourceId,
                raisedAtUtc);
        }

        _repository.Add(aggregate);
        var actorId = string.IsNullOrWhiteSpace(request.ActorId)
            ? $"station-agent:{aggregate.StationId}"
            : request.ActorId.Trim();
        _repository.AddFact(AlarmLifecycleFact.Create(
            NewFactId(),
            aggregate.Id.Value,
            aggregate.Version,
            commandId,
            fingerprint,
            AlarmLifecycleAction.Raised,
            actorId,
            raisedAtUtc,
            JsonSerializer.Serialize(
                new
                {
                    aggregate.DefinitionId,
                    aggregate.StationId,
                    aggregate.Source,
                    aggregate.SourceId,
                    Severity = aggregate.Severity.ToString(),
                    aggregate.Title,
                    aggregate.Description
                },
                JsonOptions),
            string.Empty));

        var commitOutcome = await _repository
            .CommitAlarmChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        return commitOutcome switch
        {
            AlarmCommitOutcome.Committed => new RaiseAlarmCommandResult(
                true,
                "Operations.Accepted",
                "Alarm raised.",
                ToDetails(aggregate, UtcNow())),
            AlarmCommitOutcome.UniqueConstraintConflict => RaiseConflict(
                "Operations.Alarm.IdempotencyConflict",
                "Alarm ID or command ID was committed concurrently."),
            AlarmCommitOutcome.ConcurrencyConflict => RaiseConflict(
                "Operations.Alarm.VersionConflict",
                "Alarm creation conflicted with concurrent persistence."),
            _ => RaiseConflict(
                "Operations.Alarm.NotPersisted",
                "Alarm raise operation did not persist any changes.")
        };
    }

    public async Task<AlarmDetails?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await _repository
            .GetByIdAsync(new AlarmId(id), cancellationToken)
            .ConfigureAwait(false);

        return aggregate is null
            ? null
            : ToDetails(aggregate, UtcNow());
    }

    public async Task<IReadOnlyCollection<AlarmDetails>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        var alarms = await _repository
            .GetOpenByStationAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        var now = UtcNow();

        return alarms
            .OrderBy(alarm => alarm.RaisedAtUtc)
            .ThenBy(alarm => alarm.Id.Value, StringComparer.Ordinal)
            .Select(alarm => ToDetails(alarm, now))
            .ToArray();
    }

    public Task<OperationsApplicationResult> AcknowledgeAsync(
        string id,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            id,
            AlarmLifecycleAction.Acknowledged,
            request.CommandId,
            request.ExpectedVersion,
            request.AcknowledgedBy,
            Fingerprint(
                "acknowledge",
                id,
                request.AcknowledgedBy,
                request.AcknowledgementComment,
                request.ExpectedVersion),
            JsonSerializer.Serialize(
                new { request.AcknowledgementComment },
                JsonOptions),
            (alarm, now) => alarm.Acknowledge(
                request.AcknowledgedBy,
                request.AcknowledgementComment,
                now),
            cancellationToken);
    }

    public async Task<OperationsApplicationResult> ClearSourceAsync(
        string id,
        ClearAlarmSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var aggregate = await _repository
            .GetByIdAsync(new AlarmId(id), cancellationToken)
            .ConfigureAwait(false);
        if (aggregate is null)
        {
            return NotFound();
        }

        if (!string.Equals(aggregate.StationId, request.StationId, StringComparison.Ordinal))
        {
            return OperationsApplicationResult.Rejected(
                "Operations.Alarm.StationMismatch",
                "Only the Station Agent that owns the alarm source can clear it.");
        }

        return await ExecuteLoadedAsync(
                aggregate,
                AlarmLifecycleAction.SourceCleared,
                request.CommandId,
                request.ExpectedVersion,
                request.SourceActor,
                Fingerprint(
                    "source-clear",
                    id,
                    request.SourceActor,
                    request.StationId,
                    request.ClearanceNote,
                    request.ExpectedVersion),
                JsonSerializer.Serialize(
                    new { request.StationId, request.ClearanceNote },
                    JsonOptions),
                (alarm, now) => alarm.ClearFromSource(
                    request.SourceActor,
                    request.ClearanceNote,
                    now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<OperationsApplicationResult> ShelfAsync(
        string id,
        ShelfAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            id,
            AlarmLifecycleAction.Shelved,
            request.CommandId,
            request.ExpectedVersion,
            request.ActorId,
            Fingerprint(
                "shelf",
                id,
                request.ActorId,
                request.Comment,
                request.ShelvedUntilUtc,
                request.ExpectedVersion),
            JsonSerializer.Serialize(
                new { request.Comment, request.ShelvedUntilUtc },
                JsonOptions),
            (alarm, now) => alarm.Shelf(
                request.ActorId,
                request.Comment,
                request.ShelvedUntilUtc,
                now),
            cancellationToken);
    }

    public Task<OperationsApplicationResult> SuppressAsync(
        string id,
        SuppressAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            id,
            AlarmLifecycleAction.Suppressed,
            request.CommandId,
            request.ExpectedVersion,
            request.ActorId,
            Fingerprint(
                "suppress",
                id,
                request.ActorId,
                request.SuppressionSource,
                request.Reason,
                request.SuppressedUntilUtc,
                request.ExpectedVersion),
            JsonSerializer.Serialize(
                new
                {
                    request.SuppressionSource,
                    request.Reason,
                    request.SuppressedUntilUtc
                },
                JsonOptions),
            (alarm, now) => alarm.Suppress(
                request.ActorId,
                request.SuppressionSource,
                request.Reason,
                request.SuppressedUntilUtc,
                now),
            cancellationToken);
    }

    public async Task<IReadOnlyCollection<AlarmLifecycleFactDetails>> GetFactsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var facts = await _repository
            .GetFactsAsync(new AlarmId(id), cancellationToken)
            .ConfigureAwait(false);

        return facts.Select(ToDetails).ToArray();
    }

    private async Task<OperationsApplicationResult> ExecuteAsync(
        string alarmId,
        AlarmLifecycleAction action,
        string? commandId,
        long? expectedVersion,
        string actorId,
        string fingerprint,
        string payloadJson,
        Func<Alarm, DateTimeOffset, OperationsOperationResult> transition,
        CancellationToken cancellationToken)
    {
        var aggregate = await _repository
            .GetByIdAsync(new AlarmId(alarmId), cancellationToken)
            .ConfigureAwait(false);
        return aggregate is null
            ? NotFound()
            : await ExecuteLoadedAsync(
                    aggregate,
                    action,
                    commandId,
                    expectedVersion,
                    actorId,
                    fingerprint,
                    payloadJson,
                    transition,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<OperationsApplicationResult> ExecuteLoadedAsync(
        Alarm aggregate,
        AlarmLifecycleAction action,
        string? requestedCommandId,
        long? requestedExpectedVersion,
        string actorId,
        string fingerprint,
        string payloadJson,
        Func<Alarm, DateTimeOffset, OperationsOperationResult> transition,
        CancellationToken cancellationToken)
    {
        var commandId = OptionalCommandId(
            requestedCommandId,
            $"legacy:{action}:{aggregate.Id.Value}:{Guid.NewGuid():N}");
        var replay = await _repository
            .GetFactByCommandIdAsync(commandId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return string.Equals(replay.CommandFingerprint, fingerprint, StringComparison.Ordinal)
                   && replay.Action == action
                   && string.Equals(replay.AlarmId, aggregate.Id.Value, StringComparison.Ordinal)
                ? OperationsApplicationResult.Accepted(
                    "Alarm command replayed.",
                    replay.AlarmVersion,
                    replayed: true)
                : OperationsApplicationResult.Rejected(
                    "Operations.Alarm.IdempotencyConflict",
                    "The command ID is already bound to different alarm content.");
        }

        var expectedVersion = requestedExpectedVersion ?? aggregate.Version;
        if (aggregate.Version != expectedVersion)
        {
            return OperationsApplicationResult.Rejected(
                "Operations.Alarm.VersionConflict",
                $"Expected alarm version {expectedVersion}, but current version is {aggregate.Version}.");
        }

        var now = UtcNow();
        var result = transition(aggregate, now);
        if (!result.Succeeded)
        {
            return OperationsApplicationResult.Rejected(result.Code, result.Message);
        }

        if (aggregate.Version == expectedVersion)
        {
            return OperationsApplicationResult.Accepted(result.Message, aggregate.Version);
        }

        var previousHash = await _repository
            .GetLastFactHashAsync(aggregate.Id, cancellationToken)
            .ConfigureAwait(false);
        _repository.UpdateWithExpectedVersion(aggregate, expectedVersion);
        _repository.AddFact(AlarmLifecycleFact.Create(
            NewFactId(),
            aggregate.Id.Value,
            aggregate.Version,
            commandId,
            fingerprint,
            action,
            actorId,
            now,
            payloadJson,
            previousHash));

        var commitOutcome = await _repository
            .CommitAlarmChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        return commitOutcome switch
        {
            AlarmCommitOutcome.Committed => OperationsApplicationResult.Accepted(
                result.Message,
                aggregate.Version),
            AlarmCommitOutcome.ConcurrencyConflict => OperationsApplicationResult.Rejected(
                "Operations.Alarm.VersionConflict",
                "The alarm changed concurrently; reload its current version before retrying."),
            AlarmCommitOutcome.UniqueConstraintConflict => OperationsApplicationResult.Rejected(
                "Operations.Alarm.IdempotencyConflict",
                "The command ID was committed concurrently; retry the exact command to replay it."),
            _ => OperationsApplicationResult.Rejected(
                "Operations.Alarm.NotPersisted",
                "Alarm command did not persist any changes.")
        };
    }

    private DateTimeOffset UtcNow()
    {
        var value = _timeProvider.GetUtcNow();
        return value.Offset == TimeSpan.Zero
            ? value
            : value.ToUniversalTime();
    }

    private static AlarmDefinitionCommandResult DefinitionConflict(
        string code,
        string message) =>
        new(false, code, message, null);

    private static RaiseAlarmCommandResult RaiseConflict(
        string code,
        string message) =>
        new(false, code, message, null);

    private static OperationsApplicationResult NotFound() =>
        OperationsApplicationResult.Rejected(
            "Operations.Alarm.NotFound",
            "Alarm was not found.");

    private static string OptionalCommandId(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        if (normalized.Length > 200)
        {
            throw new ArgumentException(
                "Alarm command IDs cannot exceed 200 characters.",
                nameof(value));
        }

        return normalized;
    }

    private static string Fingerprint(string operation, params object?[] values)
    {
        var builder = new StringBuilder(operation);
        foreach (var value in values)
        {
            var canonical = value switch
            {
                null => string.Empty,
                DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                Enum enumValue => enumValue.ToString(),
                IFormattable formattable => formattable.ToString(
                    null,
                    CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty
            };
            builder.Append('|')
                .Append(canonical.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(canonical);
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string NewFactId() =>
        $"operations.alarm.fact.{Guid.NewGuid():N}";

    private static AlarmDetails ToDetails(Alarm aggregate, DateTimeOffset now)
    {
        return new AlarmDetails(
            aggregate.Id.Value,
            aggregate.StationId,
            aggregate.Source,
            aggregate.SourceId,
            aggregate.Severity,
            aggregate.Status,
            aggregate.Title,
            aggregate.Description,
            aggregate.RaisedAtUtc,
            aggregate.AcknowledgedBy,
            aggregate.AcknowledgedAtUtc,
            aggregate.ResolvedBy,
            aggregate.ResolvedAtUtc,
            aggregate.ResolutionNote,
            aggregate.DefinitionId,
            aggregate.Version,
            aggregate.SourceActive,
            aggregate.SourceClearedBy,
            aggregate.SourceClearedAtUtc,
            aggregate.SourceClearanceNote,
            aggregate.IsLatching,
            aggregate.AcknowledgementComment,
            aggregate.ShelvedBy,
            aggregate.ShelfComment,
            aggregate.ShelvedAtUtc,
            aggregate.ShelvedUntilUtc,
            aggregate.IsShelvedAt(now),
            aggregate.SuppressionSource,
            aggregate.SuppressionReason,
            aggregate.SuppressedBy,
            aggregate.SuppressedAtUtc,
            aggregate.SuppressedUntilUtc,
            aggregate.IsSuppressedAt(now),
            aggregate.IsBuzzerActiveAt(now),
            aggregate.IsEscalationDueAt(now),
            aggregate.EscalationAction);
    }

    private static AlarmDefinitionDetails ToDetails(AlarmDefinition definition)
    {
        return new AlarmDefinitionDetails(
            definition.Id,
            definition.StationId,
            definition.Source,
            definition.Severity,
            definition.Title,
            definition.Description,
            definition.IsLatching,
            definition.RequiresBuzzer,
            definition.MaximumShelfSeconds,
            definition.EscalationDelaySeconds,
            definition.EscalationAction,
            definition.CreatedBy,
            definition.CreatedAtUtc);
    }

    private static AlarmLifecycleFactDetails ToDetails(AlarmLifecycleFact fact)
    {
        return new AlarmLifecycleFactDetails(
            fact.Sequence,
            fact.FactId,
            fact.AlarmId,
            fact.AlarmVersion,
            fact.CommandId,
            fact.Action,
            fact.ActorId,
            fact.OccurredAtUtc,
            fact.PayloadJson,
            fact.ContentSha256);
    }
}
