using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Domain.Events;

public sealed record ProcessDefinitionPublishedDomainEvent(
    ProcessDefinitionId ProcessDefinitionId,
    ProcessVersionId VersionId,
    DateTimeOffset PublishedAtUtc)
    : DomainEvent("ProcessDefinition.Published");
