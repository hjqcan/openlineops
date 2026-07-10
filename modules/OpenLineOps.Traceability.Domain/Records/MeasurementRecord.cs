using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record MeasurementRecord
{
    public MeasurementRecord(
        MeasurementRecordId id,
        string name,
        decimal? numericValue,
        string? textValue,
        string? unit,
        DeviceId? deviceId,
        RuntimeCommandId? runtimeCommandId,
        string actionId,
        TraceTargetKind targetKind,
        string targetId,
        TraceCommandStatus commandStatus,
        bool? passed,
        DateTimeOffset measuredAtUtc)
    {
        if (numericValue is null && string.IsNullOrWhiteSpace(textValue))
        {
            throw new ArgumentException("A measurement must include a numeric or text value.", nameof(textValue));
        }

        Id = id;
        Name = TraceabilityIdGuard.NotBlank(name, nameof(name));
        NumericValue = numericValue;
        TextValue = TraceabilityIdGuard.OptionalText(textValue);
        Unit = TraceabilityIdGuard.OptionalText(unit);
        DeviceId = deviceId;
        RuntimeCommandId = runtimeCommandId;
        ActionId = TraceabilityIdGuard.NotBlank(actionId, nameof(actionId));
        TargetKind = targetKind;
        TargetId = TraceabilityIdGuard.NotBlank(targetId, nameof(targetId));
        CommandStatus = commandStatus;
        Passed = passed;
        MeasuredAtUtc = measuredAtUtc;
    }

    public MeasurementRecordId Id { get; }

    public string Name { get; }

    public decimal? NumericValue { get; }

    public string? TextValue { get; }

    public string? Unit { get; }

    public DeviceId? DeviceId { get; }

    public RuntimeCommandId? RuntimeCommandId { get; }

    public string ActionId { get; }

    public TraceTargetKind TargetKind { get; }

    public string TargetId { get; }

    public TraceCommandStatus CommandStatus { get; }

    public bool? Passed { get; }

    public DateTimeOffset MeasuredAtUtc { get; }
}
