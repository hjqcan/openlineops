using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Events;

public sealed record ConfigurationSnapshotPublishedDomainEvent(
    EngineeringProjectId ProjectId,
    ConfigurationSnapshotId SnapshotId,
    DateTimeOffset PublishedAtUtc)
    : DomainEvent("Engineering.ConfigurationSnapshotPublished");
