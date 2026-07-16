using System.Collections.Concurrent;
using System.IO.Compression;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Extensions;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Extensions;

public sealed class FileSystemApplicationExtensionPackageService : IApplicationExtensionPackageService
{
    public const long MaximumCompressedBytes = 256L * 1024 * 1024;
    public const long MultipartEnvelopeOverheadBytes = 1024L * 1024;
    public const long MaximumMultipartBodyBytes =
        MaximumCompressedBytes + MultipartEnvelopeOverheadBytes;
    public const long MaximumExpandedBytes = 512L * 1024 * 1024;
    public const long MaximumFileBytes = 128L * 1024 * 1024;
    public const int MaximumFileCount = 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplicationGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly IPluginPackageCatalog _catalog;
    private readonly IProjectApplicationPluginPackageReferenceStore _referenceStore;
    private readonly IPluginManifestValidator _manifestValidator;

    public FileSystemApplicationExtensionPackageService(
        IPluginPackageCatalog catalog,
        IProjectApplicationPluginPackageReferenceStore referenceStore,
        IPluginManifestValidator manifestValidator)
    {
        _catalog = catalog;
        _referenceStore = referenceStore;
        _manifestValidator = manifestValidator;
    }

    public async ValueTask<Result<IReadOnlyCollection<ApplicationExtensionPackageDetails>>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            var references = ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
                await _referenceStore.ReadAsync(scope, cancellationToken).ConfigureAwait(false));
            var packages = await _catalog.DiscoverAsync(scope, cancellationToken).ConfigureAwait(false);
            var details = packages.Select(package =>
            {
                var reference = references.Single(candidate => string.Equals(
                    candidate.PluginId,
                    package.Manifest.Id,
                    StringComparison.Ordinal));
                return ToDetails(reference, package);
            }).OrderBy(item => item.Reference.PluginId, StringComparer.Ordinal).ToArray();
            return Result.Success<IReadOnlyCollection<ApplicationExtensionPackageDetails>>(details);
        }
        catch (Exception exception) when (IsPackageException(exception))
        {
            return Result.Failure<IReadOnlyCollection<ApplicationExtensionPackageDetails>>(
                InvalidPackage(exception));
        }
    }

    public async ValueTask<Result<ApplicationExtensionPackageDetails>> ImportAsync(
        ProjectApplicationWorkspaceScope scope,
        ImportApplicationExtensionPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        if (!request.ZipContent.CanRead
            || request.CompressedSizeBytes is <= 0 or > MaximumCompressedBytes)
        {
            return Result.Failure<ApplicationExtensionPackageDetails>(ApplicationError.Validation(
                "Plugins.ExtensionArchiveInvalid",
                $"Extension archive must be readable and no larger than {MaximumCompressedBytes} bytes."));
        }

        string portableId;
        try
        {
            portableId = ProjectApplicationPluginPackageReferenceContract.PortableId(
                request.PortableId,
                nameof(request.PortableId));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ApplicationExtensionPackageDetails>(ApplicationError.Validation(
                "Plugins.ExtensionPortableIdInvalid",
                exception.Message));
        }

        await using var gate = await AcquireAsync(scope.ApplicationProjectFilePath, cancellationToken)
            .ConfigureAwait(false);
        var pluginsRoot = scope.PluginsRootPath;
        var stagingPath = Path.Combine(pluginsRoot, $".olo-import-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(pluginsRoot, portableId);
        try
        {
            EnsureApplicationRoot(scope);
            Directory.CreateDirectory(pluginsRoot);
            RejectReparsePoint(pluginsRoot, "Application plugins directory");
            Directory.CreateDirectory(stagingPath);
            await ExtractArchiveAsync(request.ZipContent, stagingPath, cancellationToken)
                .ConfigureAwait(false);
            var package = await FileSystemPluginPackageInspector
                .InspectAsync(stagingPath, cancellationToken)
                .ConfigureAwait(false);
            var validation = _manifestValidator.Validate(package.Manifest);
            if (!validation.IsValid)
            {
                return Result.Failure<ApplicationExtensionPackageDetails>(ApplicationError.Validation(
                    "Plugins.ExtensionManifestInvalid",
                    string.Join("; ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))));
            }

            var references = ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
                await _referenceStore.ReadAsync(scope, cancellationToken).ConfigureAwait(false));
            if (references.Any(reference => string.Equals(
                    reference.PluginId,
                    package.Manifest.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return Result.Failure<ApplicationExtensionPackageDetails>(ApplicationError.Conflict(
                    "Plugins.ExtensionPluginIdAlreadyExists",
                    $"Application already references plugin id '{package.Manifest.Id}'. Remove it before importing another package."));
            }

            var manifestPath = ProjectApplicationPluginPackageReferenceContract.ManifestPath(portableId);
            if (references.Any(reference => string.Equals(
                    reference.ManifestPath,
                    manifestPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return Result.Failure<ApplicationExtensionPackageDetails>(ApplicationError.Conflict(
                    "Plugins.ExtensionPortableIdAlreadyExists",
                    $"Application already references plugin package path '{manifestPath}'."));
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                return Result.Failure<ApplicationExtensionPackageDetails>(ApplicationError.Conflict(
                    "Plugins.ExtensionPackagePathAlreadyExists",
                    $"Application plugin package path '{manifestPath}' already exists but is not referenced."));
            }

            Directory.Move(stagingPath, targetPath);
            var reference = new ProjectApplicationPluginPackageReference(
                package.Manifest.Id,
                package.Manifest.Version,
                manifestPath,
                package.PackageContentSha256);
            var updatedReferences = references.Append(reference).ToArray();
            try
            {
                await _referenceStore.ReplaceAsync(scope, updatedReferences, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                DeleteTree(targetPath);
                throw;
            }

            var committedPackage = package with
            {
                PackagePath = targetPath,
                ManifestPath = Path.Combine(
                    targetPath,
                    ProjectApplicationPluginPackageReferenceContract.ManifestFileName)
            };
            return Result.Success(ToDetails(reference, committedPackage));
        }
        catch (Exception exception) when (IsPackageException(exception))
        {
            return Result.Failure<ApplicationExtensionPackageDetails>(InvalidPackage(exception));
        }
        finally
        {
            DeleteTree(stagingPath);
        }
    }

    public async ValueTask<Result> RemoveAsync(
        ProjectApplicationWorkspaceScope scope,
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(pluginId)
            || !string.Equals(pluginId, pluginId.Trim(), StringComparison.Ordinal))
        {
            return Result.Failure(ApplicationError.Validation(
                "Plugins.ExtensionPluginIdInvalid",
                "Plugin id must be canonical non-empty text."));
        }

        await using var gate = await AcquireAsync(scope.ApplicationProjectFilePath, cancellationToken)
            .ConfigureAwait(false);
        string? quarantinePath = null;
        try
        {
            EnsureApplicationRoot(scope);
            if (!Directory.Exists(scope.PluginsRootPath))
            {
                throw new DirectoryNotFoundException(
                    $"Application plugins directory '{scope.PluginsRootPath}' does not exist.");
            }

            RejectReparsePoint(scope.PluginsRootPath, "Application plugins directory");
            var references = ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
                await _referenceStore.ReadAsync(scope, cancellationToken).ConfigureAwait(false));
            var matches = references.Where(reference => string.Equals(
                    reference.PluginId,
                    pluginId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                return Result.Failure(ApplicationError.NotFound(
                    "Plugins.ExtensionNotFound",
                    $"Plugin extension '{pluginId}' was not found in Application '{scope.ApplicationId}'."));
            }

            var reference = matches[0];
            var portableId = ProjectApplicationPluginPackageReferenceContract.GetPortableId(
                reference.ManifestPath);
            var packagePath = Path.Combine(scope.PluginsRootPath, portableId);
            var package = await FileSystemPluginPackageInspector
                .InspectAsync(packagePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(package.Manifest.Id, reference.PluginId, StringComparison.Ordinal)
                || !string.Equals(package.Manifest.Version, reference.Version, StringComparison.Ordinal)
                || !string.Equals(
                    package.PackageContentSha256,
                    reference.ContentSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Application plugin '{pluginId}' does not match its exact .oloapp reference.");
            }

            quarantinePath = Path.Combine(
                scope.PluginsRootPath,
                $".olo-remove-{Guid.NewGuid():N}");
            Directory.Move(packagePath, quarantinePath);
            var updatedReferences = references
                .Where(candidate => !ReferenceEquals(candidate, reference))
                .ToArray();
            try
            {
                await _referenceStore.ReplaceAsync(
                        scope,
                        updatedReferences,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                Directory.Move(quarantinePath, packagePath);
                quarantinePath = null;
                throw;
            }

            try
            {
                DeleteTree(quarantinePath);
                quarantinePath = null;
            }
            catch (Exception deletionException)
            {
                try
                {
                    await _referenceStore.ReplaceAsync(scope, references, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (Directory.Exists(quarantinePath) && !Directory.Exists(packagePath))
                    {
                        Directory.Move(quarantinePath, packagePath);
                        quarantinePath = null;
                    }
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        $"Extension removal and rollback both failed: {deletionException.Message}",
                        rollbackException);
                }

                throw;
            }

            return Result.Success();
        }
        catch (Exception exception) when (IsPackageException(exception))
        {
            return Result.Failure(InvalidPackage(exception));
        }
    }

    public ValueTask<Result<IReadOnlyCollection<ApplicationExtensionPackageDetails>>> ValidateAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        return ListAsync(scope, cancellationToken);
    }

    private ApplicationExtensionPackageDetails ToDetails(
        ProjectApplicationPluginPackageReference reference,
        PluginPackageDescriptor package)
    {
        return new ApplicationExtensionPackageDetails(
            ProjectApplicationPluginPackageReferenceContract.GetPortableId(reference.ManifestPath),
            reference,
            package,
            _manifestValidator.Validate(package.Manifest));
    }

    private static async ValueTask ExtractArchiveAsync(
        Stream content,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > MaximumFileCount)
        {
            throw new InvalidDataException(
                $"Extension archive must contain between 1 and {MaximumFileCount} entries.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLink(entry);
            var relativePath = CanonicalArchivePath(entry.FullName);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"Extension archive contains duplicate paths or paths differing only by case: '{relativePath}'.");
            }

            var isDirectory = entry.FullName.EndsWith('/');
            if (isDirectory)
            {
                Directory.CreateDirectory(ResolveInside(stagingPath, relativePath));
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaximumFileBytes
                || totalBytes > MaximumExpandedBytes - entry.Length)
            {
                throw new InvalidDataException("Extension archive exceeds expanded size limits.");
            }

            var destinationPath = ResolveInside(stagingPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            long fileBytes = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (fileBytes > MaximumFileBytes - read
                    || totalBytes > MaximumExpandedBytes - read)
                {
                    throw new InvalidDataException(
                        $"Extension archive entry '{relativePath}' exceeds expanded size limits.");
                }

                fileBytes += read;
                totalBytes += read;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (fileBytes != entry.Length || destination.Length != fileBytes)
            {
                throw new InvalidDataException(
                    $"Extension archive entry '{relativePath}' length changed while importing.");
            }
        }
    }

    private static string CanonicalArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || Path.IsPathRooted(value)
            || value.Contains('\\')
            || value.Contains(':')
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException("Extension archive path is not canonical portable text.");
        }

        var normalized = value.EndsWith('/') ? value[..^1] : value;
        if (normalized.Length == 0
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Extension archive path contains an invalid segment.");
        }

        return normalized;
    }

    private static void RejectArchiveLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixMode == unixSymbolicLink
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Extension archive entry '{entry.FullName}' is a symbolic link or reparse point.");
        }
    }

    private static string ResolveInside(string rootPath, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            ? path
            : throw new InvalidDataException("Extension archive path escapes its staging directory.");
    }

    private static void EnsureApplicationRoot(ProjectApplicationWorkspaceScope scope)
    {
        if (!Directory.Exists(scope.ApplicationRootPath)
            || !File.Exists(scope.ApplicationProjectFilePath))
        {
            throw new DirectoryNotFoundException(
                $"Application workspace '{scope.ApplicationRootPath}' is incomplete.");
        }

        RejectReparsePoint(scope.ApplicationRootPath, "Application root");
        RejectReparsePoint(scope.ApplicationProjectFilePath, "Application project file");
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} '{path}' is a reparse point.");
        }
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.TryPop(out var directory))
        {
            RejectReparsePoint(directory, "Extension package directory");
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Extension package path '{entry}' is a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                }
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static async ValueTask<ApplicationGate> AcquireAsync(
        string applicationProjectFilePath,
        CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(applicationProjectFilePath);
        var gate = ApplicationGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ApplicationGate(gate);
    }

    private static bool IsPackageException(Exception exception) => exception is
        ArgumentException or
        InvalidDataException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException;

    private static ApplicationError InvalidPackage(Exception exception) =>
        ApplicationError.Validation("Plugins.ExtensionPackageInvalid", exception.Message);

    private sealed class ApplicationGate(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
