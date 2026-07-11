using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryProductionMaterialArrivalInbox :
    IProductionMaterialArrivalInbox
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _byMessageId = [];
    private readonly Dictionary<string, Guid> _byIdempotencyKey = new(StringComparer.Ordinal);

    public ValueTask<ProductionMaterialArrivalClaim> ClaimAsync(
        MaterialArrived message,
        DateTimeOffset claimedAtUtc,
        TimeSpan claimDuration,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        ValidateClaim(claimedAtUtc, claimDuration);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        lock (_gate)
        {
            if (!_byMessageId.TryGetValue(message.MessageId, out var entry))
            {
                if (_byIdempotencyKey.TryGetValue(message.IdempotencyKey, out var existingId))
                {
                    throw new InvalidOperationException(
                        $"Material arrival idempotency key '{message.IdempotencyKey}' belongs to message {existingId:D}.");
                }

                entry = new Entry(message.MessageId, message.IdempotencyKey, payload);
                _byMessageId.Add(message.MessageId, entry);
                _byIdempotencyKey.Add(message.IdempotencyKey, message.MessageId);
            }
            else
            {
                if (!string.Equals(
                        entry.IdempotencyKey,
                        message.IdempotencyKey,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Material arrival message {message.MessageId:D} was reused with a different idempotency key.");
                }

                EnsureSameJson(entry.PayloadJson, payload);
            }

            if (entry.Result is not null)
            {
                return ValueTask.FromResult(new ProductionMaterialArrivalClaim(
                    ProductionMaterialArrivalClaimStatus.Completed,
                    null,
                    null,
                    entry.Result));
            }

            if (entry.ClaimToken is not null && entry.ClaimUntilUtc > claimedAtUtc)
            {
                return ValueTask.FromResult(new ProductionMaterialArrivalClaim(
                    ProductionMaterialArrivalClaimStatus.Busy,
                    null,
                    entry.ClaimUntilUtc,
                    null));
            }

            entry.ClaimToken = Guid.NewGuid();
            entry.ClaimUntilUtc = claimedAtUtc.Add(claimDuration);
            return ValueTask.FromResult(new ProductionMaterialArrivalClaim(
                ProductionMaterialArrivalClaimStatus.Claimed,
                entry.ClaimToken,
                null,
                null));
        }
    }

    public ValueTask CompleteAsync(
        Guid messageId,
        Guid claimToken,
        RuntimeOperationResult result,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty || claimToken == Guid.Empty)
        {
            throw new ArgumentException("Material arrival completion identity is incomplete.");
        }

        ArgumentNullException.ThrowIfNull(result);
        if (completedAtUtc == default || completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material arrival completion time must be a non-default UTC value.",
                nameof(completedAtUtc));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = _byMessageId.GetValueOrDefault(messageId)
                ?? throw new InvalidOperationException(
                    $"Material arrival Inbox message {messageId:D} does not exist.");
            if (entry.Result is not null)
            {
                if (entry.Result != result)
                {
                    throw new InvalidOperationException(
                        $"Material arrival message {messageId:D} was completed with different evidence.");
                }

                return ValueTask.CompletedTask;
            }

            if (entry.ClaimToken != claimToken)
            {
                throw new InvalidOperationException(
                    $"Material arrival message {messageId:D} is owned by a different claim.");
            }

            entry.Result = result;
            entry.CompletedAtUtc = completedAtUtc;
            entry.ClaimToken = null;
            entry.ClaimUntilUtc = null;
            return ValueTask.CompletedTask;
        }
    }

    private static void ValidateClaim(DateTimeOffset claimedAtUtc, TimeSpan claimDuration)
    {
        if (claimedAtUtc == default || claimedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material arrival claim time must be a non-default UTC value.",
                nameof(claimedAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            claimDuration,
            TimeSpan.Zero);
    }

    private static void EnsureSameJson(string existing, string candidate)
    {
        using var existingJson = JsonDocument.Parse(existing);
        using var candidateJson = JsonDocument.Parse(candidate);
        if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
        {
            throw new InvalidOperationException(
                "Material arrival Inbox identity was reused with different evidence.");
        }
    }

    private sealed class Entry(Guid messageId, string idempotencyKey, string payloadJson)
    {
        public Guid MessageId { get; } = messageId;
        public string IdempotencyKey { get; } = idempotencyKey;
        public string PayloadJson { get; } = payloadJson;
        public Guid? ClaimToken { get; set; }
        public DateTimeOffset? ClaimUntilUtc { get; set; }
        public RuntimeOperationResult? Result { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
    }
}
