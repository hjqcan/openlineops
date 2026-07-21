using System.Runtime.Versioning;
using System.Security.Principal;
using OpenLineOps.ContentProtection;
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
    string HostAccountSid,
    string ServiceSid,
    bool ServiceLogonSidEnabled,
    bool IsRestrictedToken,
    bool ServiceSidEnabled,
    bool ServiceSidRestricted);

internal interface IExternalProgramHostIdentityReader
{
    ExternalProgramHostIdentity Read(string requiredServiceSid);
}

internal sealed class WindowsExternalProgramHostIdentityReader : IExternalProgramHostIdentityReader
{
    [SupportedOSPlatform("windows")]
    internal static TokenAccessLevels RequiredTokenAccess =>
        TokenAccessLevels.Query;

    public ExternalProgramHostIdentity Read(string requiredServiceSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Restricted Station service identity validation requires Windows.");
        }

        var identity = WindowsStationServiceIdentityReader.ReadRequired(requiredServiceSid);
        return new ExternalProgramHostIdentity(
            identity.HostAccountSid,
            identity.ServiceSid,
            identity.ServiceLogonSidEnabled,
            identity.IsRestrictedToken,
            identity.ServiceSidEnabled,
            identity.ServiceSidRestricted);
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

        if (!resourcePolicy.NetworkAccessAllowed && !_options.RequireAppContainerIsolation)
        {
            throw new InvalidOperationException(
                "External program network denial requires AppContainer isolation.");
        }

        if (!_options.RequireRestrictedHostIdentity)
        {
            return new ExternalProgramHostIdentity(
                HostAccountSid: string.Empty,
                ServiceSid: string.Empty,
                ServiceLogonSidEnabled: false,
                IsRestrictedToken: false,
                ServiceSidEnabled: false,
                ServiceSidRestricted: false);
        }

        var requiredServiceSid = _options.RestrictedServiceSid
                                 ?? throw new InvalidOperationException(
                                     "Restricted external program hosting requires one exact Station service SID.");
        if (!WindowsStationServiceIdentityReader.IsCanonicalServiceSid(requiredServiceSid))
        {
            throw new InvalidOperationException(
                "Restricted external program hosting requires a canonical Windows service SID.");
        }

        var identity = _identityReader.Read(requiredServiceSid);
        if (!string.Equals(
                identity.HostAccountSid,
                WindowsStationServiceIdentityReader.LocalServiceSid,
                StringComparison.Ordinal)
            || !string.Equals(
                identity.ServiceSid,
                requiredServiceSid,
                StringComparison.Ordinal)
            || !identity.ServiceLogonSidEnabled
            || !identity.IsRestrictedToken
            || !identity.ServiceSidEnabled
            || !identity.ServiceSidRestricted)
        {
            throw new InvalidOperationException(
                "External programs require a restricted LocalService service token with the "
                + "Windows service-logon SID and the exact enabled restricted Station service SID.");
        }

        return identity;
    }
}
