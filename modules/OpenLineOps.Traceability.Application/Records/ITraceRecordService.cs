using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Application.Queries;

namespace OpenLineOps.Traceability.Application.Records;

public interface ITraceRecordService
{
    Task<Result<TraceRecordDetails>> CreateCompletedAsync(
        CreateTraceRecordRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<TraceRecordDetails>> GetByIdAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<TraceRecordSummary>>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<TraceRecordExportPackage>> ExportAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken = default);
}
