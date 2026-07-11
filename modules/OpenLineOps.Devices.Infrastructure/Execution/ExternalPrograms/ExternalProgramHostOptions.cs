namespace OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

public sealed class ExternalProgramHostOptions
{
    internal const int WorkspaceLeafLength = 32;

    public const string SectionName = "OpenLineOps:Devices:ExternalProgramHost";

    public string WorkspaceRootPath { get; set; } = "data/openlineops-external-program-workspaces";

    public string EvidenceRootPath { get; set; } = "data/openlineops-traceability-artifacts";

    public int MaximumStandardOutputBytes { get; set; } = 4 * 1024 * 1024;

    public int MaximumStandardErrorBytes { get; set; } = 4 * 1024 * 1024;

    public int MaximumArtifactCount { get; set; } = 128;

    public long MaximumArtifactBytes { get; set; } = 128L * 1024 * 1024;

    public long MaximumTotalArtifactBytes { get; set; } = 512L * 1024 * 1024;

    public long MaximumWorkingSetBytes { get; set; } = 1024L * 1024 * 1024;

    public long MaximumJobMemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public int MaximumProcessCount { get; set; } = 32;

    public long MaximumCpuTimeMilliseconds { get; set; } = 3_600_000;

    public long MaximumExecutionTimeMilliseconds { get; set; } = 3_600_000;

    public int MaximumOutputDirectoryEntries { get; set; } = 512;

    public int MaximumOutputDirectoryDepth { get; set; } = 8;

    public int OutputDirectoryScanIntervalMilliseconds { get; set; } = 50;

    public bool RequireRestrictedHostIdentity { get; set; } = true;

    public bool RequireImmutableContentProtection { get; set; } = true;

    public bool RequireAppContainerIsolation { get; set; } = true;

    public string? AppContainerProfileName { get; set; }

    public bool AppContainerProfileExternallyOwned { get; set; }

    public IList<string> AllowedRestrictedHostAccounts { get; } = new List<string>();

    public IList<string> AllowedRestrictedHostSids { get; } = new List<string>();

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
        var workspaceRoot = ResolveWorkspaceRootPath();
        _ = ResolveEvidenceRootPath();
        if (OperatingSystem.IsWindows()
            && workspaceRoot.Length + 1 + WorkspaceLeafLength >= 260)
        {
            throw new InvalidOperationException(
                "External program workspace root is too long for a bounded Windows process current directory.");
        }

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
        Positive(MaximumWorkingSetBytes, nameof(MaximumWorkingSetBytes));
        Positive(MaximumJobMemoryBytes, nameof(MaximumJobMemoryBytes));
        Positive(MaximumProcessCount, nameof(MaximumProcessCount));
        Positive(MaximumCpuTimeMilliseconds, nameof(MaximumCpuTimeMilliseconds));
        Positive(MaximumExecutionTimeMilliseconds, nameof(MaximumExecutionTimeMilliseconds));
        Positive(MaximumOutputDirectoryEntries, nameof(MaximumOutputDirectoryEntries));
        Positive(MaximumOutputDirectoryDepth, nameof(MaximumOutputDirectoryDepth));
        Positive(OutputDirectoryScanIntervalMilliseconds, nameof(OutputDirectoryScanIntervalMilliseconds));
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


        ValidateIdentityAllowlist();
    }

    private void ValidateIdentityAllowlist()
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in AllowedRestrictedHostAccounts)
        {
            if (!IsCanonical(account) || !accounts.Add(account))
            {
                throw new InvalidOperationException(
                    "Allowed restricted host accounts must be canonical and unique.");
            }
        }

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in AllowedRestrictedHostSids)
        {
            if (!IsCanonical(sid) || !sids.Add(sid))
            {
                throw new InvalidOperationException(
                    "Allowed restricted host SIDs must be canonical and unique.");
            }
        }

        if (RequireRestrictedHostIdentity
            && accounts.Count == 0
            && sids.Count == 0)
        {
            throw new InvalidOperationException(
                "Restricted external program hosting requires an allowed service account or SID.");
        }

        if (RequireAppContainerIsolation
            && (!IsCanonical(AppContainerProfileName)
                || AppContainerProfileName!.Length > 64
                || AppContainerProfileName.Any(character =>
                    !char.IsAsciiLetterOrDigit(character)
                    && character is not '.' and not '-' and not '_')))
        {
            throw new InvalidOperationException(
                "External program AppContainer profile name must be a canonical portable identity of at most 64 characters.");
        }

        if (AppContainerProfileExternallyOwned && !RequireAppContainerIsolation)
        {
            throw new InvalidOperationException(
                "An externally owned AppContainer profile requires AppContainer isolation.");
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
