namespace OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

public sealed class ExternalProgramHostOptions
{
    public const string SectionName = "OpenLineOps:Devices:ExternalProgramHost";

    public string WorkspaceRootPath { get; set; } = "data/openlineops-external-program-workspaces";

    public string EvidenceRootPath { get; set; } = "data/openlineops-traceability-artifacts";

    public int MaximumStandardOutputBytes { get; set; } = 4 * 1024 * 1024;

    public int MaximumStandardErrorBytes { get; set; } = 4 * 1024 * 1024;

    public int MaximumArtifactCount { get; set; } = 128;

    public long MaximumArtifactBytes { get; set; } = 128L * 1024 * 1024;

    public long MaximumTotalArtifactBytes { get; set; } = 512L * 1024 * 1024;

    public long ProcessMemoryLimitBytes { get; set; } = 1024L * 1024 * 1024;

    public int ActiveProcessLimit { get; set; } = 32;

    public int CpuTimeLimitSeconds { get; set; } = 3600;

    public bool RequireWindowsJobObject { get; set; } = true;

    public IList<string> AllowedInheritedEnvironmentVariables { get; } =
        new List<string> { "SystemRoot", "WINDIR" };

    public string ResolveWorkspaceRootPath() => ResolveRoot(
        WorkspaceRootPath,
        nameof(WorkspaceRootPath));

    public string ResolveEvidenceRootPath() => ResolveRoot(
        EvidenceRootPath,
        nameof(EvidenceRootPath));

    public void Validate()
    {
        _ = ResolveWorkspaceRootPath();
        _ = ResolveEvidenceRootPath();
        Positive(MaximumStandardOutputBytes, nameof(MaximumStandardOutputBytes));
        Positive(MaximumStandardErrorBytes, nameof(MaximumStandardErrorBytes));
        Positive(MaximumArtifactCount, nameof(MaximumArtifactCount));
        if (MaximumArtifactCount < 2)
        {
            throw new InvalidOperationException(
                "MaximumArtifactCount must reserve stdout and stderr evidence.");
        }
        Positive(MaximumArtifactBytes, nameof(MaximumArtifactBytes));
        Positive(MaximumTotalArtifactBytes, nameof(MaximumTotalArtifactBytes));
        Positive(ProcessMemoryLimitBytes, nameof(ProcessMemoryLimitBytes));
        Positive(ActiveProcessLimit, nameof(ActiveProcessLimit));
        Positive(CpuTimeLimitSeconds, nameof(CpuTimeLimitSeconds));
        if (MaximumTotalArtifactBytes < MaximumArtifactBytes)
        {
            throw new InvalidOperationException(
                "MaximumTotalArtifactBytes cannot be smaller than MaximumArtifactBytes.");
        }

        var names = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var name in AllowedInheritedEnvironmentVariables)
        {
            if (!IsCanonical(name)
                || name.Contains('=', StringComparison.Ordinal)
                || !names.Add(name))
            {
                throw new InvalidOperationException(
                    "Allowed inherited environment variable names must be canonical and unique.");
            }
        }
    }

    private static string ResolveRoot(string value, string name)
    {
        return IsCanonical(value)
            ? Path.GetFullPath(value)
            : throw new InvalidOperationException(
                $"External program host {name} must be a canonical path.");
    }

    private static void Positive(long value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"External program host {name} must be positive.");
        }
    }

    private static bool IsCanonical(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !char.IsWhiteSpace(value[0])
               && !char.IsWhiteSpace(value[^1]);
    }
}
