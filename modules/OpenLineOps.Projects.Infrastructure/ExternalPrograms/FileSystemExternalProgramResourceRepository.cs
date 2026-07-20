using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.ExternalPrograms;

namespace OpenLineOps.Projects.Infrastructure.ExternalPrograms;

public sealed class FileSystemExternalProgramResourceRepository : IExternalProgramResourceRepository
{
    private static readonly ApplicationLockRegistry ApplicationLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async ValueTask<IReadOnlyCollection<ExternalProgramResource>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        var root = GetResourcesRoot(scope);
        if (!Directory.Exists(root))
        {
            return [];
        }

        ValidateResourcesRoot(root);
        RecoverAllInterruptedCommits(root);
        var resourceIds = ValidateResourcesRoot(root);
        var resources = new List<ExternalProgramResource>();
        foreach (var resourceId in resourceIds.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = await ReadAsync(scope, resourceId, cancellationToken).ConfigureAwait(false);
            resources.Add(resource);
        }

        return resources;
    }

    public async ValueTask<ExternalProgramResource?> GetAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        var root = GetResourcesRoot(scope);
        if (Directory.Exists(root))
        {
            ValidateResourcesRoot(root);
            RecoverInterruptedCommit(root, resourceId);
            ValidateResourcesRoot(root);
        }
        var descriptorPath = GetDescriptorPath(scope, resourceId);
        return File.Exists(descriptorPath)
            ? await ReadAsync(scope, resourceId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<ExternalProgramResource> SaveDefinitionAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ExternalProgramResourceValidator.ValidateDefinition(request);
        EnsureUtc(updatedAtUtc);
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        return await SaveLockedAsync(
                scope,
                request,
                [],
                replaceFileSet: request.LaunchKind == ExternalProgramLaunchKind.Provider,
                updatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ExternalProgramResource> ImportDirectoryAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> files,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(files);
        ExternalProgramResourceValidator.ValidateDefinition(request);
        if (request.LaunchKind != ExternalProgramLaunchKind.ApplicationExecutable)
        {
            throw new ArgumentException(
                "External program directory imports require ApplicationExecutable launch kind.",
                nameof(request));
        }
        ValidateUploads(files);
        EnsureUtc(updatedAtUtc);
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        return await SaveLockedAsync(
                scope,
                request,
                files,
                replaceFileSet: true,
                updatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<ExternalProgramResource> SaveLockedAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> uploads,
        bool replaceFileSet,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var root = GetResourcesRoot(scope);
        Directory.CreateDirectory(root);
        ValidateResourcesRoot(root);
        RecoverInterruptedCommit(root, request.ResourceId);
        ValidateResourcesRoot(root);
        var finalDirectory = ResolveInside(root, request.ResourceId);
        var stagingDirectory = ResolveInside(root, $".{request.ResourceId}.staging");
        var backupDirectory = ResolveInside(root, $".{request.ResourceId}.backup");
        if (File.Exists(finalDirectory))
        {
            throw new InvalidDataException(
                $"External program resource path '{finalDirectory}' must be a directory.");
        }

        var existingResource = Directory.Exists(finalDirectory)
            ? await ReadAsync(scope, request.ResourceId, cancellationToken).ConfigureAwait(false)
            : null;
        AssertTransactionPathAbsent(stagingDirectory);
        AssertTransactionPathAbsent(backupDirectory);
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            if (!replaceFileSet && existingResource is not null)
            {
                await CopyFrozenFileSetAsync(
                        finalDirectory,
                        stagingDirectory,
                        existingResource.Files,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var upload in uploads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyUploadedFileAsync(stagingDirectory, upload, cancellationToken).ConfigureAwait(false);
            }

            var resource = await BuildResourceAsync(
                    stagingDirectory,
                    request,
                    updatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteDescriptorAsync(stagingDirectory, resource, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CommitDirectory(stagingDirectory, finalDirectory, backupDirectory);
            return resource;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async ValueTask DeleteAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        var resourcesRoot = GetResourcesRoot(scope);
        if (Directory.Exists(resourcesRoot))
        {
            ValidateResourcesRoot(resourcesRoot);
            RecoverInterruptedCommit(resourcesRoot, resourceId);
            ValidateResourcesRoot(resourcesRoot);
        }
        var directory = GetResourceDirectory(scope, resourceId);
        var tombstone = ResolveInside(
            GetResourcesRoot(scope),
            $".{resourceId}.deleting");
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
        {
            return;
        }

        ValidateResourceDirectoryLayout(directory, requireDescriptor: true);
        Directory.Move(directory, tombstone);
        TryDeleteDirectory(tombstone);
    }

    private static async ValueTask<ExternalProgramResource> ReadAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var directory = GetResourceDirectory(scope, resourceId);
        ValidateResourceDirectoryLayout(directory, requireDescriptor: true);
        var descriptorPath = GetDescriptorPath(scope, resourceId);
        RejectReparsePoint(descriptorPath);
        await using var stream = new FileStream(
            descriptorPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<ExternalProgramResourceDocument>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("External program resource descriptor is empty.");
        if (!string.Equals(document.Schema, ExternalProgramResourceContract.Schema, StringComparison.Ordinal)
            || !string.Equals(document.ResourceKind, ExternalProgramResourceContract.ResourceKind, StringComparison.Ordinal)
            || !string.Equals(document.ResourceId, resourceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("External program resource descriptor identity is invalid.");
        }

        var resource = ToResource(document);
        ExternalProgramResourceValidator.ValidateFrozenResource(resource);
        var actualFiles = await InventoryFilesAsync(directory, cancellationToken).ConfigureAwait(false);
        if (!FileInventoriesEqual(resource.Files, actualFiles))
        {
            throw new InvalidDataException(
                $"External program resource {resourceId} file inventory does not match the Application files.");
        }

        var actualContentHash = ExternalProgramResourceFactory.ComputeContentSha256(resource with
        {
            Files = actualFiles,
            ContentSha256 = string.Empty
        });
        if (!string.Equals(actualContentHash, resource.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"External program resource {resourceId} content SHA-256 is invalid.");
        }

        return resource;
    }

    private static async ValueTask<ExternalProgramResource> BuildResourceAsync(
        string resourceDirectory,
        SaveExternalProgramResourceRequest request,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateResourceDirectoryLayout(resourceDirectory, requireDescriptor: false);
        var files = await InventoryFilesAsync(resourceDirectory, cancellationToken).ConfigureAwait(false);
        return ExternalProgramResourceFactory.Create(request, files, updatedAtUtc);
    }

    private static async ValueTask<IReadOnlyCollection<ExternalProgramResourceFile>> InventoryFilesAsync(
        string resourceDirectory,
        CancellationToken cancellationToken)
    {
        var filesDirectory = ResolveInside(resourceDirectory, ExternalProgramResourceContract.FilesDirectoryName);
        if (!Directory.Exists(filesDirectory))
        {
            return [];
        }

        RejectReparsePoint(filesDirectory);
        var files = new List<ExternalProgramResourceFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(filesDirectory);
        var directoryCount = 1;
        long totalBytes = 0;
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(entryPath);
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directoryCount++;
                    if (directoryCount > ExternalProgramResourceContract.MaximumFrozenDirectoryCount)
                    {
                        throw new InvalidDataException(
                            "External program directory inventory exceeds the supported limits.");
                    }

                    pendingDirectories.Push(entryPath);
                    continue;
                }

                var inventory = await HashBoundedFileAsync(
                        entryPath,
                        files.Count,
                        totalBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                totalBytes = inventory.TotalBytes;
                var relativePath = Path.GetRelativePath(resourceDirectory, entryPath).Replace('\\', '/');
                ExternalProgramResourceContract.CanonicalRelativePath(
                    relativePath,
                    nameof(relativePath),
                    ExternalProgramResourceContract.FilesDirectoryName);
                files.Add(new ExternalProgramResourceFile(
                    relativePath,
                    inventory.SizeBytes,
                    inventory.Sha256));
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static async ValueTask<BoundedFileInventory> HashBoundedFileAsync(
        string filePath,
        int currentFileCount,
        long accumulatedBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var declaredLength = stream.Length;
        var totalBytes = ExternalProgramResourceContract.AccumulateFrozenFileBytes(
            currentFileCount,
            accumulatedBytes,
            declaredLength);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long readBytes = 0;
        while (readBytes < declaredLength)
        {
            var requested = (int)Math.Min(buffer.Length, declaredLength - readBytes);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"External program file '{filePath}' became shorter while it was inventoried.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            readBytes += read;
        }

        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0
            || stream.Length != declaredLength)
        {
            throw new InvalidDataException(
                $"External program file '{filePath}' changed while it was inventoried.");
        }

        return new BoundedFileInventory(
            declaredLength,
            totalBytes,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async ValueTask WriteDescriptorAsync(
        string resourceDirectory,
        ExternalProgramResource resource,
        CancellationToken cancellationToken)
    {
        var document = FromResource(resource);
        var descriptorPath = ResolveInside(resourceDirectory, ExternalProgramResourceContract.DescriptorFileName);
        await using var stream = new FileStream(
            descriptorPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CopyUploadedFileAsync(
        string stagingDirectory,
        ExternalProgramFileUpload upload,
        CancellationToken cancellationToken)
    {
        var relativePath = ExternalProgramResourceContract.CanonicalRelativePath(
            upload.ResourceRelativePath,
            nameof(upload.ResourceRelativePath),
            ExternalProgramResourceContract.FilesDirectoryName);
        var destinationPath = ResolveInside(stagingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var originalPosition = upload.Content.CanSeek ? upload.Content.Position : 0;
        var buffer = new byte[64 * 1024];
        using var sha256 = SHA256.Create();
        long copied = 0;
        while (copied < upload.SizeBytes)
        {
            var requested = (int)Math.Min(buffer.Length, upload.SizeBytes - copied);
            var read = await upload.Content.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"External program upload '{relativePath}' ended before its declared length.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sha256.TransformBlock(buffer, 0, read, null, 0);
            copied += read;
        }

        if (await upload.Content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException(
                $"External program upload '{relativePath}' exceeds its declared length.");
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        sha256.TransformFinalBlock([], 0, 0);
        var actualSha256 = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
        if (copied != upload.SizeBytes || destination.Length != upload.SizeBytes)
        {
            throw new InvalidDataException(
                $"External program upload '{relativePath}' size does not match the declared length.");
        }

        if (!string.Equals(actualSha256, upload.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"External program upload '{relativePath}' SHA-256 does not match the declared hash.");
        }

        if (upload.Content.CanSeek)
        {
            upload.Content.Position = originalPosition;
        }
    }

    private static async ValueTask CopyFrozenFileSetAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyCollection<ExternalProgramResourceFile> expectedFiles,
        CancellationToken cancellationToken)
    {
        ValidateResourceDirectoryLayout(sourceDirectory, requireDescriptor: true);
        foreach (var file in expectedFiles.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ExternalProgramResourceContract.CanonicalRelativePath(
                file.RelativePath,
                nameof(file.RelativePath),
                ExternalProgramResourceContract.FilesDirectoryName);
            var sourcePath = ResolveInside(sourceDirectory, relativePath);
            var attributes = GetNonReparseAttributes(sourcePath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException(
                    $"External program frozen file '{relativePath}' must be a regular file.");
            }

            await using var content = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyUploadedFileAsync(
                    destinationDirectory,
                    new ExternalProgramFileUpload(
                        relativePath,
                        content,
                        file.SizeBytes,
                        file.Sha256),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ValidateResourceDirectoryLayout(sourceDirectory, requireDescriptor: true);
        var actualFiles = await InventoryFilesAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
        if (!FileInventoriesEqual(expectedFiles, actualFiles))
        {
            throw new InvalidDataException(
                "External program file inventory changed while its definition was being saved.");
        }
    }

    private static void CommitDirectory(
        string stagingDirectory,
        string finalDirectory,
        string backupDirectory)
    {
        AssertTransactionPathAbsent(backupDirectory);
        var movedExisting = false;
        try
        {
            if (Directory.Exists(finalDirectory))
            {
                Directory.Move(finalDirectory, backupDirectory);
                movedExisting = true;
            }

            Directory.Move(stagingDirectory, finalDirectory);
        }
        catch
        {
            if (!Directory.Exists(finalDirectory) && movedExisting && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, finalDirectory);
            }

            throw;
        }

        if (movedExisting)
        {
            TryDeleteDirectory(backupDirectory);
        }
    }

    private static void RecoverAllInterruptedCommits(string resourcesRoot)
    {
        foreach (var transactionDirectory in Directory.EnumerateDirectories(
                     resourcesRoot,
                     ".*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(transactionDirectory);
            if (!TryReadTransactionResourceId(name, out var resourceId))
            {
                throw new InvalidDataException(
                    $"External program recovery directory '{name}' is invalid.");
            }

            RecoverInterruptedCommit(resourcesRoot, resourceId);
        }
    }

    private static void RecoverInterruptedCommit(string resourcesRoot, string resourceId)
    {
        ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
        var finalDirectory = ResolveInside(resourcesRoot, resourceId);
        var stagingDirectory = ResolveInside(resourcesRoot, $".{resourceId}.staging");
        var backupDirectory = ResolveInside(resourcesRoot, $".{resourceId}.backup");
        var deletingDirectory = ResolveInside(resourcesRoot, $".{resourceId}.deleting");

        if (Directory.Exists(backupDirectory))
        {
            ValidateResourceDirectoryLayout(backupDirectory, requireDescriptor: true);
            if (Directory.Exists(finalDirectory))
            {
                ValidateResourceDirectoryLayout(finalDirectory, requireDescriptor: true);
                DeleteTransactionDirectoryOrThrow(backupDirectory);
            }
            else
            {
                Directory.Move(backupDirectory, finalDirectory);
            }
        }

        if (Directory.Exists(stagingDirectory))
        {
            ValidateResourceDirectoryLayout(stagingDirectory, requireDescriptor: false);
            DeleteTransactionDirectoryOrThrow(stagingDirectory);
        }

        if (Directory.Exists(deletingDirectory))
        {
            ValidateResourceDirectoryLayout(deletingDirectory, requireDescriptor: true);
            DeleteTransactionDirectoryOrThrow(deletingDirectory);
        }

        AssertTransactionPathAbsent(stagingDirectory);
        AssertTransactionPathAbsent(deletingDirectory);
        AssertTransactionPathAbsent(backupDirectory);
    }

    private static void DeleteTransactionDirectoryOrThrow(string path)
    {
        TryDeleteDirectory(path);
        AssertTransactionPathAbsent(path);
    }

    private static void AssertTransactionPathAbsent(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException(
                $"External program transaction path '{path}' could not be recovered safely.");
        }
    }

    private static bool FileInventoriesEqual(
        IReadOnlyCollection<ExternalProgramResourceFile> left,
        IReadOnlyCollection<ExternalProgramResourceFile> right)
    {
        var orderedLeft = left.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        var orderedRight = right.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        return orderedLeft.Length == orderedRight.Length
            && orderedLeft.Zip(orderedRight).All(pair => pair.First == pair.Second);
    }

    private static string GetResourcesRoot(ProjectApplicationWorkspaceScope scope) =>
        ResolveInside(scope.ApplicationRootPath, ExternalProgramResourceContract.ResourceDirectoryName);

    private static string GetResourceDirectory(ProjectApplicationWorkspaceScope scope, string resourceId) =>
        ResolveInside(GetResourcesRoot(scope), ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId)));

    private static string GetDescriptorPath(ProjectApplicationWorkspaceScope scope, string resourceId) =>
        ResolveInside(GetResourceDirectory(scope, resourceId), ExternalProgramResourceContract.DescriptorFileName);

    private static string ResolveInside(string rootPath, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException($"External program resource path '{relativePath}' escapes its root.");
        }

        return fullPath;
    }

    private static HashSet<string> ValidateResourcesRoot(string resourcesRoot)
    {
        RejectReparsePoint(resourcesRoot);
        var resourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(resourcesRoot))
        {
            var attributes = GetNonReparseAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    $"External programs root entry '{Path.GetFileName(entry)}' must be a resource directory.");
            }

            var name = Path.GetFileName(entry);
            if (!entryNames.Add(name))
            {
                throw new InvalidDataException(
                    $"External programs root contains a portable path collision for '{name}'.");
            }

            if (TryReadTransactionResourceId(name, out _))
            {
                continue;
            }

            var resourceId = ExternalProgramResourceContract.PortableId(name, nameof(resourcesRoot));
            if (!resourceIds.Add(resourceId))
            {
                throw new InvalidDataException(
                    $"External programs root contains a case-insensitive resource identity collision for '{resourceId}'.");
            }
        }

        return resourceIds;
    }

    private static bool TryReadTransactionResourceId(string name, out string resourceId)
    {
        resourceId = string.Empty;
        if (!name.StartsWith('.'))
        {
            return false;
        }

        var suffix = name.EndsWith(".backup", StringComparison.Ordinal)
            ? ".backup"
            : name.EndsWith(".staging", StringComparison.Ordinal)
                ? ".staging"
                : name.EndsWith(".deleting", StringComparison.Ordinal)
                    ? ".deleting"
                    : null;
        if (suffix is null || name.Length <= 1 + suffix.Length)
        {
            throw new InvalidDataException(
                $"External program recovery directory '{name}' is invalid.");
        }

        resourceId = ExternalProgramResourceContract.PortableId(
            name[1..^suffix.Length],
            nameof(name));
        return true;
    }

    private static void ValidateResourceDirectoryLayout(
        string resourceDirectory,
        bool requireDescriptor)
    {
        RejectReparsePoint(resourceDirectory);
        var descriptorSeen = false;
        var filesSeen = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(resourceDirectory))
        {
            var attributes = GetNonReparseAttributes(entry);
            var name = Path.GetFileName(entry);
            if (string.Equals(name, ExternalProgramResourceContract.DescriptorFileName, StringComparison.Ordinal))
            {
                if ((attributes & FileAttributes.Directory) != 0 || descriptorSeen)
                {
                    throw new InvalidDataException(
                        "External program resource descriptor must be one regular file.");
                }

                descriptorSeen = true;
                continue;
            }

            if (string.Equals(name, ExternalProgramResourceContract.FilesDirectoryName, StringComparison.Ordinal))
            {
                if ((attributes & FileAttributes.Directory) == 0 || filesSeen)
                {
                    throw new InvalidDataException(
                        "External program resource files entry must be one directory.");
                }

                filesSeen = true;
                ValidateFrozenFilesTree(resourceDirectory, entry);
                continue;
            }

            throw new InvalidDataException(
                $"External program resource root entry '{name}' is not allowed.");
        }

        if (requireDescriptor && !descriptorSeen)
        {
            throw new InvalidDataException("External program resource descriptor is missing.");
        }
    }

    private static void ValidateFrozenFilesTree(string resourceDirectory, string filesDirectory)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(filesDirectory);
        var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryCount = 1;
        var fileCount = 0;
        long totalBytes = 0;
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = GetNonReparseAttributes(entry);
                var relativePath = Path.GetRelativePath(resourceDirectory, entry).Replace('\\', '/');
                var canonicalPath = ExternalProgramResourceContract.CanonicalRelativePath(
                    relativePath,
                    nameof(relativePath),
                    ExternalProgramResourceContract.FilesDirectoryName);
                if (!canonicalPaths.Add(canonicalPath))
                {
                    throw new InvalidDataException(
                        $"External program resource path '{canonicalPath}' conflicts by portable case.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directoryCount++;
                    if (directoryCount > ExternalProgramResourceContract.MaximumFrozenDirectoryCount)
                    {
                        throw new InvalidDataException(
                            "External program directory inventory exceeds the supported limits.");
                    }

                    pendingDirectories.Push(entry);
                    continue;
                }

                var length = new FileInfo(entry).Length;
                totalBytes = ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                    fileCount,
                    totalBytes,
                    length);
                fileCount++;
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            _ = GetNonReparseAttributes(path);
        }
    }

    private static FileAttributes GetNonReparseAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"External program resource path '{path}' cannot be a symbolic link or reparse point.");
        }

        return attributes;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ValidateResourceDirectoryLayout(path, requireDescriptor: false);
                DeleteDirectoryWithoutFollowingReparsePoints(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string root)
    {
        var pending = new Stack<(string Path, bool ChildrenVisited)>();
        pending.Push((root, false));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            _ = GetNonReparseAttributes(current.Path);
            if (current.ChildrenVisited)
            {
                Directory.Delete(current.Path, recursive: false);
                continue;
            }

            pending.Push((current.Path, true));
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                var attributes = GetNonReparseAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, false));
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("External program resource timestamp must use UTC offset zero.");
        }
    }

    private static void ValidateUploads(IReadOnlyCollection<ExternalProgramFileUpload> uploads)
    {
        if (uploads.Count is 0 or > ExternalProgramResourceContract.MaximumFrozenFileCount)
        {
            throw new ArgumentException("External program upload file count exceeds the repository limit.");
        }

        long totalBytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var upload in uploads)
        {
            if (upload is null
                || upload.Content is null
                || !upload.Content.CanRead
                || upload.SizeBytes < 0
                || upload.SizeBytes > ExternalProgramResourceContract.MaximumFrozenFileBytes
                || !ExternalProgramResourceContract.IsSha256(upload.ExpectedSha256))
            {
                throw new ArgumentException("External program upload metadata is invalid.");
            }

            try
            {
                totalBytes = ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                    paths.Count,
                    totalBytes,
                    upload.SizeBytes);
            }
            catch (InvalidDataException exception)
            {
                throw new ArgumentException(
                    "External program upload total size exceeds the repository limit.",
                    nameof(uploads),
                    exception);
            }
            var path = ExternalProgramResourceContract.CanonicalRelativePath(
                upload.ResourceRelativePath,
                nameof(upload.ResourceRelativePath),
                ExternalProgramResourceContract.FilesDirectoryName);
            if (!paths.Add(path))
            {
                throw new ArgumentException($"External program upload target '{path}' is duplicated.");
            }
        }
    }

    private static ExternalProgramResourceDocument FromResource(ExternalProgramResource resource) => new(
        ExternalProgramResourceContract.Schema,
        ExternalProgramResourceContract.ResourceKind,
        resource.ResourceId,
        resource.DisplayName,
        resource.CapabilityId,
        resource.CommandName,
        resource.LaunchKind,
        resource.EntryPoint,
        resource.ProviderKind,
        resource.ProviderKey,
        resource.ArgumentTemplates.ToArray(),
        resource.InputMappings.ToArray(),
        resource.ResultMappings.ToArray(),
        resource.OutcomeMapping,
        resource.PermissionProfile,
        resource.ExecutionLimits,
        resource.Files.ToArray(),
        resource.ContentSha256,
        resource.UpdatedAtUtc);

    private static ExternalProgramResource ToResource(ExternalProgramResourceDocument document) => new(
        document.ResourceId,
        document.DisplayName,
        document.CapabilityId,
        document.CommandName,
        document.LaunchKind,
        document.EntryPoint,
        document.ProviderKind,
        document.ProviderKey,
        document.ArgumentTemplates,
        document.InputMappings,
        document.ResultMappings,
        document.OutcomeMapping,
        document.PermissionProfile,
        document.ExecutionLimits,
        document.Files,
        document.ContentSha256,
        document.UpdatedAtUtc);

    private sealed record BoundedFileInventory(long SizeBytes, long TotalBytes, string Sha256);

    private sealed record ExternalProgramResourceDocument(
        string Schema,
        string ResourceKind,
        string ResourceId,
        string DisplayName,
        string CapabilityId,
        string CommandName,
        ExternalProgramLaunchKind LaunchKind,
        string? EntryPoint,
        string? ProviderKind,
        string? ProviderKey,
        string[] ArgumentTemplates,
        ExternalProgramInputMapping[] InputMappings,
        ExternalProgramResultMapping[] ResultMappings,
        ExternalProgramOutcomeMapping OutcomeMapping,
        ExternalProgramPermissionProfile PermissionProfile,
        ExternalProgramExecutionLimits ExecutionLimits,
        ExternalProgramResourceFile[] Files,
        string ContentSha256,
        DateTimeOffset UpdatedAtUtc);

    private sealed class ApplicationLockRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, LockEntry> _entries;

        public ApplicationLockRegistry(IEqualityComparer<string> comparer)
        {
            _entries = new Dictionary<string, LockEntry>(comparer);
        }

        public async ValueTask<LockLease> AcquireAsync(
            string applicationRootPath,
            CancellationToken cancellationToken)
        {
            var key = Path.GetFullPath(applicationRootPath);
            LockEntry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new LockEntry();
                    _entries.Add(key, entry);
                }

                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new LockLease(this, key, entry);
            }
            catch
            {
                ReleaseReference(key, entry, releaseSemaphore: false);
                throw;
            }
        }

        private void Release(string key, LockEntry entry) =>
            ReleaseReference(key, entry, releaseSemaphore: true);

        private void ReleaseReference(string key, LockEntry entry, bool releaseSemaphore)
        {
            if (releaseSemaphore)
            {
                entry.Semaphore.Release();
            }

            lock (_gate)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        internal sealed class LockEntry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int ReferenceCount { get; set; }
        }

        public sealed class LockLease : IAsyncDisposable
        {
            private readonly ApplicationLockRegistry _owner;
            private readonly string _key;
            private LockEntry? _entry;

            internal LockLease(ApplicationLockRegistry owner, string key, LockEntry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public ValueTask DisposeAsync()
            {
                var entry = Interlocked.Exchange(ref _entry, null);
                if (entry is not null)
                {
                    _owner.Release(_key, entry);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
