using System.Runtime.Versioning;
using System.Security.Principal;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Agent;

internal sealed record StationAgentContentCacheProvisioningOptions(
    string WindowsServiceName,
    string PackageCacheDirectory)
{
    public static StationAgentContentCacheProvisioningOptions Load(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var serviceName = WindowsStationServiceIdentityReader.RequireCanonicalServiceName(
            configuration["OpenLineOps:WindowsServiceName"],
            "OpenLineOps:WindowsServiceName");
        var configuredCacheDirectory = configuration[
            "OpenLineOps:Agent:PackageCacheDirectory"];
        if (string.IsNullOrWhiteSpace(configuredCacheDirectory))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:PackageCacheDirectory is required.");
        }

        return new StationAgentContentCacheProvisioningOptions(
            serviceName,
            StationAgentPackageCachePath.RequireCanonicalAbsolute(
                configuredCacheDirectory));
    }
}

internal static class StationAgentPackageCachePath
{
    private const string SettingName = "OpenLineOps:Agent:PackageCacheDirectory";

    public static string RequireCanonicalAbsolute(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (!Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidDataException(
                $"{SettingName} must be a fully-qualified canonical path.");
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(configuredPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            throw new InvalidDataException(
                $"{SettingName} must be a fully-qualified canonical path.",
                exception);
        }

        var pathRoot = Path.GetPathRoot(canonicalPath);
        var configuredForComparison = configuredPath;
        if (!string.Equals(configuredPath, pathRoot, StringComparison.Ordinal)
            && Path.EndsInDirectorySeparator(configuredPath))
        {
            configuredForComparison = configuredPath[..^1];
            canonicalPath = Path.TrimEndingDirectorySeparator(canonicalPath);
            if (Path.EndsInDirectorySeparator(configuredForComparison))
            {
                throw new InvalidDataException(
                    $"{SettingName} must not use repeated trailing directory separators.");
            }
        }

        if (!string.Equals(
                configuredForComparison,
                canonicalPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{SettingName} must already use its fully-qualified canonical form without dot-segment or repeated-separator aliases.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (pathRoot is null
            || string.Equals(
                Path.TrimEndingDirectorySeparator(canonicalPath),
                Path.TrimEndingDirectorySeparator(pathRoot),
                comparison))
        {
            throw new InvalidDataException(
                $"{SettingName} must be a canonical non-root directory beneath a dedicated namespace anchor.");
        }

        if (OperatingSystem.IsWindows())
        {
            RequireLocalFixedNtfsVolume(canonicalPath, pathRoot);
        }

        return canonicalPath;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireLocalFixedNtfsVolume(
        string canonicalPath,
        string? pathRoot)
    {
        if (pathRoot is null
            || pathRoot.Length != 3
            || !char.IsAsciiLetter(pathRoot[0])
            || pathRoot[1] != Path.VolumeSeparatorChar
            || pathRoot[2] != Path.DirectorySeparatorChar)
        {
            throw new InvalidDataException(
                $"{SettingName} must be a canonical local drive path; UNC and device paths are not supported.");
        }

        try
        {
            var drive = new DriveInfo(pathRoot);
            if (!drive.IsReady
                || drive.DriveType != DriveType.Fixed
                || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{SettingName} must reside on a ready local fixed NTFS volume.");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"{SettingName} volume could not be verified as ready local fixed NTFS storage for '{canonicalPath}'.",
                exception);
        }
    }
}

internal static class StationAgentContentCacheProvisioningCommand
{
    public static void Execute(IConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Station content-cache provisioning requires an elevated Windows administrator or LocalSystem process.");
        }

        ExecuteWindows(configuration);
    }

    public static ValueTask RemovePackageAsync(
        IConfiguration configuration,
        string contentSha256,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Station content-cache package removal requires an elevated Windows administrator or LocalSystem process.");
        }

        return RemovePackageWindowsAsync(configuration, contentSha256, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static void ExecuteWindows(IConfiguration configuration)
    {
        var context = CreateContext(configuration);

        new ImmutableContentProtector().ProvisionCacheNamespace(
            context.Options.PackageCacheDirectory,
            context.Options.WindowsServiceName,
            context.Policy);

        Console.WriteLine(
            $"Provisioned Station content-cache namespace at '{context.Options.PackageCacheDirectory}'.");
    }

    [SupportedOSPlatform("windows")]
    private static async ValueTask RemovePackageWindowsAsync(
        IConfiguration configuration,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        var context = CreateContext(configuration);
        await new ImmutableContentProtector().RemoveProtectedPackageInstallationAsync(
            context.Options.PackageCacheDirectory,
            contentSha256,
            context.Options.WindowsServiceName,
            context.Policy,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"Removed protected Station package '{contentSha256}' from the content cache.");
    }

    [SupportedOSPlatform("windows")]
    private static StationContentCacheAdministrationContext CreateContext(
        IConfiguration configuration)
    {
        EnsureAdministrativeCaller();
        var options = StationAgentContentCacheProvisioningOptions.Load(configuration);
        var stationServiceSid =
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                options.WindowsServiceName);
        var contentReaderSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        return new StationContentCacheAdministrationContext(
            options,
            new ImmutableContentProtectionPolicy(
                contentReaderSid,
                stationServiceSid));
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureAdministrativeCaller()
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        var user = identity.User
            ?? throw new UnauthorizedAccessException(
                "Station content-cache administration requires a Windows token user SID.");
        if (user.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            return;
        }

        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        if (new WindowsPrincipal(identity).IsInRole(administrators))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            "Station content-cache administration requires an elevated Windows administrator or LocalSystem process.");
    }

    private sealed record StationContentCacheAdministrationContext(
        StationAgentContentCacheProvisioningOptions Options,
        ImmutableContentProtectionPolicy Policy);
}
