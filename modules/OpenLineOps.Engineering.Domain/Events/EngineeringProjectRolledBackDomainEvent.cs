using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Events;

public sealed record EngineeringProjectRolledBackDomainEvent(
    EngineeringProjectId ProjectId,
    ConfigurationSnapshotId ActiveSnapshotId,
    DateTimeOffset RolledBackAtUtc)
    : DomainEvent("Engineering.ProjectRolledBack");
