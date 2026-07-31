namespace OpenLineOps.Quality.Domain.Nonconformances;

public enum NonconformanceSeverity
{
    Minor = 0,
    Major = 1,
    Critical = 2
}

public enum NonconformanceStatus
{
    Open = 0,
    Dispositioned = 1
}

public enum NonconformanceDisposition
{
    Rework = 0,
    UseAsIs = 1,
    Scrap = 2
}
