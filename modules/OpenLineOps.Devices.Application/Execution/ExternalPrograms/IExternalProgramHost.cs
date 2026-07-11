using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Devices.Application.Execution.ExternalPrograms;

public interface IExternalProgramHost
{
    ValueTask<ExternalProgramExecutionResult> ExecuteAsync(
        ExternalProgramExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalProgramExecutionRequest(
    string ResourceId,
    Guid ProductionRunId,
    Guid RuntimeCommandId,
    string ReleaseApplicationRootPath,
    string ResourceRootRelativePath,
    string EntryPointRelativePath,
    long EntryPointSizeBytes,
    string EntryPointSha256,
    IReadOnlyCollection<ExternalProgramExecutionFile> Files,
    IReadOnlyCollection<string> Arguments,
    string InvocationPayload,
    TimeSpan Timeout,
    ExternalProgramExecutionPolicy Policy);

public sealed record ExternalProgramExecutionPolicy(
    string PermissionProfileName,
    bool NetworkAccessAllowed,
    IReadOnlyCollection<string> AllowedEnvironmentVariables,
    int MaximumProcessCount,
    long MaximumWorkingSetBytes,
    long MaximumCpuTimeMilliseconds,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int MaximumArtifactCount,
    long MaximumArtifactBytes,
    long MaximumTotalArtifactBytes);

public sealed record ExternalProgramExecutionFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ExternalProgramExecutionResult(
    ExecutionStatus ExecutionStatus,
    ResultJudgement ResultJudgement,
    string? StandardOutput,
    string? FailureReason,
    IReadOnlyCollection<ExternalProgramArtifact> Artifacts)
{
    public static ExternalProgramExecutionResult Completed(
        string standardOutput,
        IReadOnlyCollection<ExternalProgramArtifact> artifacts) =>
        new(
            ExecutionStatus.Completed,
            ResultJudgement.NotApplicable,
            standardOutput,
            null,
            artifacts);

    public static ExternalProgramExecutionResult Failed(
        string reason,
        IReadOnlyCollection<ExternalProgramArtifact> artifacts) =>
        Terminal(ExecutionStatus.Failed, reason, artifacts);

    public static ExternalProgramExecutionResult TimedOut(
        string reason,
        IReadOnlyCollection<ExternalProgramArtifact> artifacts) =>
        Terminal(ExecutionStatus.TimedOut, reason, artifacts);

    public static ExternalProgramExecutionResult Canceled(
        string reason,
        IReadOnlyCollection<ExternalProgramArtifact> artifacts) =>
        Terminal(ExecutionStatus.Canceled, reason, artifacts);

    public static ExternalProgramExecutionResult Rejected(string reason) =>
        Terminal(ExecutionStatus.Rejected, reason, []);

    private static ExternalProgramExecutionResult Terminal(
        ExecutionStatus status,
        string reason,
        IReadOnlyCollection<ExternalProgramArtifact> artifacts)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || char.IsWhiteSpace(reason[0])
            || char.IsWhiteSpace(reason[^1]))
        {
            throw new ArgumentException(
                "External program terminal reason must be canonical text.",
                nameof(reason));
        }

        return new ExternalProgramExecutionResult(
            status,
            ResultJudgement.Unknown,
            null,
            reason,
            artifacts ?? throw new ArgumentNullException(nameof(artifacts)));
    }
}

public sealed record ExternalProgramArtifact(
    string Name,
    ExternalProgramArtifactKind Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string Sha256);

public enum ExternalProgramArtifactKind
{
    Log = 1,
    Image = 2,
    Csv = 3,
    Binary = 4,
    Report = 5
}
