namespace OpenLineOps.Quality.Domain.Testing;

public enum TestAttemptStatus
{
    Running = 0,
    Completed = 1,
    Aborted = 2
}

public enum TestAttemptJudgement
{
    Pending = 0,
    Passed = 1,
    Failed = 2,
    Invalid = 3
}
