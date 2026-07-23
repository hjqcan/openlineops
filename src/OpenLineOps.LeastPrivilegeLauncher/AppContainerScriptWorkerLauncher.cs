using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.LeastPrivilegeLauncher;

internal static class AppContainerScriptWorkerLauncher
{
    internal const string Identity = "PerExecutionAppContainer";
    internal const string WorkerFileName = "OpenLineOps.ScriptWorker.exe";
    internal const string ProvisionPythonRuntimeCommand = "provision-python-runtime";

    private const int ConfigurationErrorExitCode = 78;
    private const int TokenIntegrityLevelInformationClass = 25;
    private const int TokenIsAppContainerInformationClass = 29;
    private const int ErrorInsufficientBuffer = 122;
    private const uint MediumIntegrityRid = 8192;
    private const int MaximumStaleProfiles = 1_024;
    private const int MaximumMarkerLength = 8_192;
    private const string ProfilePrefix = "OpenLineOps.ScriptWorker.";
    private const string MarkerExtension = ".active.json";
    private const string RestrictedHostMessage =
        "The Least Privilege Launcher cannot create an AppContainer from an already restricted token.";
    private const string PythonRuntimeProvisioningMessage =
        "The Python runtime is not provisioned for OpenLineOps AppContainer execution. "
        + "Run 'OpenLineOps.LeastPrivilegeLauncher.exe provision-python-runtime "
        + "--runtime-dll <absolute-path>' from an elevated installer or administrator console.";

    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE"] =
                "LeastPrivilegeIdentity",
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_REQUIRE_LEAST_PRIVILEGE"] =
                bool.TrueString,
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_IDENTITY"] = Identity
        };

    public static int Run(IReadOnlyList<string> arguments, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(error);

        if (!OperatingSystem.IsWindows())
        {
            error.WriteLine("OpenLineOps Least Privilege Launcher requires Windows.");
            return ConfigurationErrorExitCode;
        }

        if (arguments.Count > 0
            && string.Equals(
                arguments[0],
                ProvisionPythonRuntimeCommand,
                StringComparison.Ordinal))
        {
            return ProvisionPythonRuntime(arguments, error);
        }

        FileStream? marker = null;
        string? markerPath = null;
        string? profileName = null;
        var profileDeleted = false;
        var exitCode = ConfigurationErrorExitCode;
        try
        {
            var command = Parse(arguments);
            ValidateHostToken();
            var markerParent = EnsureTrustedMarkerParent();
            ScavengeStaleProfiles(markerParent);

            profileName = ProfilePrefix + Guid.NewGuid().ToString("N");
            markerPath = Path.Combine(markerParent, profileName + MarkerExtension);
            marker = CreateActiveMarker(markerPath);

            var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
            var profileRoot = Path.GetFullPath(
                WindowsAppContainerIdentity.GetProfileFolderPath(appContainerSid));
            RejectReparsePoint(profileRoot, "AppContainer profile root");

            var contentRoot = Path.Combine(profileRoot, "LocalState", "OpenLineOps", "content");
            var runtimeRoot = Path.Combine(profileRoot, "LocalState", "OpenLineOps", "runtime");
            ConfigureExecutionDirectory(
                contentRoot,
                appContainerSid,
                FileSystemRights.ReadAndExecute);
            ConfigureExecutionDirectory(
                runtimeRoot,
                appContainerSid,
                FileSystemRights.Modify);
            CopyWorkerPayload(command.WorkerPath, contentRoot);

            var pythonRuntimeDll = ResolvePythonRuntimeDll();
            var pythonCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.PythonRuntimeCapabilityName);
            VerifyPythonRuntimeAccess(pythonRuntimeDll, pythonCapabilitySid);

            WriteMarker(
                marker,
                new ActiveProfileMarker(profileName, appContainerSid, runtimeRoot));
            exitCode = LaunchAndProxy(
                Path.Combine(contentRoot, WorkerFileName),
                runtimeRoot,
                profileName,
                pythonRuntimeDll,
                command.Environment);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            error.WriteLine(exception.Message);
            exitCode = ConfigurationErrorExitCode;
        }
        finally
        {
            marker?.Dispose();
            if (profileName is not null)
            {
                try
                {
                    DeleteProfileWithRetry(profileName);
                    profileDeleted = true;
                }
                catch (Exception exception) when (IsOperationalFailure(exception))
                {
                    error.WriteLine(exception.Message);
                    exitCode = ConfigurationErrorExitCode;
                }
            }

            if (profileDeleted && markerPath is not null)
            {
                try
                {
                    TryDeleteMarker(markerPath);
                }
                catch (Exception exception) when (IsOperationalFailure(exception))
                {
                    error.WriteLine(exception.Message);
                    exitCode = ConfigurationErrorExitCode;
                }
            }
        }

        return exitCode;
    }

    [SupportedOSPlatform("windows")]
    private static int ProvisionPythonRuntime(
        IReadOnlyList<string> arguments,
        TextWriter error)
    {
        try
        {
            if (arguments.Count != 3
                || !string.Equals(arguments[1], "--runtime-dll", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Expected exactly 'provision-python-runtime --runtime-dll <absolute-path>'.");
            }

            var runtimeDll = CanonicalPythonRuntimeDll(arguments[2]);
            var runtimeRoot = ValidatePythonRuntimeLayout(runtimeDll);
            var capabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.PythonRuntimeCapabilityName);
            try
            {
                WindowsContentAccessAuthorizer.GrantReadExecute(runtimeRoot, capabilitySid);
                VerifyPythonRuntimeAccess(runtimeDll, capabilitySid);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                throw new InvalidDataException(
                    $"{PythonRuntimeProvisioningMessage} {exception.Message}",
                    exception);
            }
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new PythonRuntimeProvisioningResult(
                    "PythonRuntimeProvisioned",
                    runtimeRoot,
                    runtimeDll,
                    capabilitySid)));
            return 0;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            error.WriteLine(exception.Message);
            return ConfigurationErrorExitCode;
        }
    }

    private static LauncherCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 8
            || !string.Equals(arguments[0], "-n", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "-u", StringComparison.Ordinal)
            || !string.Equals(arguments[2], Identity, StringComparison.Ordinal)
            || !string.Equals(arguments[3], "--", StringComparison.Ordinal)
            || !string.Equals(arguments[4], "env", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Expected exactly the non-interactive PerExecutionAppContainer worker launch protocol.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 5;
        while (index < arguments.Count && arguments[index].Contains('=', StringComparison.Ordinal))
        {
            var assignment = arguments[index];
            var separator = assignment.IndexOf('=', StringComparison.Ordinal);
            var name = assignment[..separator];
            var value = assignment[(separator + 1)..];
            if (!RequiredEnvironment.TryGetValue(name, out var requiredValue)
                || !string.Equals(value, requiredValue, StringComparison.Ordinal)
                || !environment.TryAdd(name, value))
            {
                throw new InvalidDataException(
                    $"Unsupported least-privilege worker environment assignment '{name}'.");
            }

            index++;
        }

        if (environment.Count != RequiredEnvironment.Count
            || RequiredEnvironment.Keys.Any(name => !environment.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "The least-privilege worker environment contract is incomplete.");
        }

        if (index >= arguments.Count)
        {
            throw new InvalidDataException("The least-privilege worker executable is required.");
        }

        var workerPath = CanonicalBundledWorker(arguments[index]);
        index++;
        if (index != arguments.Count)
        {
            throw new InvalidDataException(
                "The bundled Python Script Worker does not accept launcher arguments.");
        }

        return new LauncherCommand(workerPath, environment);
    }

    private static string CanonicalBundledWorker(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException(
                "The least-privilege worker path must be canonical and absolute.");
        }

        var path = Path.GetFullPath(value);
        var bundleRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        if (!string.Equals(Path.GetFileName(path), WorkerFileName, StringComparison.Ordinal)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
                bundleRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The launcher can execute only the co-packaged {WorkerFileName}.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The co-packaged Python Script Worker is missing.", path);
        }
        RejectFileReparsePoint(path, "co-packaged Python Script Worker");
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateHostToken()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var token = identity.AccessToken;
        if (IsTokenRestricted(token)
            || ReadTokenBoolean(token, TokenIsAppContainerInformationClass)
            || ReadIntegrityRid(token) < MediumIntegrityRid)
        {
            throw new InvalidDataException(RestrictedHostMessage);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string EnsureTrustedMarkerParent()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidDataException(
                "The launcher could not resolve the current Local Application Data directory.");
        }

        var localRoot = Path.GetFullPath(localApplicationData);
        RejectReparsePoint(localRoot, "Local Application Data root");
        var applicationRoot = Path.Combine(localRoot, "OpenLineOps");
        Directory.CreateDirectory(applicationRoot);
        RejectReparsePoint(applicationRoot, "OpenLineOps application data root");
        var markerParent = Path.Combine(applicationRoot, "ScriptWorker");
        Directory.CreateDirectory(markerParent);
        RejectReparsePoint(markerParent, "Script Worker marker parent");

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
                          ?? throw new InvalidDataException(
                              "The launcher token does not expose a user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControlRule(security, system);
        AddFullControlRule(security, currentUser);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(markerParent), security);

        var verified = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(markerParent),
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!verified.AreAccessRulesProtected
            || !currentUser.Equals(verified.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new InvalidDataException(
                "The Script Worker marker parent is not protected by the launcher identity.");
        }

        return markerParent;
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControlRule(
        DirectorySecurity security,
        SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static FileStream CreateActiveMarker(string markerPath) =>
        new(
            markerPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.WriteThrough);

    private static void WriteMarker(FileStream marker, ActiveProfileMarker value)
    {
        marker.Position = 0;
        marker.SetLength(0);
        JsonSerializer.Serialize(marker, value);
        marker.Flush(flushToDisk: true);
    }

    private static void ScavengeStaleProfiles(string markerParent)
    {
        var markers = Directory
            .EnumerateFiles(
                markerParent,
                $"{ProfilePrefix}*{MarkerExtension}",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumStaleProfiles + 1)
            .ToArray();
        if (markers.Length > MaximumStaleProfiles)
        {
            throw new InvalidDataException(
                $"The Script Worker marker parent exceeds {MaximumStaleProfiles} recoverable profiles.");
        }

        foreach (var markerPath in markers)
        {
            RejectFileReparsePoint(markerPath, "Script Worker active profile marker");
            FileStream? staleMarker = null;
            try
            {
                staleMarker = new FileStream(
                    markerPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException)
            {
                continue;
            }

            using (staleMarker)
            {
                if (staleMarker.Length > MaximumMarkerLength)
                {
                    throw new InvalidDataException(
                        "A Script Worker active profile marker exceeds the maximum length.");
                }

                var fileName = Path.GetFileName(markerPath);
                var staleProfileName = fileName[..^MarkerExtension.Length];
                ValidateGeneratedProfileName(staleProfileName);
                DeleteProfileWithRetry(staleProfileName);
            }
            TryDeleteMarker(markerPath);
        }
    }

    private static void ValidateGeneratedProfileName(string profileName)
    {
        if (!profileName.StartsWith(ProfilePrefix, StringComparison.Ordinal)
            || profileName.Length != ProfilePrefix.Length + 32
            || !Guid.TryParseExact(profileName[ProfilePrefix.Length..], "N", out _))
        {
            throw new InvalidDataException(
                "A Script Worker active profile marker has a non-canonical identity.");
        }
    }

    private static void DeleteProfileWithRetry(string profileName)
    {
        ValidateGeneratedProfileName(profileName);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
                return;
            }
            catch (Win32Exception exception)
            {
                lastError = exception;
                Thread.Sleep(100);
            }
        }

        throw new IOException(
            $"Could not remove AppContainer profile '{profileName}' after the process tree stopped.",
            lastError);
    }

    private static void TryDeleteMarker(string markerPath)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (FileNotFoundException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureExecutionDirectory(
        string path,
        string appContainerSid,
        FileSystemRights appContainerRights)
    {
        Directory.CreateDirectory(path);
        RejectReparsePoint(path, "AppContainer execution directory");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
                          ?? throw new InvalidDataException(
                              "The launcher token does not expose a user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var appContainer = new SecurityIdentifier(appContainerSid);
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControlRule(security, system);
        AddFullControlRule(security, currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            appContainer,
            appContainerRights,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPythonRuntimeAccess(string runtimeDll, string capabilitySid)
    {
        var root = ValidatePythonRuntimeLayout(runtimeDll);
        try
        {
            WindowsContentAccessAuthorizer.VerifyReadExecute(root, capabilitySid);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or IOException
                                           or InvalidDataException)
        {
            throw new InvalidDataException(
                PythonRuntimeProvisioningMessage,
                exception);
        }
    }

    private static string ValidatePythonRuntimeLayout(string runtimeDll)
    {
        var root = Path.GetDirectoryName(runtimeDll)
                   ?? throw new InvalidDataException(PythonRuntimeProvisioningMessage);
        root = Path.GetFullPath(root);
        RejectReparsePoint(root, "Python runtime root");
        var standardLibraryFile = Path.Combine(root, "Lib", "os.py");
        if (!File.Exists(standardLibraryFile)
            && !Directory.EnumerateFiles(root, "python*.zip", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException(PythonRuntimeProvisioningMessage);
        }
        return root;
    }

    private static void CopyWorkerPayload(string sourceWorkerPath, string contentRoot)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.GetDirectoryName(sourceWorkerPath)
            ?? throw new InvalidDataException(
                "The co-packaged Python Script Worker has no parent directory.")));
        RejectPayloadDirectoryReparsePoints(sourceRoot, Path.GetFullPath(sourceWorkerPath));
        var runtimeConfig = Path.Combine(
            sourceRoot,
            "OpenLineOps.ScriptWorker.runtimeconfig.json");
        var frameworkDependent = File.Exists(runtimeConfig);
        var payload = frameworkDependent
            ? ResolveFrameworkDependentPayload(
                sourceRoot,
                sourceWorkerPath,
                runtimeConfig)
            : [new WorkerPayloadFile(sourceWorkerPath, WorkerFileName)];

        foreach (var file in payload)
        {
            var destinationPath = CanonicalPayloadPath(contentRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            CopyPayloadFile(
                file.SourcePath,
                destinationPath);
        }
    }

    private static WorkerPayloadFile[] ResolveFrameworkDependentPayload(
        string sourceRoot,
        string sourceWorkerPath,
        string runtimeConfig)
    {
        var dependenciesPath = Path.Combine(
            sourceRoot,
            "OpenLineOps.ScriptWorker.deps.json");
        var payload = new Dictionary<string, WorkerPayloadFile>(
            StringComparer.OrdinalIgnoreCase);

        AddPayloadFile(payload, sourceRoot, sourceWorkerPath, WorkerFileName);
        AddPayloadFile(
            payload,
            sourceRoot,
            dependenciesPath,
            Path.GetFileName(dependenciesPath));
        AddPayloadFile(
            payload,
            sourceRoot,
            runtimeConfig,
            Path.GetFileName(runtimeConfig));

        RejectFileReparsePoint(dependenciesPath, "Python Script Worker dependency manifest");
        using var stream = new FileStream(
            dependenciesPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.SequentialScan);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var root = document.RootElement;
        var runtimeTargetName = RequiredString(
            RequiredProperty(root, "runtimeTarget"),
            "name");
        var targets = RequiredProperty(root, "targets");
        if (targets.ValueKind != JsonValueKind.Object
            || !targets.TryGetProperty(runtimeTargetName, out var target)
            || target.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The Python Script Worker dependency manifest has no runtime target.");
        }

        foreach (var library in target.EnumerateObject())
        {
            if (library.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The Python Script Worker dependency manifest contains an invalid library.");
            }
            AddFlattenedAssets(payload, sourceRoot, library.Value, "runtime");
            AddFlattenedAssets(payload, sourceRoot, library.Value, "native");
            AddResourceAssets(payload, sourceRoot, library.Value);
            AddCurrentRuntimeAssets(payload, sourceRoot, library.Value);
        }

        return payload.Values.ToArray();
    }

    private static void AddFlattenedAssets(
        IDictionary<string, WorkerPayloadFile> payload,
        string sourceRoot,
        JsonElement library,
        string groupName)
    {
        if (!library.TryGetProperty(groupName, out var assets))
        {
            return;
        }
        RequireObject(assets, groupName);
        foreach (var asset in assets.EnumerateObject())
        {
            var fileName = Path.GetFileName(CanonicalManifestAsset(asset.Name));
            AddPayloadFile(payload, sourceRoot, fileName, fileName);
        }
    }

    private static void AddResourceAssets(
        IDictionary<string, WorkerPayloadFile> payload,
        string sourceRoot,
        JsonElement library)
    {
        if (!library.TryGetProperty("resources", out var assets))
        {
            return;
        }
        RequireObject(assets, "resources");
        foreach (var asset in assets.EnumerateObject())
        {
            var relativePath = CanonicalManifestAsset(asset.Name);
            var parent = Path.GetDirectoryName(relativePath)
                         ?? throw new InvalidDataException(
                             "A Python Script Worker resource has no culture directory.");
            var culture = Path.GetFileName(parent);
            var destination = Path.Combine(culture, Path.GetFileName(relativePath));
            AddPayloadFile(payload, sourceRoot, destination, destination);
        }
    }

    private static void AddCurrentRuntimeAssets(
        IDictionary<string, WorkerPayloadFile> payload,
        string sourceRoot,
        JsonElement library)
    {
        if (!library.TryGetProperty("runtimeTargets", out var assets))
        {
            return;
        }
        RequireObject(assets, "runtimeTargets");
        var currentRuntimeIdentifier = CurrentWindowsRuntimeIdentifier();
        foreach (var asset in assets.EnumerateObject())
        {
            if (asset.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The Python Script Worker dependency manifest contains an invalid runtime target asset.");
            }
            if (!string.Equals(
                    RequiredString(asset.Value, "rid"),
                    currentRuntimeIdentifier,
                    StringComparison.Ordinal))
            {
                continue;
            }
            var relativePath = CanonicalManifestAsset(asset.Name);
            AddPayloadFile(payload, sourceRoot, relativePath, relativePath);
        }
    }

    private static void AddPayloadFile(
        IDictionary<string, WorkerPayloadFile> payload,
        string sourceRoot,
        string sourcePath,
        string relativeDestinationPath)
    {
        var canonicalSource = Path.IsPathFullyQualified(sourcePath)
            ? Path.GetFullPath(sourcePath)
            : CanonicalPayloadPath(sourceRoot, sourcePath);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (!canonicalSource.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(canonicalSource))
        {
            throw new FileNotFoundException(
                "The Python Script Worker payload is incomplete.",
                canonicalSource);
        }
        RejectPayloadDirectoryReparsePoints(canonicalRoot, canonicalSource);

        var relativePath = CanonicalRelativePayloadPath(relativeDestinationPath);
        var file = new WorkerPayloadFile(canonicalSource, relativePath);
        if (payload.TryGetValue(relativePath, out var existing))
        {
            if (!string.Equals(
                    existing.SourcePath,
                    file.SourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Python Script Worker payload assets collide at '{relativePath}'.");
            }
            return;
        }
        payload.Add(relativePath, file);
    }

    private static void RejectPayloadDirectoryReparsePoints(
        string sourceRoot,
        string sourcePath)
    {
        RejectReparsePoint(sourceRoot, "Python Script Worker payload root");
        var parent = Path.GetDirectoryName(sourcePath)
                     ?? throw new InvalidDataException(
                         "A Python Script Worker payload asset has no parent directory.");
        var relativeParent = Path.GetRelativePath(sourceRoot, parent);
        if (string.Equals(relativeParent, ".", StringComparison.Ordinal))
        {
            return;
        }

        var current = sourceRoot;
        foreach (var segment in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current, "Python Script Worker payload directory");
        }
    }

    private static string CanonicalManifestAsset(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value[0] == '/')
        {
            throw new InvalidDataException(
                "The Python Script Worker dependency manifest contains a non-canonical asset path.");
        }
        var segments = value.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                    || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "The Python Script Worker dependency manifest contains a non-canonical asset path.");
        }
        return Path.Combine(segments);
    }

    private static string CanonicalRelativePayloadPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException(
                "The Python Script Worker payload contains a non-canonical relative path.");
        }
        var segments = value.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                    || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "The Python Script Worker payload contains a non-canonical relative path.");
        }
        return Path.Combine(segments);
    }

    private static string CanonicalPayloadPath(string root, string relativePath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        if (!path.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A Python Script Worker payload asset escapes its bundle root.");
        }
        return path;
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException(
                $"The Python Script Worker dependency manifest omits '{name}'.");
        }
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"The Python Script Worker dependency manifest has an invalid '{name}'.");
        }
        return value.GetString()!;
    }

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The Python Script Worker dependency manifest has an invalid '{name}'.");
        }
    }

    private static string CurrentWindowsRuntimeIdentifier() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                "The Python Script Worker does not support this Windows architecture.")
        };

    private static void CopyPayloadFile(string sourcePath, string destinationPath)
    {
        RejectFileReparsePoint(sourcePath, "Python Script Worker payload file");
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1_048_576,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1_048_576,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static string ResolvePythonRuntimeDll()
    {
        var value = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        return CanonicalPythonRuntimeDll(value);
    }

    private static string CanonicalPythonRuntimeDll(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException(
                "PYTHONNET_PYDLL must identify a canonical absolute Python runtime DLL.");
        }

        var path = Path.GetFullPath(value);
        if (!string.Equals(path, value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "PYTHONNET_PYDLL must identify a canonical absolute Python runtime DLL.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The configured Python runtime DLL is missing.", path);
        }
        RejectFileReparsePoint(path, "configured Python runtime DLL");
        return path;
    }

    private static int LaunchAndProxy(
        string workerPath,
        string runtimeRoot,
        string profileName,
        string pythonRuntimeDll,
        IReadOnlyDictionary<string, string> requiredEnvironment)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyEnvironment(environment, "SystemRoot");
        CopyEnvironment(environment, "WINDIR");
        environment["PATH"] = DeterministicWorkerPath(pythonRuntimeDll);
        foreach (var pair in requiredEnvironment)
        {
            environment.Add(pair.Key, pair.Value);
        }
        environment["PYTHONNET_PYDLL"] = pythonRuntimeDll;
        environment["LOCALAPPDATA"] = runtimeRoot;
        environment["TEMP"] = runtimeRoot;
        environment["TMP"] = runtimeRoot;
        environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = runtimeRoot;
        environment["OPENLINEOPS_SCRIPT_WORKER_RUNTIME_ROOT"] = runtimeRoot;

        using var process = new WindowsProcessLauncher().Launch(
            new IsolatedProcessStartRequest(
                workerPath,
                [],
                runtimeRoot,
                environment,
                new WindowsProcessLimits(
                    ActiveProcessLimit: 32,
                    ProcessMemoryLimitBytes: 1L * 1024 * 1024 * 1024,
                    JobMemoryLimitBytes: 2L * 1024 * 1024 * 1024,
                    CpuTimeLimit: TimeSpan.FromHours(1)),
                new WindowsAppContainerPolicy(
                    profileName,
                    NetworkAccessAllowed: false,
                    [WindowsAppContainerIdentity.PythonRuntimeCapabilityName])));

        return ProxyStandardStreamsAsync(process).GetAwaiter().GetResult();
    }

    private static string DeterministicWorkerPath(string pythonRuntimeDll)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot) || !Path.IsPathFullyQualified(systemRoot))
        {
            throw new InvalidDataException(
                "The launcher requires a canonical SystemRoot for the isolated worker.");
        }

        var systemDirectory = Path.Combine(Path.GetFullPath(systemRoot), "System32");
        var pythonRuntimeRoot = Path.GetDirectoryName(pythonRuntimeDll)
                                ?? throw new InvalidDataException(
                                    "The configured Python runtime DLL has no parent directory.");
        return string.Join(Path.PathSeparator, systemDirectory, pythonRuntimeRoot);
    }

    private static async Task<int> ProxyStandardStreamsAsync(WindowsIsolatedProcess process)
    {
        var input = CopyInputAsync(process.StandardInput);
        var output = process.StandardOutput.CopyToAsync(Console.OpenStandardOutput());
        var error = process.StandardError.CopyToAsync(Console.OpenStandardError());
        await process.WaitForExitAsync();
        process.TerminateProcessTree();
        await Task.WhenAll(input, output, error);
        return process.ExitCode;
    }

    private static async Task CopyInputAsync(Stream childInput)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(childInput);
            await childInput.FlushAsync();
        }
        catch (IOException)
        {
        }
        finally
        {
            childInput.Dispose();
        }
    }

    private static void CopyEnvironment(Dictionary<string, string> target, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            target[name] = value;
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The {description} does not exist: '{path}'.");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The {description} cannot be a reparse point: '{path}'.");
        }
    }

    private static void RejectFileReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The {description} cannot be a reparse point: '{path}'.");
        }
    }

    private static bool IsOperationalFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception
            or JsonException;

    private static bool ReadTokenBoolean(SafeAccessTokenHandle token, int informationClass)
    {
        var buffer = ReadTokenInformation(token, informationClass);
        try
        {
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ReadIntegrityRid(SafeAccessTokenHandle token)
    {
        var buffer = ReadTokenInformation(token, TokenIntegrityLevelInformationClass);
        try
        {
            var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
            var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
            var count = countPointer == IntPtr.Zero ? 0 : Marshal.ReadByte(countPointer);
            var ridPointer = count == 0
                ? IntPtr.Zero
                : GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
            if (ridPointer == IntPtr.Zero)
            {
                throw new InvalidDataException("The launcher token integrity SID is invalid.");
            }
            return unchecked((uint)Marshal.ReadInt32(ridPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr ReadTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                error,
                "Could not determine launcher token information size.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        if (!GetTokenInformation(token, informationClass, buffer, required, out _))
        {
            var exception = new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read launcher token information.");
            Marshal.FreeHGlobal(buffer);
            throw exception;
        }
        return buffer;
    }

    private sealed record LauncherCommand(
        string WorkerPath,
        IReadOnlyDictionary<string, string> Environment);

    private sealed record PythonRuntimeProvisioningResult(
        string Operation,
        string RuntimeRoot,
        string RuntimeDll,
        string CapabilitySid);

    private sealed record WorkerPayloadFile(
        string SourcePath,
        string RelativePath);

    private sealed record ActiveProfileMarker(
        string ProfileName,
        string AppContainerSid,
        string RuntimeRoot);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes(IntPtr sid, uint attributes)
    {
        public readonly IntPtr Sid = sid;
        public readonly uint Attributes = attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenMandatoryLabel(SidAndAttributes label)
    {
        public readonly SidAndAttributes Label = label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}
