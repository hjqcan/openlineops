using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenLineOps.Agent.Tests;

internal static class AppContainerPythonRuntimeTestSupport
{
    private const int MaximumFileAliasDepth = 16;

    private static readonly Lazy<string> RuntimeDllPath = new(
        CreateRuntimeCopy,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string ResolveRuntimeDll() => RuntimeDllPath.Value;

    private static string CreateRuntimeCopy()
    {
        var sourceDll = DiscoverSourceRuntimeDll();
        var sourceRoot = Path.GetDirectoryName(sourceDll)
                         ?? throw new InvalidDataException(
                             "The test Python runtime DLL has no parent directory.");
        var sourceDllHash = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(sourceDll)));
        var cacheKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(sourceRoot).ToUpperInvariant()
                + "|"
                + sourceDllHash)));
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "PythonRuntime",
            cacheKey);
        var destinationDll = Path.Combine(destinationRoot, Path.GetFileName(sourceDll));
        using var mutex = new Mutex(
            initiallyOwned: false,
            $"Local\\OpenLineOps.Tests.PythonRuntime.{cacheKey}");
        var lockTaken = false;
        try
        {
            lockTaken = mutex.WaitOne(TimeSpan.FromMinutes(2));
        }
        catch (AbandonedMutexException)
        {
            lockTaken = true;
        }
        if (!lockTaken)
        {
            throw new TimeoutException(
                "Timed out waiting for the shared AppContainer Python test runtime.");
        }

        try
        {
            if (File.Exists(destinationDll) && HasStandardLibrary(destinationRoot))
            {
                ProvisionRuntimeAccess(destinationRoot, destinationDll);
                return destinationDll;
            }

            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }
            MaterializeRuntimeTree(sourceRoot, destinationRoot);
            if (!File.Exists(destinationDll) || !HasStandardLibrary(destinationRoot))
            {
                throw new InvalidDataException(
                    "The copied test Python runtime is incomplete.");
            }
            ProvisionRuntimeAccess(destinationRoot, destinationDll);
            return destinationDll;
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static bool HasStandardLibrary(string runtimeRoot) =>
        File.Exists(Path.Combine(runtimeRoot, "Lib", "os.py"))
        || Directory.EnumerateFiles(
                runtimeRoot,
                "python*.zip",
                SearchOption.TopDirectoryOnly)
            .Any();

    private static void ProvisionRuntimeAccess(string runtimeRoot, string runtimeDll)
    {
        var stagedBundleRoot = Environment.GetEnvironmentVariable(
            "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT");
        var launcherRoot = string.IsNullOrWhiteSpace(stagedBundleRoot)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(stagedBundleRoot);
        var launcherPath = Path.Combine(
            launcherRoot,
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException(
                "The AppContainer Python runtime provisioning launcher is missing.",
                launcherPath);
        }

        var startInfo = new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = launcherRoot
        };
        startInfo.ArgumentList.Add("provision-python-runtime");
        startInfo.ArgumentList.Add("--runtime-dll");
        startInfo.ArgumentList.Add(runtimeDll);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The AppContainer Python runtime provisioning process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(
                "The AppContainer Python runtime provisioning process timed out.");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "The AppContainer Python runtime provisioning process failed: " + error);
        }

        using var evidence = JsonDocument.Parse(output);
        var root = evidence.RootElement;
        if (!string.Equals(
                root.GetProperty("Operation").GetString(),
                "PythonRuntimeProvisioned",
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFullPath(root.GetProperty("RuntimeRoot").GetString() ?? string.Empty),
                Path.GetFullPath(runtimeRoot),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFullPath(root.GetProperty("RuntimeDll").GetString() ?? string.Empty),
                Path.GetFullPath(runtimeDll),
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(root.GetProperty("CapabilitySid").GetString()))
        {
            throw new InvalidDataException(
                "The AppContainer Python runtime provisioning evidence is invalid.");
        }
    }

    private static string DiscoverSourceRuntimeDll()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-c \"import pathlib,sysconfig; print(pathlib.Path(sysconfig.get_config_var('BINDIR')).joinpath(sysconfig.get_config_var('LDLIBRARY')).resolve())\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException(
            "Python runtime discovery process did not start.");
        if (!process.WaitForExit(2_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(
                "AppContainer tests timed out while discovering the Python runtime DLL.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "AppContainer tests could not discover the Python runtime DLL.");
        }

        configured = process.StandardOutput.ReadLine();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? Path.GetFullPath(configured)
            : throw new InvalidOperationException(
                "AppContainer tests require an installed Python runtime DLL.");
    }

    internal static void MaterializeRuntimeTree(string sourceRoot, string destinationRoot) =>
        MaterializeRuntimeTree(
            sourceRoot,
            destinationRoot,
            File.GetAttributes,
            path => File.ResolveLinkTarget(path, returnFinalTarget: false));

    internal static void MaterializeRuntimeTree(
        string sourceRoot,
        string destinationRoot,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileSystemInfo?> resolveFileLinkTarget) =>
        CopyDirectory(
            Path.GetFullPath(sourceRoot),
            Path.GetFullPath(destinationRoot),
            relativePath: string.Empty,
            getAttributes,
            resolveFileLinkTarget);

    private static void CopyDirectory(
        string sourceRoot,
        string destinationRoot,
        string relativePath,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileSystemInfo?> resolveFileLinkTarget)
    {
        var source = relativePath.Length == 0
            ? sourceRoot
            : Path.Combine(sourceRoot, relativePath);
        if ((getAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The test Python runtime cannot contain a reparse directory: '{source}'.");
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            CopyRuntimeFile(
                sourceRoot,
                file,
                Path.Combine(destinationRoot, Path.GetFileName(file)),
                getAttributes,
                resolveFileLinkTarget);
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var childRelativePath = relativePath.Length == 0
                ? Path.GetFileName(directory)
                : Path.Combine(relativePath, Path.GetFileName(directory));
            if (string.Equals(
                    childRelativePath,
                    Path.Combine("Lib", "site-packages"),
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(directory),
                    "__pycache__",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(
                sourceRoot,
                Path.Combine(destinationRoot, Path.GetFileName(directory)),
                childRelativePath,
                getAttributes,
                resolveFileLinkTarget);
        }
    }

    private static void CopyRuntimeFile(
        string sourceRoot,
        string sourcePath,
        string destinationPath,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileSystemInfo?> resolveFileLinkTarget)
    {
        var sourceAttributes = getAttributes(sourcePath);
        var materializedSource = (sourceAttributes & FileAttributes.ReparsePoint) == 0
            ? sourcePath
            : ResolveContainedRuntimeFileAlias(
                sourceRoot,
                sourcePath,
                getAttributes,
                resolveFileLinkTarget);
        File.Copy(materializedSource, destinationPath);
    }

    private static string ResolveContainedRuntimeFileAlias(
        string sourceRoot,
        string aliasPath,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileSystemInfo?> resolveFileLinkTarget)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var current = Path.GetFullPath(aliasPath);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; ; depth++)
        {
            EnsureContainedRuntimePath(canonicalRoot, current);
            RejectReparseDirectoryTraversal(canonicalRoot, current, getAttributes);
            if (!File.Exists(current))
            {
                throw new InvalidDataException(
                    $"The test Python runtime contains a dangling file alias: '{aliasPath}'.");
            }

            var attributes = getAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return current;
            }
            if (!visited.Add(current))
            {
                throw new InvalidDataException(
                    $"The test Python runtime contains a cyclic file alias: '{aliasPath}'.");
            }
            if (depth >= MaximumFileAliasDepth)
            {
                throw new InvalidDataException(
                    $"The test Python runtime file alias exceeds {MaximumFileAliasDepth} links: "
                    + $"'{aliasPath}'.");
            }

            FileSystemInfo? target;
            try
            {
                target = resolveFileLinkTarget(current);
            }
            catch (IOException exception)
            {
                throw new InvalidDataException(
                    $"The test Python runtime contains an unsupported reparse file: '{current}'.",
                    exception);
            }
            if (target is not FileInfo)
            {
                throw new InvalidDataException(
                    $"The test Python runtime contains an unsupported reparse file: '{current}'.");
            }

            current = Path.GetFullPath(target.FullName);
        }
    }

    private static void EnsureContainedRuntimePath(string sourceRoot, string path)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, path);
        if (Path.IsPathFullyQualified(relativePath)
            || string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The test Python runtime file alias cannot leave its runtime root: '{path}'.");
        }
    }

    private static void RejectReparseDirectoryTraversal(
        string sourceRoot,
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        if ((getAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The test Python runtime cannot contain a reparse directory: '{sourceRoot}'.");
        }

        var relativePath = Path.GetRelativePath(sourceRoot, path);
        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = sourceRoot;
        for (var index = 0; index < components.Length - 1; index++)
        {
            current = Path.Combine(current, components[index]);
            if (!Directory.Exists(current)
                || (getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The test Python runtime file alias cannot traverse a reparse directory: "
                    + $"'{current}'.");
            }
        }
    }
}
