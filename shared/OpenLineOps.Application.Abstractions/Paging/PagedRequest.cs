namespace OpenLineOps.Application.Abstractions.Paging;

public sealed record PagedRequest(int PageNumber = 1, int PageSize = 50)
{
    public int Skip => (Normalize().PageNumber - 1) * Normalize().PageSize;

    public PagedRequest Normalize(int maxPageSize = 200)
    {
        return new PagedRequest(
            Math.Max(1, PageNumber),
            Math.Clamp(PageSize, 1, maxPageSize));
    }
}
