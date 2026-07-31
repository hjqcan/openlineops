using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace OpenLineOps.Agent.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<AgentPresenceState>))]
public enum AgentPresenceState
{
    Started = 1,
    Heartbeat = 2,
    Stopping = 3
}

public sealed record AgentPresenceReported(
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid SessionId,
    long Sequence,
    AgentPresenceState State,
    DateTimeOffset ObservedAtUtc);

public static class AgentPresenceContract
{
    public static void Validate(AgentPresenceReported message)
    {
        ArgumentNullException.ThrowIfNull(message);
        StationIdentityContract.RequireMessage(message.AgentId, nameof(message.AgentId));
        StationIdentityContract.RequireMessage(message.StationId, nameof(message.StationId));
        _ = Required(message.StationSystemId, nameof(message.StationSystemId));
        if (message.SessionId == Guid.Empty
            || message.Sequence <= 0
            || !Enum.IsDefined(message.State)
            || message.ObservedAtUtc == default
            || message.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Agent presence identity, sequence, state, or timestamp is invalid.");
        }

        if ((message.State == AgentPresenceState.Started && message.Sequence != 1)
            || (message.State != AgentPresenceState.Started && message.Sequence == 1))
        {
            throw new InvalidDataException(
                "Agent presence sequence 1 is reserved for the Started state.");
        }
    }

    public static Guid MessageId(AgentPresenceReported message)
    {
        Validate(message);
        Span<byte> identity = stackalloc byte[24];
        message.SessionId.TryWriteBytes(identity[..16]);
        BinaryPrimitives.WriteInt64BigEndian(identity[16..], message.Sequence);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity, hash);
        return new Guid(hash[..16]);
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"Agent presence {parameterName} must be canonical non-empty text.")
            : value;
}
