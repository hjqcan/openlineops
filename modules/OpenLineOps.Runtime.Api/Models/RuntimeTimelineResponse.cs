namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeTimelineResponse(IReadOnlyCollection<RuntimeTimelineEntryResponse> Items);
