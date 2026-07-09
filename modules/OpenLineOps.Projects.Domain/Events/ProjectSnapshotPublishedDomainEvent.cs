using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Projects.Domain.Identifiers;

namespace OpenLineOps.Projects.Domain.Events;

public sealed record ProjectSnapshotPublishedDomainEvent(
    AutomationProjectId ProjectId,
    PublishedProjectSnapshotId SnapshotId,
    ProjectApplicationId ApplicationId,
    AutomationTopologyId TopologyId,
    DateTimeOffset PublishedAtUtc)
    : DomainEvent("Projects.ProjectSnapshotPublished");
