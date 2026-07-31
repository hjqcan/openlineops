using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Domain.Aggregates;

public sealed class AlarmDefinition
{
    private AlarmDefinition()
    {
        Id = string.Empty;
        StationId = string.Empty;
        Source = string.Empty;
        Title = string.Empty;
        Description = string.Empty;
        CreatedBy = string.Empty;
        RegistrationCommandId = string.Empty;
        CommandFingerprint = string.Empty;
        ContentSha256 = string.Empty;
    }

    private AlarmDefinition(
        string id,
        string stationId,
        string source,
        AlarmSeverity severity,
        string title,
        string description,
        bool isLatching,
        bool requiresBuzzer,
        int maximumShelfSeconds,
        int? escalationDelaySeconds,
        AlarmPolicyAction escalationAction,
        string createdBy,
        DateTimeOffset createdAtUtc,
        string registrationCommandId,
        string commandFingerprint)
    {
        Id = RequiredText(id, nameof(id), 160);
        StationId = RequiredText(stationId, nameof(stationId), 160);
        Source = RequiredText(source, nameof(source), 160);
        Severity = severity;
        Title = RequiredText(title, nameof(title), 200);
        Description = RequiredText(description, nameof(description), 1_000);
        if (maximumShelfSeconds is < 1 or > 86_400)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumShelfSeconds),
                "Maximum shelf duration must be between one second and 24 hours.");
        }

        if (escalationDelaySeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(escalationDelaySeconds),
                "Escalation delay must be positive when configured.");
        }

        if (escalationAction == AlarmPolicyAction.None && escalationDelaySeconds.HasValue)
        {
            throw new ArgumentException(
                "An escalation delay requires an escalation action.",
                nameof(escalationAction));
        }

        if (escalationAction != AlarmPolicyAction.None && !escalationDelaySeconds.HasValue)
        {
            throw new ArgumentException(
                "An escalation action requires an escalation delay.",
                nameof(escalationDelaySeconds));
        }

        ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        IsLatching = isLatching;
        RequiresBuzzer = requiresBuzzer;
        MaximumShelfSeconds = maximumShelfSeconds;
        EscalationDelaySeconds = escalationDelaySeconds;
        EscalationAction = escalationAction;
        CreatedBy = RequiredText(createdBy, nameof(createdBy), 160);
        CreatedAtUtc = createdAtUtc;
        RegistrationCommandId = RequiredText(
            registrationCommandId,
            nameof(registrationCommandId),
            200);
        CommandFingerprint = RequiredHex(
            commandFingerprint,
            nameof(commandFingerprint));
        ContentSha256 = ComputeContentSha256(
            Id,
            StationId,
            Source,
            Severity,
            Title,
            Description,
            IsLatching,
            RequiresBuzzer,
            MaximumShelfSeconds,
            EscalationDelaySeconds,
            EscalationAction,
            CreatedBy,
            CreatedAtUtc,
            RegistrationCommandId,
            CommandFingerprint);
    }

    public string Id { get; private set; }

    public string StationId { get; private set; }

    public string Source { get; private set; }

    public AlarmSeverity Severity { get; private set; }

    public string Title { get; private set; }

    public string Description { get; private set; }

    public bool IsLatching { get; private set; }

    public bool RequiresBuzzer { get; private set; }

    public int MaximumShelfSeconds { get; private set; }

    public int? EscalationDelaySeconds { get; private set; }

    public AlarmPolicyAction EscalationAction { get; private set; }

    public string CreatedBy { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string RegistrationCommandId { get; private set; }

    public string CommandFingerprint { get; private set; }

    public string ContentSha256 { get; private set; }

    public static AlarmDefinition Create(
        string id,
        string stationId,
        string source,
        AlarmSeverity severity,
        string title,
        string description,
        bool isLatching,
        bool requiresBuzzer,
        int maximumShelfSeconds,
        int? escalationDelaySeconds,
        AlarmPolicyAction escalationAction,
        string createdBy,
        DateTimeOffset createdAtUtc,
        string registrationCommandId,
        string commandFingerprint) =>
        new(
            id,
            stationId,
            source,
            severity,
            title,
            description,
            isLatching,
            requiresBuzzer,
            maximumShelfSeconds,
            escalationDelaySeconds,
            escalationAction,
            createdBy,
            createdAtUtc,
            registrationCommandId,
            commandFingerprint);

    public bool HasValidContentHash() =>
        string.Equals(
            ContentSha256,
            ComputeContentSha256(
                Id,
                StationId,
                Source,
                Severity,
                Title,
                Description,
                IsLatching,
                RequiresBuzzer,
                MaximumShelfSeconds,
                EscalationDelaySeconds,
                EscalationAction,
                CreatedBy,
                CreatedAtUtc,
                RegistrationCommandId,
                CommandFingerprint),
            StringComparison.Ordinal);

    private static string ComputeContentSha256(
        string id,
        string stationId,
        string source,
        AlarmSeverity severity,
        string title,
        string description,
        bool isLatching,
        bool requiresBuzzer,
        int maximumShelfSeconds,
        int? escalationDelaySeconds,
        AlarmPolicyAction escalationAction,
        string createdBy,
        DateTimeOffset createdAtUtc,
        string registrationCommandId,
        string commandFingerprint)
    {
        var canonical = string.Join(
            '\n',
            id,
            stationId,
            source,
            severity.ToString(),
            title,
            description,
            isLatching ? "true" : "false",
            requiresBuzzer ? "true" : "false",
            maximumShelfSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            escalationDelaySeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
            escalationAction.ToString(),
            createdBy,
            createdAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            registrationCommandId,
            commandFingerprint);

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

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be non-default UTC.", parameterName);
        }
    }
}
