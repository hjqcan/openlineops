using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Resources;

public enum ResourceKind
{
    Station = 1,
    Slot = 2,
    Fixture = 3,
    Device = 4,
    SlotGroup = 5
}

public sealed record ResourceRequirement
{
    public ResourceRequirement(ResourceKind kind, string resourceId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported resource kind.");
        }

        Kind = kind;
        ResourceId = Required(resourceId, nameof(resourceId));
        if (kind == ResourceKind.Slot)
        {
            var segments = ResourceId.Split('/', StringSplitOptions.None);
            if (segments.Length != 3
                || segments.Any(static segment => string.IsNullOrWhiteSpace(segment)))
            {
                throw new ArgumentException(
                    "Slot resource id must be the canonical Line/Station/Slot address.",
                    nameof(resourceId));
            }
        }
    }

    public ResourceKind Kind { get; }

    public string ResourceId { get; }

    public string CanonicalKey => $"{Kind}:{ResourceId}";

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
}

public sealed record ResourceLease
{
    public ResourceLease(
        ResourceRequirement resource,
        ProductionRunId productionRunId,
        string operationRunId,
        long fencingToken,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        if (productionRunId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production Run id cannot be empty.", nameof(productionRunId));
        }

        ProductionRunId = productionRunId;
        OperationRunId = string.IsNullOrWhiteSpace(operationRunId)
            ? throw new ArgumentException("Operation Run id is required.", nameof(operationRunId))
            : operationRunId;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        if (acquiredAtUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Resource lease timestamps must use UTC offset zero.");
        }

        if (expiresAtUtc <= acquiredAtUtc)
        {
            throw new ArgumentException("Resource lease expiry must follow acquisition.");
        }

        FencingToken = fencingToken;
        AcquiredAtUtc = acquiredAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public ResourceRequirement Resource { get; }

    public ProductionRunId ProductionRunId { get; }

    public string OperationRunId { get; }

    public long FencingToken { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record ResourceLeaseFenceEvidence
{
    public ResourceLeaseFenceEvidence(
        ResourceRequirement resource,
        long fencingToken,
        DateTimeOffset expiresAtUtc)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Resource lease fence expiry must use UTC offset zero.",
                nameof(expiresAtUtc));
        }

        FencingToken = fencingToken;
        ExpiresAtUtc = expiresAtUtc;
    }

    public ResourceRequirement Resource { get; }

    public long FencingToken { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public static ResourceLeaseFenceEvidence FromLease(ResourceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new ResourceLeaseFenceEvidence(
            lease.Resource,
            lease.FencingToken,
            lease.ExpiresAtUtc);
    }
}
