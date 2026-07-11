namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeProductionUnitIdentityResponse(
    string ModelId,
    string InputKey,
    string Value);
