namespace OpenLineOps.Traceability.Api.Models;

public sealed record PagedTraceRecordSummaryResponse(
    IReadOnlyCollection<TraceRecordSummaryResponse> Items,
    int PageNumber,
    int PageSize,
    long TotalCount,
    long TotalPages);
