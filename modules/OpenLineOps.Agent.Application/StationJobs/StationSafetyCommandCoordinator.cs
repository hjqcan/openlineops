using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Application.StationJobs;

public enum StationSafetyCommandKind
{
    EmergencyStop = 1,
    SafeStop = 2,
    JobCancel = 3
}

public sealed record StationSafetyInboxEntry(
    string IdempotencyKey,
    StationSafetyCommandKind CommandKind,
    string RequestSha256,
    Guid RequestMessageId,
    Guid AcknowledgementMessageId,
    Guid? TargetJobId,
    string AgentId,
    string StationId,
    DateTimeOffset ReceivedAtUtc,
    string? AcknowledgementJson,
    DateTimeOffset? CompletedAtUtc);

public interface IStationSafetyInboxStore
{
    ValueTask<StationSafetyInboxEntry?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
        StationJobId jobId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryBeginAsync(
        StationSafetyInboxEntry entry,
        CancellationToken cancellationToken = default);

    ValueTask<StationSafetyInboxEntry> CompleteAsync(
        string idempotencyKey,
        StationSafetyCommandKind commandKind,
        string requestSha256,
        string acknowledgementJson,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class StationSafetyCommandCoordinator(
    IStationSafetyInboxStore store,
    IClock clock)
{
    private const string RecoveryFailureCode = "Agent.SafetyRecoveryRequired";
    private const string RecoveryFailureReason =
        "A prior safety command may have reached the actuator before its acknowledgement was persisted; physical reconciliation is required and the actuator was not replayed.";
    private const string JobCancellationRecoveryFailureCode =
        "Agent.JobCancellationRecoveryRequired";
    private const string JobCancellationRecoveryFailureReason =
        "A prior job cancellation reached the Agent before its acknowledgement was persisted; the interrupted job was not replayed and physical reconciliation is required if it had begun.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask<EmergencyStopAcknowledged> HandleEmergencyStopAsync(
        EmergencyStopRequested request,
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        var requestSha256 = Fingerprint(request with { MessageId = Guid.Empty });
        var (entry, created) = await GetOrBeginAsync(
                request.IdempotencyKey,
                StationSafetyCommandKind.EmergencyStop,
                requestSha256,
                request.MessageId,
                null,
                request.AgentId,
                request.StationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            return await ReplayEmergencyStopAsync(entry, request.MessageId, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await handler(request, cancellationToken).ConfigureAwait(false);
        var acknowledgement = new EmergencyStopAcknowledged(
            entry.AcknowledgementMessageId,
            entry.RequestMessageId,
            entry.IdempotencyKey,
            entry.AgentId,
            entry.StationId,
            result.Accepted,
            result.FailureCode,
            result.FailureReason,
            UtcNow());
        var completed = await CompleteAsync(entry, acknowledgement, cancellationToken)
            .ConfigureAwait(false);
        return DeserializeEmergencyStop(completed);
    }

    public async ValueTask<StationSafeStopAcknowledged> HandleSafeStopAsync(
        StationSafeStopRequested request,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        var requestSha256 = Fingerprint(request with { MessageId = Guid.Empty });
        var (entry, created) = await GetOrBeginAsync(
                request.IdempotencyKey,
                StationSafetyCommandKind.SafeStop,
                requestSha256,
                request.MessageId,
                null,
                request.AgentId,
                request.StationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            return await ReplaySafeStopAsync(entry, request.MessageId, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await handler(request, cancellationToken).ConfigureAwait(false);
        var acknowledgement = new StationSafeStopAcknowledged(
            entry.AcknowledgementMessageId,
            entry.RequestMessageId,
            entry.IdempotencyKey,
            entry.AgentId,
            entry.StationId,
            result.Accepted,
            result.FailureCode,
            result.FailureReason,
            UtcNow());
        var completed = await CompleteAsync(entry, acknowledgement, cancellationToken)
            .ConfigureAwait(false);
        return DeserializeSafeStop(completed);
    }

    public async ValueTask<StationJobCancelAcknowledged> HandleJobCancelAsync(
        StationJobCancelRequested request,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        var requestSha256 = Fingerprint(request with
        {
            MessageId = Guid.Empty,
            RequestedAtUtc = DateTimeOffset.UnixEpoch
        });
        var (entry, created) = await GetOrBeginAsync(
                request.IdempotencyKey,
                StationSafetyCommandKind.JobCancel,
                requestSha256,
                request.MessageId,
                request.JobId,
                request.AgentId,
                request.StationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            return await ReplayJobCancelAsync(entry, request.MessageId, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await handler(request, cancellationToken).ConfigureAwait(false);
        var acknowledgement = new StationJobCancelAcknowledged(
            entry.AcknowledgementMessageId,
            entry.RequestMessageId,
            entry.IdempotencyKey,
            entry.TargetJobId!.Value,
            entry.AgentId,
            entry.StationId,
            result.Accepted,
            result.FailureCode,
            result.FailureReason,
            UtcNow());
        var completed = await CompleteAsync(entry, acknowledgement, cancellationToken)
            .ConfigureAwait(false);
        return DeserializeJobCancel(completed);
    }

    private async ValueTask<(StationSafetyInboxEntry Entry, bool Created)> GetOrBeginAsync(
        string idempotencyKey,
        StationSafetyCommandKind commandKind,
        string requestSha256,
        Guid requestMessageId,
        Guid? targetJobId,
        string agentId,
        string stationId,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSameRequest(existing, commandKind, requestSha256, targetJobId, agentId, stationId);
            return (existing, false);
        }

        var pending = new StationSafetyInboxEntry(
            Required(idempotencyKey, nameof(idempotencyKey)),
            commandKind,
            requestSha256,
            RequiredGuid(requestMessageId, nameof(requestMessageId)),
            Guid.NewGuid(),
            targetJobId,
            Required(agentId, nameof(agentId)),
            Required(stationId, nameof(stationId)),
            UtcNow(),
            null,
            null);
        if (await store.TryBeginAsync(pending, cancellationToken).ConfigureAwait(false))
        {
            return (pending, true);
        }

        var raced = await store.GetAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Safety command idempotency race did not persist an Inbox entry.");
        EnsureSameRequest(raced, commandKind, requestSha256, targetJobId, agentId, stationId);
        return (raced, false);
    }

    private async ValueTask<EmergencyStopAcknowledged> ReplayEmergencyStopAsync(
        StationSafetyInboxEntry entry,
        Guid currentRequestMessageId,
        CancellationToken cancellationToken)
    {
        EnsureKind(entry, StationSafetyCommandKind.EmergencyStop);
        if (entry.AcknowledgementJson is not null)
        {
            return Correlate(DeserializeEmergencyStop(entry), currentRequestMessageId);
        }

        var recovery = new EmergencyStopAcknowledged(
            entry.AcknowledgementMessageId,
            entry.RequestMessageId,
            entry.IdempotencyKey,
            entry.AgentId,
            entry.StationId,
            false,
            RecoveryFailureCode,
            RecoveryFailureReason,
            UtcNow());
        var completed = await CompleteAsync(entry, recovery, cancellationToken).ConfigureAwait(false);
        return Correlate(DeserializeEmergencyStop(completed), currentRequestMessageId);
    }

    private async ValueTask<StationSafeStopAcknowledged> ReplaySafeStopAsync(
        StationSafetyInboxEntry entry,
        Guid currentRequestMessageId,
        CancellationToken cancellationToken)
    {
        EnsureKind(entry, StationSafetyCommandKind.SafeStop);
        if (entry.AcknowledgementJson is not null)
        {
            return Correlate(DeserializeSafeStop(entry), currentRequestMessageId);
        }

        var recovery = new StationSafeStopAcknowledged(
            entry.AcknowledgementMessageId,
            entry.RequestMessageId,
            entry.IdempotencyKey,
            entry.AgentId,
            entry.StationId,
            false,
            RecoveryFailureCode,
            RecoveryFailureReason,
            UtcNow());
        var completed = await CompleteAsync(entry, recovery, cancellationToken).ConfigureAwait(false);
        return Correlate(DeserializeSafeStop(completed), currentRequestMessageId);
    }

    private async ValueTask<StationJobCancelAcknowledged> ReplayJobCancelAsync(
        StationSafetyInboxEntry entry,
        Guid currentRequestMessageId,
        CancellationToken cancellationToken)
    {
        EnsureKind(entry, StationSafetyCommandKind.JobCancel);
        if (entry.AcknowledgementJson is not null)
        {
            return Correlate(DeserializeJobCancel(entry), currentRequestMessageId);
        }

        var recovery = new StationJobCancelAcknowledged(
            entry.AcknowledgementMessageId,
            entry.RequestMessageId,
            entry.IdempotencyKey,
            entry.TargetJobId
            ?? throw new InvalidDataException("Job cancellation Inbox target is not persisted."),
            entry.AgentId,
            entry.StationId,
            false,
            JobCancellationRecoveryFailureCode,
            JobCancellationRecoveryFailureReason,
            UtcNow());
        var completed = await CompleteAsync(entry, recovery, cancellationToken).ConfigureAwait(false);
        return Correlate(DeserializeJobCancel(completed), currentRequestMessageId);
    }

    private static EmergencyStopAcknowledged Correlate(
        EmergencyStopAcknowledged acknowledgement,
        Guid currentRequestMessageId) =>
        acknowledgement.RequestMessageId == currentRequestMessageId
            ? acknowledgement
            : acknowledgement with
            {
                MessageId = ReplayAcknowledgementMessageId(
                    acknowledgement.MessageId,
                    currentRequestMessageId),
                RequestMessageId = currentRequestMessageId
            };

    private static StationSafeStopAcknowledged Correlate(
        StationSafeStopAcknowledged acknowledgement,
        Guid currentRequestMessageId) =>
        acknowledgement.RequestMessageId == currentRequestMessageId
            ? acknowledgement
            : acknowledgement with
            {
                MessageId = ReplayAcknowledgementMessageId(
                    acknowledgement.MessageId,
                    currentRequestMessageId),
                RequestMessageId = currentRequestMessageId
            };

    private static StationJobCancelAcknowledged Correlate(
        StationJobCancelAcknowledged acknowledgement,
        Guid currentRequestMessageId) =>
        acknowledgement.RequestMessageId == currentRequestMessageId
            ? acknowledgement
            : acknowledgement with
            {
                MessageId = ReplayAcknowledgementMessageId(
                    acknowledgement.MessageId,
                    currentRequestMessageId),
                RequestMessageId = currentRequestMessageId
            };

    private static Guid ReplayAcknowledgementMessageId(
        Guid persistedAcknowledgementMessageId,
        Guid currentRequestMessageId)
    {
        Span<byte> identity = stackalloc byte[32];
        _ = persistedAcknowledgementMessageId.TryWriteBytes(identity[..16]);
        _ = currentRequestMessageId.TryWriteBytes(identity[16..]);
        var hash = SHA256.HashData(identity);
        return new Guid(hash.AsSpan(0, 16));
    }

    private async ValueTask<StationSafetyInboxEntry> CompleteAsync<TAcknowledgement>(
        StationSafetyInboxEntry entry,
        TAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        return await store.CompleteAsync(
                entry.IdempotencyKey,
                entry.CommandKind,
                entry.RequestSha256,
                JsonSerializer.Serialize(acknowledgement, JsonOptions),
                UtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static EmergencyStopAcknowledged DeserializeEmergencyStop(StationSafetyInboxEntry entry)
    {
        EnsureKind(entry, StationSafetyCommandKind.EmergencyStop);
        return JsonSerializer.Deserialize<EmergencyStopAcknowledged>(
                   entry.AcknowledgementJson
                   ?? throw new InvalidDataException("Emergency stop acknowledgement is not persisted."),
                   JsonOptions)
               ?? throw new InvalidDataException("Emergency stop acknowledgement is null.");
    }

    private static StationSafeStopAcknowledged DeserializeSafeStop(StationSafetyInboxEntry entry)
    {
        EnsureKind(entry, StationSafetyCommandKind.SafeStop);
        return JsonSerializer.Deserialize<StationSafeStopAcknowledged>(
                   entry.AcknowledgementJson
                   ?? throw new InvalidDataException("Safe stop acknowledgement is not persisted."),
                   JsonOptions)
               ?? throw new InvalidDataException("Safe stop acknowledgement is null.");
    }

    private static StationJobCancelAcknowledged DeserializeJobCancel(StationSafetyInboxEntry entry)
    {
        EnsureKind(entry, StationSafetyCommandKind.JobCancel);
        return JsonSerializer.Deserialize<StationJobCancelAcknowledged>(
                   entry.AcknowledgementJson
                   ?? throw new InvalidDataException("Job cancellation acknowledgement is not persisted."),
                   JsonOptions)
               ?? throw new InvalidDataException("Job cancellation acknowledgement is null.");
    }

    private static void EnsureSameRequest(
        StationSafetyInboxEntry existing,
        StationSafetyCommandKind commandKind,
        string requestSha256,
        Guid? targetJobId,
        string agentId,
        string stationId)
    {
        if (existing.CommandKind != commandKind
            || !string.Equals(existing.RequestSha256, requestSha256, StringComparison.Ordinal)
            || existing.TargetJobId != targetJobId
            || !string.Equals(existing.AgentId, agentId, StringComparison.Ordinal)
            || !string.Equals(existing.StationId, stationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Safety command idempotency key '{existing.IdempotencyKey}' was reused with different evidence.");
        }
    }

    private static void EnsureKind(
        StationSafetyInboxEntry entry,
        StationSafetyCommandKind expectedKind)
    {
        if (entry.CommandKind != expectedKind)
        {
            throw new InvalidDataException(
                $"Safety Inbox command kind {entry.CommandKind} does not match {expectedKind}.");
        }
    }

    private static string Fingerprint<TRequest>(TRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private DateTimeOffset UtcNow() => clock.UtcNow.ToUniversalTime();

    private static Guid RequiredGuid(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException($"{parameterName} cannot be empty.", parameterName)
        : value;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical text.", parameterName)
            : value;
}
