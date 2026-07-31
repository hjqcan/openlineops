using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Execution;

namespace OpenLineOps.Agent;

internal sealed record StationAgentHostOptions(
    string AgentId,
    string StationId,
    string StationSystemId,
    TimeSpan HeartbeatInterval,
    string DataDirectory,
    Uri BrokerUri,
    bool RequireBrokerTls,
    ushort PrefetchCount,
    ushort MaximumConcurrentJobs,
    string PackageDistributionDirectory,
    string PackageCacheDirectory,
    string MaterialArrivalPackageContentSha256,
    IReadOnlyDictionary<string, string> TrustedPackagePublicKeys,
    string RuntimeExecutablePath,
    string PluginHostExecutablePath,
    StationRuntimePythonScriptOptions PythonScript,
    string RuntimeWorkingDirectory,
    string ArtifactDirectory,
    Uri CoordinatorBaseUri,
    string ArtifactUploadBearerToken,
    TimeSpan ArtifactUploadTimeout,
    TimeSpan RuntimeTimeout,
    int MaximumRuntimeOutputBytes,
    string ExternalProgramAppContainerProfileNamespace,
    string SafetyExecutablePath,
    string SafetyWorkingDirectory,
    TimeSpan SafetyTimeout)
{
    internal const string AgentExecutableFileName = "OpenLineOps.Agent.exe";
    internal const string RuntimeExecutableFileName = "OpenLineOps.StationRuntime.exe";
    internal const string PluginHostExecutableFileName = "OpenLineOps.PluginHost.exe";
    internal const string PythonScriptWorkerExecutableFileName = "OpenLineOps.ScriptWorker.exe";
    internal const string LeastPrivilegeLauncherExecutableFileName =
        "OpenLineOps.LeastPrivilegeLauncher.exe";
    internal const string LeastPrivilegeIdentity = "PerExecutionAppContainer";

    public override string ToString() =>
        $"StationAgentHostOptions {{ AgentId = {AgentId}, StationId = {StationId}, "
        + $"BrokerEndpoint = {BrokerUri.Scheme}://{BrokerUri.Host}:{BrokerUri.Port}, "
        + $"CoordinatorEndpoint = {CoordinatorBaseUri.Scheme}://{CoordinatorBaseUri.Authority}, "
        + "ArtifactUploadBearerToken = [REDACTED] }";

    public static StationAgentHostOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("OpenLineOps:Agent");
        var agentId = StationIdentity(
            section["AgentId"],
            "OpenLineOps:Agent:AgentId");
        var stationId = StationIdentity(
            section["StationId"],
            "OpenLineOps:Agent:StationId");
        var dataDirectory = ResolvePath(
            Required(section["DataDirectory"], "OpenLineOps:Agent:DataDirectory"));
        var brokerUriText = Required(
            section["BrokerUri"],
            "OpenLineOps:Agent:BrokerUri");
        if (!Uri.TryCreate(brokerUriText, UriKind.Absolute, out var brokerUri)
            || brokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:BrokerUri must be an absolute amqp or amqps URI.");
        }
        var requireBrokerTls = Boolean(section["RequireBrokerTls"], defaultValue: true);
        ValidateBrokerSecurity(brokerUri, requireBrokerTls);

        var trustedKeys = section
            .GetSection("TrustedPackagePublicKeyFiles")
            .GetChildren()
            .ToDictionary(
                child => Required(child.Key, "Trusted package key id"),
                child => File.ReadAllText(ResolvePath(Required(
                    child.Value,
                    $"Trusted package public key file '{child.Key}'"))),
                StringComparer.Ordinal);
        if (trustedKeys.Count == 0)
        {
            throw new InvalidDataException(
                "At least one OpenLineOps:Agent:TrustedPackagePublicKeyFiles entry is required.");
        }

        var runtimeExecutablePath = ResolveBundledExecutable(
            section["RuntimeExecutablePath"],
            "OpenLineOps:Agent:RuntimeExecutablePath",
            RuntimeExecutableFileName);
        var pluginHostExecutablePath = ResolveBundledExecutable(
            section["PluginHostExecutablePath"],
            "OpenLineOps:Agent:PluginHostExecutablePath",
            PluginHostExecutableFileName);
        var pythonScript = LoadPythonScriptOptions(section.GetSection("PythonScript"));
        var agentExecutablePath = ResolveBundledExecutable(
            AgentExecutableFileName,
            "OpenLineOps Agent executable",
            AgentExecutableFileName);
        EnsureDistinctFileIdentities(
            (agentExecutablePath, "Station Agent"),
            (runtimeExecutablePath, "Station Runtime"),
            (pluginHostExecutablePath, "Plugin Host"),
            (pythonScript.WorkerExecutablePath, "Python Script Worker"),
            (pythonScript.Sandbox.LeastPrivilegeLauncherExecutable!,
                "Least Privilege Launcher"));
        var safetyExecutablePath = ResolvePath(Required(
            section["SafetyExecutablePath"],
            "OpenLineOps:Agent:SafetyExecutablePath"));
        EnsureExistingRegularFile(
            safetyExecutablePath,
            "OpenLineOps:Agent:SafetyExecutablePath");
        EnsureNoReparsePointInPath(
            safetyExecutablePath,
            "OpenLineOps:Agent:SafetyExecutablePath");
        EnsureDifferentFileIdentity(
            runtimeExecutablePath,
            safetyExecutablePath,
            "OpenLineOps:Agent:SafetyExecutablePath must be an independently reviewed "
            + "safety actuator, not OpenLineOps.StationRuntime.");
        var coordinatorBaseUri = ParseCoordinatorBaseUri(
            section["CoordinatorBaseUri"],
            "OpenLineOps:Agent:CoordinatorBaseUri");
        var artifactUploadBearerToken = Required(
            section["ArtifactUploadBearerToken"],
            "OpenLineOps:Agent:ArtifactUploadBearerToken");
        ValidateBearerToken(artifactUploadBearerToken);

        var packageDistributionDirectory = ResolvePath(Required(
            section["PackageDistributionDirectory"],
            "OpenLineOps:Agent:PackageDistributionDirectory"));
        var packageCacheDirectory = StationAgentPackageCachePath.RequireCanonicalAbsolute(Required(
            section["PackageCacheDirectory"],
            "OpenLineOps:Agent:PackageCacheDirectory"));
        var runtimeWorkingDirectory = ResolvePath(
            section["RuntimeWorkingDirectory"],
            Path.Combine(dataDirectory, "work"));
        var artifactDirectory = ResolvePath(
            section["ArtifactDirectory"],
            Path.Combine(dataDirectory, "artifacts"));
        var safetyWorkingDirectory = ResolvePath(
            section["SafetyWorkingDirectory"],
            Path.Combine(dataDirectory, "safety"));
        EnsureMutableRootsOutsidePackageCacheNamespace(
            packageCacheDirectory,
            (dataDirectory, "OpenLineOps:Agent:DataDirectory"),
            (packageDistributionDirectory, "OpenLineOps:Agent:PackageDistributionDirectory"),
            (runtimeWorkingDirectory, "OpenLineOps:Agent:RuntimeWorkingDirectory"),
            (artifactDirectory, "OpenLineOps:Agent:ArtifactDirectory"),
            (safetyWorkingDirectory, "OpenLineOps:Agent:SafetyWorkingDirectory"));

        return new StationAgentHostOptions(
            agentId,
            stationId,
            Required(section["StationSystemId"], "OpenLineOps:Agent:StationSystemId"),
            RequiredDuration(
                section["HeartbeatInterval"],
                "OpenLineOps:Agent:HeartbeatInterval"),
            dataDirectory,
            brokerUri,
            requireBrokerTls,
            UShort(section["PrefetchCount"], 8, "OpenLineOps:Agent:PrefetchCount"),
            UShort(section["MaximumConcurrentJobs"], 4, "OpenLineOps:Agent:MaximumConcurrentJobs"),
            packageDistributionDirectory,
            packageCacheDirectory,
            Sha256(
                section["MaterialArrivalPackageContentSha256"],
                "OpenLineOps:Agent:MaterialArrivalPackageContentSha256"),
            trustedKeys,
            runtimeExecutablePath,
            pluginHostExecutablePath,
            pythonScript,
            runtimeWorkingDirectory,
            artifactDirectory,
            coordinatorBaseUri,
            artifactUploadBearerToken,
            Duration(
                section["ArtifactUploadTimeout"],
                TimeSpan.FromMinutes(5),
                "OpenLineOps:Agent:ArtifactUploadTimeout"),
            Duration(
                section["RuntimeTimeout"],
                TimeSpan.FromHours(1),
                "OpenLineOps:Agent:RuntimeTimeout"),
            PositiveInt(
                section["MaximumRuntimeOutputBytes"],
                1024 * 1024,
                "OpenLineOps:Agent:MaximumRuntimeOutputBytes"),
            Required(
                section["ExternalProgramAppContainerProfileNamespace"],
                "OpenLineOps:Agent:ExternalProgramAppContainerProfileNamespace"),
            safetyExecutablePath,
            safetyWorkingDirectory,
            Duration(
                section["SafetyTimeout"],
                TimeSpan.FromSeconds(5),
                "OpenLineOps:Agent:SafetyTimeout"));
    }

    private static StationRuntimePythonScriptOptions LoadPythonScriptOptions(
        IConfigurationSection section)
    {
        var sandbox = section.GetSection("Sandbox");
        var requireLeastPrivilege = Boolean(
            sandbox["RequireLeastPrivilegeExecution"],
            defaultValue: true);
        var isolationMode = Required(
            sandbox["IsolationMode"],
            "OpenLineOps:Agent:PythonScript:Sandbox:IsolationMode");
        var identity = Required(
            sandbox["LeastPrivilegeIdentity"],
            "OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeIdentity");
        var noInteractivePrompt = Boolean(
            sandbox["LeastPrivilegeNoInteractivePrompt"],
            defaultValue: true);
        if (!requireLeastPrivilege
            || !string.Equals(
                isolationMode,
                StationRuntimePythonScriptIsolationModes.LeastPrivilegeIdentity,
                StringComparison.Ordinal)
            || !string.Equals(identity, LeastPrivilegeIdentity, StringComparison.Ordinal)
            || !noInteractivePrompt
            || !string.IsNullOrWhiteSpace(sandbox["LeastPrivilegeArgumentsTemplate"]))
        {
            throw new InvalidDataException(
                "OpenLineOps Agent Python execution requires the fixed non-interactive "
                + "PerExecutionAppContainer policy without a custom arguments template.");
        }

        return new StationRuntimePythonScriptOptions(
            ResolveBundledExecutable(
                section["WorkerExecutablePath"],
                "OpenLineOps:Agent:PythonScript:WorkerExecutablePath",
                PythonScriptWorkerExecutableFileName),
            ResolvePath(Required(
                section["HostPythonRuntimeDllPath"],
                "OpenLineOps:Agent:PythonScript:HostPythonRuntimeDllPath")),
            new StationRuntimePythonScriptSandboxOptions(
                requireLeastPrivilege,
                isolationMode,
                identity,
                ResolveBundledExecutable(
                    sandbox["LeastPrivilegeLauncherExecutable"],
                    "OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeLauncherExecutable",
                    LeastPrivilegeLauncherExecutableFileName),
                LeastPrivilegeArgumentsTemplate: null,
                LeastPrivilegeNoInteractivePrompt: noInteractivePrompt));
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException($"{name} is required and must be canonical text.")
            : value;

    private static string StationIdentity(string? value, string name) =>
        StationIdentityContract.IsCanonical(value)
            ? value!
            : throw new InvalidDataException(
                $"{name} must satisfy the shared Station identity contract.");

    private static Uri ParseCoordinatorBaseUri(string? value, string name)
    {
        var canonical = Required(value, name);
        if (!Uri.TryCreate(canonical, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                $"{name} must be an HTTPS URI, except loopback HTTP used for local execution.");
        }

        return uri;
    }

    private static void ValidateBearerToken(string token)
    {
        if (token.Length is < 43 or > 86
            || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:ArtifactUploadBearerToken must be a 32-64 byte base64url secret.");
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length is < 32 or > 64)
            {
                throw new FormatException();
            }

            var canonical = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            if (canonical.Length != token.Length
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(canonical),
                    Encoding.ASCII.GetBytes(token)))
            {
                throw new FormatException();
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:ArtifactUploadBearerToken must be a 32-64 byte base64url secret.",
                exception);
        }
    }

    private static string ResolvePath(string? configured, string? fallback = null)
    {
        var value = configured ?? fallback
            ?? throw new InvalidDataException("A required Agent path is missing.");
        return Path.GetFullPath(value, AppContext.BaseDirectory);
    }

    private static void EnsureMutableRootsOutsidePackageCacheNamespace(
        string packageCacheDirectory,
        params (string Path, string SettingName)[] mutableRoots)
    {
        var cacheAnchor = Directory.GetParent(packageCacheDirectory)?.FullName
            ?? throw new InvalidDataException(
                "OpenLineOps:Agent:PackageCacheDirectory must have a dedicated namespace anchor.");
        var cacheAnchorRoot = Path.GetPathRoot(cacheAnchor)
            ?? throw new InvalidDataException(
                "OpenLineOps:Agent:PackageCacheDirectory must have a rooted dedicated namespace anchor.");
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(cacheAnchor),
                Path.TrimEndingDirectorySeparator(cacheAnchorRoot),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:PackageCacheDirectory must be beneath a non-root dedicated namespace anchor.");
        }
        foreach (var (path, settingName) in mutableRoots)
        {
            if (PathsOverlap(path, cacheAnchor))
            {
                throw new InvalidDataException(
                    $"{settingName} must be outside the dedicated PackageCacheDirectory namespace anchor.");
            }
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var canonicalLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var canonicalRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(canonicalLeft, canonicalRight, comparison)
               || IsDescendantPath(canonicalLeft, canonicalRight, comparison)
               || IsDescendantPath(canonicalRight, canonicalLeft, comparison);
    }

    private static bool IsDescendantPath(
        string candidate,
        string ancestor,
        StringComparison comparison)
    {
        var prefix = Path.EndsInDirectorySeparator(ancestor)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static string ResolveBundledExecutable(
        string? configured,
        string settingName,
        string requiredFileName)
    {
        var value = Required(configured, settingName);
        if (!string.Equals(value, requiredFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{settingName} must be exactly '{requiredFileName}' and cannot redirect the Agent outside its release bundle.");
        }

        var bundleRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var fullPath = Path.GetFullPath(requiredFileName, bundleRoot);
        if (!string.Equals(
                Path.GetDirectoryName(fullPath)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                bundleRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{settingName} must resolve directly under the Agent release bundle root.");
        }

        EnsureExistingRegularFile(fullPath, settingName);
        EnsureNoReparsePointInPath(fullPath, settingName, stopAtDirectory: bundleRoot);
        return fullPath;
    }

    private static void EnsureExistingRegularFile(string path, string settingName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{settingName} executable does not exist.",
                path);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"{settingName} must identify a regular file.");
        }
    }

    private static void EnsureNoReparsePointInPath(
        string path,
        string settingName,
        string? stopAtDirectory = null)
    {
        var stopAt = stopAtDirectory is null
            ? null
            : Path.GetFullPath(stopAtDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        FileSystemInfo? current = new FileInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"{settingName} cannot traverse a symbolic link, junction, or other reparse point.");
            }

            if (stopAt is not null
                && string.Equals(
                    current.FullName.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    stopAt,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                break;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }

    private static void EnsureDistinctFileIdentities(
        params (string Path, string Role)[] executables)
    {
        for (var left = 0; left < executables.Length; left++)
        {
            for (var right = left + 1; right < executables.Length; right++)
            {
                EnsureDifferentFileIdentity(
                    executables[left].Path,
                    executables[right].Path,
                    $"Bundled {executables[left].Role} and {executables[right].Role} executables must be distinct files.");
            }
        }
    }

    private static void EnsureDifferentFileIdentity(
        string leftPath,
        string rightPath,
        string errorMessage)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(leftPath, rightPath, comparison))
        {
            throw new InvalidDataException(errorMessage);
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var leftIdentity = ReadWindowsFileIdentity(leftPath);
        var rightIdentity = ReadWindowsFileIdentity(rightPath);
        if (leftIdentity == rightIdentity)
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private static WindowsFileIdentity ReadWindowsFileIdentity(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                $"Could not read the file identity for '{path}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new WindowsFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct WindowsFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private static bool Boolean(string? value, bool defaultValue) =>
        value is null
            ? defaultValue
            : bool.TryParse(value, out var parsed)
                ? parsed
                : throw new InvalidDataException($"'{value}' is not a Boolean Agent setting.");

    private static ushort UShort(string? value, ushort defaultValue, string name) =>
        value is null
            ? defaultValue
            : ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
              && parsed > 0
                ? parsed
                : throw new InvalidDataException($"{name} must be a positive UInt16.");

    private static int PositiveInt(string? value, int defaultValue, string name) =>
        value is null
            ? defaultValue
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
              && parsed > 0
                ? parsed
                : throw new InvalidDataException($"{name} must be a positive integer.");

    private static string Sha256(string? value, string name)
    {
        var canonical = Required(value, name);
        return canonical.Length == 64
               && canonical.All(static character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? canonical
            : throw new InvalidDataException($"{name} must be lowercase hexadecimal SHA-256.");
    }

    private static TimeSpan Duration(string? value, TimeSpan defaultValue, string name) =>
        value is null
            ? defaultValue
            : TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var parsed)
              && parsed > TimeSpan.Zero
                ? parsed
                : throw new InvalidDataException(
                $"{name} must be a positive constant-format duration.");

    private static void ValidateBrokerSecurity(Uri brokerUri, bool requireBrokerTls)
    {
        if (!requireBrokerTls)
        {
            return;
        }

        if (!string.Equals(brokerUri.Scheme, "amqps", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:BrokerUri must use amqps when RequireBrokerTls is true.");
        }

        var separator = brokerUri.UserInfo.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == brokerUri.UserInfo.Length - 1)
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:BrokerUri must include a dedicated non-guest username and non-empty password when RequireBrokerTls is true.");
        }

        string userName;
        string password;
        try
        {
            userName = Uri.UnescapeDataString(brokerUri.UserInfo[..separator]);
            password = Uri.UnescapeDataString(brokerUri.UserInfo[(separator + 1)..]);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:BrokerUri contains invalid escaped credentials.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(userName)
            || string.Equals(userName, "guest", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:BrokerUri must include a dedicated non-guest username and non-empty password when RequireBrokerTls is true.");
        }
    }

    private static TimeSpan RequiredDuration(string? value, string name)
    {
        var canonical = Required(value, name);
        return TimeSpan.TryParseExact(
                   canonical,
                   "c",
                   CultureInfo.InvariantCulture,
                   out var parsed)
               && parsed >= TimeSpan.FromMilliseconds(250)
               && parsed <= TimeSpan.FromSeconds(10)
            ? parsed
            : throw new InvalidDataException(
                $"{name} must be a constant-format duration from 00:00:00.250 through 00:00:10.");
    }
}
