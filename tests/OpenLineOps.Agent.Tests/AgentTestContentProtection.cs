using OpenLineOps.ContentProtection;

namespace OpenLineOps.Agent.Tests;

internal static class AgentTestStationServiceIdentity
{
    public const string EnvironmentVariable = "OPENLINEOPS_TEST_WINDOWS_SERVICE_NAME";
    public const string CanonicalFixtureServiceName = "TrustedInstaller";

    public static string ConfiguredOrFixtureSid()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var serviceName = WindowsStationServiceIdentityReader.IsCanonicalServiceName(configured)
            ? configured!
            : CanonicalFixtureServiceName;
        return WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(serviceName);
    }

    public static string ConfiguredOrFixtureName()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return WindowsStationServiceIdentityReader.IsCanonicalServiceName(configured)
            ? configured!
            : CanonicalFixtureServiceName;
    }

    public static bool TryReadCurrent(out WindowsStationServiceIdentity identity)
    {
        identity = null!;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!WindowsStationServiceIdentityReader.IsCanonicalServiceName(configured))
        {
            return false;
        }

        try
        {
            identity = WindowsStationServiceIdentityReader.ReadRequired(
                WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(configured!));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal static class AgentTestStationPackageCache
{
    public static void RemovePackageInstallations(
        string cacheRoot,
        IImmutableContentProtector protector,
        ImmutableContentProtectionPolicy policy)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(cacheRoot).ToArray())
        {
            var leaf = Path.GetFileName(directory);
            if (leaf.Length != 64
                || !leaf.All(static character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                continue;
            }

            protector.RemoveProtectedPackageInstallationAsync(
                    cacheRoot,
                    leaf,
                    AgentTestStationServiceIdentity.ConfiguredOrFixtureName(),
                    policy)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }
}

internal sealed class InventoryOnlyTestContentProtector(
    bool markFilesReadOnly = false,
    int? failFirstProtectionAfterFileCount = null,
    int failProtectionCall = 1)
    : IImmutableContentProtector
{
    private readonly ImmutableContentProtector _inventoryVerifier = new();
    private int _protectionFailureArmed = failFirstProtectionAfterFileCount is null ? 0 : 1;
    private int _protectionCallCount;

    public void ProtectCacheBoundary(
        string cacheRootDirectory,
        ImmutableContentProtectionPolicy policy)
    {
        _ = policy;
        Directory.CreateDirectory(cacheRootDirectory);
    }

    public void VerifyCacheBoundary(
        string cacheRootDirectory,
        ImmutableContentProtectionPolicy policy)
    {
        _ = policy;
        if (!Directory.Exists(cacheRootDirectory))
        {
            throw new DirectoryNotFoundException(cacheRootDirectory);
        }
    }

    public async ValueTask ProtectAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        _ = policy;
        await _inventoryVerifier.VerifyInventoryAsync(
            rootDirectory,
            files,
            cancellationToken);
        var currentCall = Interlocked.Increment(ref _protectionCallCount);
        var protectedFileCount = 0;
        foreach (ImmutableContentFile file in files)
        {
            if (markFilesReadOnly)
            {
                var path = Path.Combine(
                    rootDirectory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }

            protectedFileCount++;
            if (currentCall == failProtectionCall
                && failFirstProtectionAfterFileCount == protectedFileCount
                && Interlocked.Exchange(ref _protectionFailureArmed, 0) == 1)
            {
                throw new InvalidOperationException(
                    $"Injected immutable content protection failure after file {protectedFileCount}.");
            }
        }
    }

    public async ValueTask VerifyAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        _ = policy;
        await _inventoryVerifier.VerifyInventoryAsync(
            rootDirectory,
            files,
            cancellationToken);
        if (markFilesReadOnly && files.Any(file =>
                !File.GetAttributes(Path.Combine(
                        rootDirectory,
                        file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                    .HasFlag(FileAttributes.ReadOnly)))
        {
            throw new InvalidDataException(
                "Immutable test content is not completely protected.");
        }
    }

    public ValueTask VerifyInventoryAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        CancellationToken cancellationToken = default) =>
        _inventoryVerifier.VerifyInventoryAsync(rootDirectory, files, cancellationToken);

    public ValueTask RemoveProtectedPackageInstallationAsync(
        string cacheRootDirectory,
        string contentSha256,
        string windowsServiceName,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        _ = windowsServiceName;
        _ = policy;
        cancellationToken.ThrowIfCancellationRequested();
        var contentDirectory = Path.Combine(cacheRootDirectory, contentSha256);
        var commitDirectory = Path.Combine(
            cacheRootDirectory,
            $".{contentSha256}.installed");
        if (Directory.Exists(commitDirectory) && !Directory.Exists(contentDirectory))
        {
            throw new InvalidDataException(
                "Station package commit exists without content.");
        }

        if (Directory.Exists(commitDirectory))
        {
            DeleteDirectory(commitDirectory);
        }

        if (Directory.Exists(contentDirectory))
        {
            DeleteDirectory(contentDirectory);
        }

        return ValueTask.CompletedTask;
    }

    private static void DeleteDirectory(string contentDirectory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     contentDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(contentDirectory, recursive: true);
    }
}
