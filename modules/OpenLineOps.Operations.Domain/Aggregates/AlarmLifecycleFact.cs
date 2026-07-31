using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Domain.Aggregates;

public sealed class AlarmLifecycleFact
{
    private AlarmLifecycleFact()
    {
        FactId = string.Empty;
        AlarmId = string.Empty;
        CommandId = string.Empty;
        CommandFingerprint = string.Empty;
        ActorId = string.Empty;
        PayloadJson = string.Empty;
        PreviousSha256 = string.Empty;
        ContentSha256 = string.Empty;
    }

    private AlarmLifecycleFact(
        string factId,
        string alarmId,
        long alarmVersion,
        string commandId,
        string commandFingerprint,
        AlarmLifecycleAction action,
        string actorId,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        string previousSha256)
    {
        FactId = RequiredText(factId, nameof(factId), 160);
        AlarmId = RequiredText(alarmId, nameof(alarmId), 160);
        if (alarmVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alarmVersion),
                "Alarm fact version must be positive.");
        }

        AlarmVersion = alarmVersion;
        CommandId = RequiredText(commandId, nameof(commandId), 200);
        CommandFingerprint = RequiredHex(commandFingerprint, nameof(commandFingerprint));
        Action = action;
        ActorId = RequiredText(actorId, nameof(actorId), 160);
        if (occurredAtUtc == default || occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fact timestamp must be non-default UTC.", nameof(occurredAtUtc));
        }

        OccurredAtUtc = occurredAtUtc;
        PayloadJson = RequiredText(payloadJson, nameof(payloadJson), 8_000);
        PreviousSha256 = string.IsNullOrEmpty(previousSha256)
            ? string.Empty
            : RequiredHex(previousSha256, nameof(previousSha256));
        ContentSha256 = ComputeContentSha256(
            FactId,
            AlarmId,
            AlarmVersion,
            CommandId,
            CommandFingerprint,
            Action,
            ActorId,
            OccurredAtUtc,
            PayloadJson,
            PreviousSha256);
    }

    public long Sequence { get; private set; }

    public string FactId { get; private set; }

    public string AlarmId { get; private set; }

    public long AlarmVersion { get; private set; }

    public string CommandId { get; private set; }

    public string CommandFingerprint { get; private set; }

    public AlarmLifecycleAction Action { get; private set; }

    public string ActorId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string PayloadJson { get; private set; }

    public string PreviousSha256 { get; private set; }

    public string ContentSha256 { get; private set; }

    public static AlarmLifecycleFact Create(
        string factId,
        string alarmId,
        long alarmVersion,
        string commandId,
        string commandFingerprint,
        AlarmLifecycleAction action,
        string actorId,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        string previousSha256) =>
        new(
            factId,
            alarmId,
            alarmVersion,
            commandId,
            commandFingerprint,
            action,
            actorId,
            occurredAtUtc,
            payloadJson,
            previousSha256);

    public bool HasValidContentHash() =>
        string.Equals(
            ContentSha256,
            ComputeContentSha256(
                FactId,
                AlarmId,
                AlarmVersion,
                CommandId,
                CommandFingerprint,
                Action,
                ActorId,
                OccurredAtUtc,
                PayloadJson,
                PreviousSha256),
            StringComparison.Ordinal);

    private static string ComputeContentSha256(
        string factId,
        string alarmId,
        long alarmVersion,
        string commandId,
        string commandFingerprint,
        AlarmLifecycleAction action,
        string actorId,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        string previousSha256)
    {
        var canonical = string.Join(
            '\n',
            previousSha256,
            factId,
            alarmId,
            alarmVersion.ToString(CultureInfo.InvariantCulture),
            commandId,
            commandFingerprint,
            action.ToString(),
            actorId,
            occurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            payloadJson);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string RequiredText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Text values cannot be blank.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Text values cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string RequiredHex(string value, string parameterName)
    {
        var normalized = RequiredText(value, parameterName, 64);
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The value must be a 64-character SHA-256 hexadecimal digest.",
                parameterName);
        }

        return normalized.ToLowerInvariant();
    }
}
