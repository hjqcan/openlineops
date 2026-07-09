using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record ArtifactRecord
{
    public ArtifactRecord(
        ArtifactRecordId id,
        string name,
        ArtifactKind kind,
        string storageKey,
        string? mediaType,
        long sizeBytes,
        string? sha256,
        DeviceId deviceId,
        DateTimeOffset capturedAtUtc)
    {
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Artifact size cannot be negative.");
        }

        Id = id;
        Name = TraceabilityIdGuard.NotBlank(name, nameof(name));
        Kind = kind;
        StorageKey = TraceabilityIdGuard.NotBlank(storageKey, nameof(storageKey));
        MediaType = TraceabilityIdGuard.OptionalText(mediaType);
        SizeBytes = sizeBytes;
        Sha256 = TraceabilityIdGuard.OptionalText(sha256);
        DeviceId = deviceId;
        CapturedAtUtc = capturedAtUtc;
    }

    public ArtifactRecordId Id { get; }

    public string Name { get; }

    public ArtifactKind Kind { get; }

    public string StorageKey { get; }

    public string? MediaType { get; }

    public long SizeBytes { get; }

    public string? Sha256 { get; }

    public DeviceId DeviceId { get; }

    public DateTimeOffset CapturedAtUtc { get; }
}
