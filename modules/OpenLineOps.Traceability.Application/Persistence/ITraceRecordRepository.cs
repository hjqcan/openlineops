using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Persistence;

public interface ITraceRecordRepository
{
    ValueTask SaveAsync(TraceRecord traceRecord, CancellationToken cancellationToken = default);

    ValueTask<TraceRecord?> GetByIdAsync(TraceRecordId traceRecordId, CancellationToken cancellationToken = default);

    ValueTask<PagedResult<TraceRecord>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default);
}
