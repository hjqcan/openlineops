using System.Runtime.Versioning;
using System.Security.Principal;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

internal sealed record EffectiveExternalProgramPolicy(
    TimeSpan ExecutionTimeout,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int MaximumArtifactCount,
    long MaximumArtifactBytes,
    long MaximumTotalArtifactBytes,
    int MaximumOutputDirectoryEntries,
    int MaximumOutputFileCount,
    int MaximumOutputDirectoryDepth,
    TimeSpan OutputDirectoryScanInterval,
    IReadOnlyCollection<string> InheritedEnvironmentVariables,
    WindowsProcessLimits ProcessLimits)
{
    public static EffectiveExternalProgramPolicy Create(
        ExternalProgramHostOptions host,
        ExternalProgramExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);
        var resource = request.Policy;
        ValidateResourcePolicy(resource);
        var maximumProcessCount = Math.Min(host.MaximumProcessCount, resource.MaximumProcessCount);
        var maximumWorkingSet = Math.Min(
            host.MaximumWorkingSetBytes,
            resource.MaximumWorkingSetBytes);
        var resourceJobMemory = resource.MaximumWorkingSetBytes > long.MaxValue / maximumProcessCount
            ? long.MaxValue
            : resource.MaximumWorkingSetBytes * maximumProcessCount;
        var processLimits = new WindowsProcessLimits(
            maximumProcessCount,
            maximumWorkingSet,
            Math.Min(host.MaximumJobMemoryBytes, resourceJobMemory),
            TimeSpan.FromMilliseconds(Math.Min(
                host.MaximumCpuTimeMilliseconds,
                resource.MaximumCpuTimeMilliseconds)));
        processLimits.Validate();

        var resourceEnvironment = resource.AllowedEnvironmentVariables.ToHashSet(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var inheritedEnvironment = host.AllowedInheritedEnvironmentVariables
            .Where(resourceEnvironment.Contains)
            .Order(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        return new EffectiveExternalProgramPolicy(
            Minimum(
                request.Timeout,
                TimeSpan.FromMilliseconds(host.MaximumExecutionTimeMilliseconds)),
            Math.Min(host.MaximumStandardOutputBytes, resource.MaximumStandardOutputBytes),
            Math.Min(host.MaximumStandardErrorBytes, resource.MaximumStandardErrorBytes),
            Math.Min(host.MaximumArtifactCount, resource.MaximumArtifactCount),
            Math.Min(host.MaximumArtifactBytes, resource.MaximumArtifactBytes),
            Math.Min(host.MaximumTotalArtifactBytes, resource.MaximumTotalArtifactBytes),
            host.MaximumOutputDirectoryEntries,
            Math.Min(host.MaximumArtifactCount, resource.MaximumArtifactCount) - 2,
            host.MaximumOutputDirectoryDepth,
            TimeSpan.FromMilliseconds(host.OutputDirectoryScanIntervalMilliseconds),
            inheritedEnvironment,
            processLimits);
    }

    private static void ValidateResourcePolicy(ExternalProgramExecutionPolicy policy)
    {
        if (!string.Equals(policy.PermissionProfileName, "Restricted", StringComparison.Ordinal)
            || policy.AllowedEnvironmentVariables is null
            || policy.MaximumProcessCount <= 0
            || policy.MaximumWorkingSetBytes <= 0
            || policy.MaximumCpuTimeMilliseconds <= 0
            || policy.MaximumStandardOutputBytes <= 0
            || policy.MaximumStandardErrorBytes <= 0
            || policy.MaximumArtifactCount < 2
            || policy.MaximumArtifactBytes <= 0
            || policy.MaximumTotalArtifactBytes < policy.MaximumArtifactBytes)
        {
            throw new InvalidDataException("External program frozen execution policy is invalid.");
        }

        var names = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var name in policy.AllowedEnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Contains('=')
                || name.Contains('\0')
                || char.IsWhiteSpace(name[0])
                || char.IsWhiteSpace(name[^1])
                || !names.Add(name))
            {
                throw new InvalidDataException(
                    "External program frozen environment allowlist is invalid.");
            }
        }
    }

    private static TimeSpan Minimum(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;
}

internal sealed record ExternalProgramHostIdentity(
    string Name,
    string Sid,
    bool IsAuthenticated,
    bool IsSystem,
    bool IsAdministrator);

internal interface IExternalProgramHostIdentityReader
{
    ExternalProgramHostIdentity Read();
}

internal sealed class WindowsExternalProgramHostIdentityReader : IExternalProgramHostIdentityReader
{
    [SupportedOSPlatform("windows")]
    internal static TokenAccessLevels RequiredTokenAccess =>
        TokenAccessLevels.Query | TokenAccessLevels.Duplicate;

    public ExternalProgramHostIdentity Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ExternalProgramHostIdentity(
                Environment.UserName,
                Environment.UserName,
                IsAuthenticated: true,
                IsSystem: false,
                IsAdministrator: false);
        }

        return ReadWindows();
    }

    [SupportedOSPlatform("windows")]
    private static ExternalProgramHostIdentity ReadWindows()
    {
        using var identity = WindowsIdentity.GetCurrent(RequiredTokenAccess);
        var sid = identity.User
                  ?? throw new InvalidOperationException("External program host identity has no SID.");
        return CreateIdentity(
            identity.Name,
            sid,
            identity.AuthenticationType,
            () => new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator));
    }

    [SupportedOSPlatform("windows")]
    internal static ExternalProgramHostIdentity CreateIdentity(
        string name,
        SecurityIdentifier sid,
        string? authenticationType,
        Func<bool> administratorProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sid);
        ArgumentNullException.ThrowIfNull(administratorProbe);
        var isAdministrator = false;
        try
        {
            isAdministrator = administratorProbe();
        }
        catch (System.Security.SecurityException)
        {
            isAdministrator = true;
        }

        return new ExternalProgramHostIdentity(
            name,
            sid.Value,
            !string.IsNullOrWhiteSpace(authenticationType)
            && !sid.IsWellKnown(WellKnownSidType.AnonymousSid),
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid),
            isAdministrator);
    }
}

internal sealed class ExternalProgramHostPolicyEnforcer
{
    private readonly ExternalProgramHostOptions _options;
    private readonly IExternalProgramHostIdentityReader _identityReader;

    public ExternalProgramHostPolicyEnforcer(
        ExternalProgramHostOptions options,
        IExternalProgramHostIdentityReader? identityReader = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _identityReader = identityReader ?? new WindowsExternalProgramHostIdentityReader();
    }

    public ExternalProgramHostIdentity Enforce(ExternalProgramExecutionPolicy resourcePolicy)
    {
        ArgumentNullException.ThrowIfNull(resourcePolicy);
        if (_options.RequireAppContainerIsolation && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "External program AppContainer isolation requires Windows and cannot be downgraded.");
        }

        var identity = _identityReader.Read();
        if (!resourcePolicy.NetworkAccessAllowed && !_options.RequireAppContainerIsolation)
        {
            throw new InvalidOperationException(
                "External program network denial requires AppContainer isolation.");
        }

        if (!_options.RequireRestrictedHostIdentity)
        {
            return identity;
        }

        if (!identity.IsAuthenticated || identity.IsSystem || identity.IsAdministrator)
        {
            throw new InvalidOperationException(
                "External programs require an authenticated, non-SYSTEM, non-administrative service identity.");
        }

        var allowed = _options.AllowedRestrictedHostAccounts.Contains(
                          identity.Name,
                          StringComparer.OrdinalIgnoreCase)
                      || _options.AllowedRestrictedHostSids.Contains(
                          identity.Sid,
                          StringComparer.OrdinalIgnoreCase);
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"External program host identity '{identity.Name}' ({identity.Sid}) is not allowed.");
        }

        return identity;
    }
}
