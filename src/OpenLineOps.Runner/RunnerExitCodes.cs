namespace OpenLineOps.Runner;

/// <summary>
/// Stable process exit codes emitted by the one-shot OpenLineOps Runner.
/// </summary>
public static class RunnerExitCodes
{
    public const int Success = 0;

    public const int UsageError = 2;

    public const int ProjectOpenFailed = 3;

    public const int SnapshotSelectionFailed = 4;

    public const int RuntimeStartRejected = 5;

    public const int RuntimeExecutionFailed = 6;

    public const int Canceled = 7;

    public const int InternalError = 70;
}
