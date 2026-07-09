namespace OpenLineOps.Application.Abstractions.Paging;

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int PageNumber,
    int PageSize,
    long TotalCount)
{
    public long TotalPages => PageSize <= 0
        ? 0
        : (long)Math.Ceiling(TotalCount / (double)PageSize);
}
