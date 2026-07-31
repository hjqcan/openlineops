namespace OpenLineOps.Plugin.Abstractions;

public enum PluginDeviceSignalQuality
{
    Unknown = 0,
    Good = 1,
    Uncertain = 2,
    Bad = 3
}

public sealed record PluginDeviceSignalSample
{
    public PluginDeviceSignalSample(
        string signalId,
        PluginDeviceValue value,
        string? unit,
        PluginDeviceSignalQuality quality,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset observedTimestampUtc,
        long sequence,
        string? qualityCode = null)
    {
        SignalId = PluginDeviceContractGuard.RequiredCanonical(signalId, nameof(signalId));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Unit = PluginDeviceContractGuard.OptionalCanonical(unit, nameof(unit));
        Quality = PluginDeviceContractGuard.Defined(quality, nameof(quality));
        SourceTimestampUtc = PluginDeviceContractGuard.Utc(
            sourceTimestampUtc,
            nameof(sourceTimestampUtc));
        ObservedTimestampUtc = PluginDeviceContractGuard.Utc(
            observedTimestampUtc,
            nameof(observedTimestampUtc));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        Sequence = sequence;
        QualityCode = PluginDeviceContractGuard.OptionalCanonical(
            qualityCode,
            nameof(qualityCode));
    }

    public string SignalId { get; }

    public PluginDeviceValue Value { get; }

    public string? Unit { get; }

    public PluginDeviceSignalQuality Quality { get; }

    public DateTimeOffset SourceTimestampUtc { get; }

    public DateTimeOffset ObservedTimestampUtc { get; }

    public long Sequence { get; }

    public string? QualityCode { get; }
}

public sealed record PluginDeviceSignalReadRequest
{
    public PluginDeviceSignalReadRequest(
        string sessionId,
        IEnumerable<string> signalIds)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        SignalIds = PluginDeviceContractGuard.UniqueCanonicalIds(signalIds, nameof(signalIds));
    }

    public string SessionId { get; }

    public IReadOnlyList<string> SignalIds { get; }
}

public sealed record PluginDeviceSignalWrite
{
    public PluginDeviceSignalWrite(
        string signalId,
        PluginDeviceValue value,
        string? unit = null)
    {
        SignalId = PluginDeviceContractGuard.RequiredCanonical(signalId, nameof(signalId));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Unit = PluginDeviceContractGuard.OptionalCanonical(unit, nameof(unit));
    }

    public string SignalId { get; }

    public PluginDeviceValue Value { get; }

    public string? Unit { get; }
}

public sealed record PluginDeviceSignalWriteRequest
{
    public PluginDeviceSignalWriteRequest(
        string sessionId,
        PluginDeviceCommandEnvelope command,
        IEnumerable<PluginDeviceSignalWrite> writes)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        Command = command ?? throw new ArgumentNullException(nameof(command));
        ArgumentNullException.ThrowIfNull(writes);

        var snapshot = writes.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(static write => write is null))
        {
            throw new ArgumentException(
                "Writes must contain at least one non-null value.",
                nameof(writes));
        }

        if (snapshot
            .Select(static write => write.SignalId)
            .Distinct(StringComparer.Ordinal)
            .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Writes must target unique signal ids.",
                nameof(writes));
        }

        Writes = Array.AsReadOnly(snapshot);
    }

    public string SessionId { get; }

    public PluginDeviceCommandEnvelope Command { get; }

    public IReadOnlyList<PluginDeviceSignalWrite> Writes { get; }
}

public sealed record PluginDeviceSignalSubscriptionRequest
{
    public PluginDeviceSignalSubscriptionRequest(
        string sessionId,
        string subscriptionId,
        IEnumerable<string> signalIds,
        long? resumeAfterSequence = null,
        TimeSpan? minimumSamplingInterval = null)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        SubscriptionId = PluginDeviceContractGuard.RequiredCanonical(
            subscriptionId,
            nameof(subscriptionId));
        SignalIds = PluginDeviceContractGuard.UniqueCanonicalIds(signalIds, nameof(signalIds));
        if (resumeAfterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeAfterSequence),
                resumeAfterSequence,
                "Resume sequence cannot be negative.");
        }

        ResumeAfterSequence = resumeAfterSequence;
        MinimumSamplingInterval = minimumSamplingInterval is null
            ? null
            : PluginDeviceContractGuard.PositiveWholeMilliseconds(
                minimumSamplingInterval.Value,
                nameof(minimumSamplingInterval));
    }

    public string SessionId { get; }

    public string SubscriptionId { get; }

    public IReadOnlyList<string> SignalIds { get; }

    public long? ResumeAfterSequence { get; }

    public TimeSpan? MinimumSamplingInterval { get; }
}

public sealed record PluginDeviceSignalSubscriptionEvent
{
    public PluginDeviceSignalSubscriptionEvent(
        string subscriptionId,
        long sequence,
        PluginDeviceSignalSample sample)
    {
        SubscriptionId = PluginDeviceContractGuard.RequiredCanonical(
            subscriptionId,
            nameof(subscriptionId));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        Sequence = sequence;
        Sample = sample ?? throw new ArgumentNullException(nameof(sample));
    }

    public string SubscriptionId { get; }

    public long Sequence { get; }

    public PluginDeviceSignalSample Sample { get; }
}
