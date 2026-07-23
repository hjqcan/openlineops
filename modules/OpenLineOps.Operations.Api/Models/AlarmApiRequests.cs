using System.Text.Json.Serialization;

namespace OpenLineOps.Operations.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcknowledgeAlarmApiRequest;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolveAlarmApiRequest(string ResolutionNote);
