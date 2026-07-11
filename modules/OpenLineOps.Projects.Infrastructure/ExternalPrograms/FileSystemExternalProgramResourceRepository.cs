using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

        RejectReparsePoint(root);
        var resources = new List<ExternalProgramResource>();
        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Where(path => !Path.GetFileName(path).StartsWith('.'))
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            var resourceId = ExternalProgramResourceContract.PortableId(
                Path.GetFileName(directory),
                nameof(directory));
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
        var descriptorPath = GetDescriptorPath(scope, resourceId);
        return File.Exists(descriptorPath)
            ? await ReadAsync(scope, resourceId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<ExternalProgramResource> SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> uploads,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(uploads);
        ExternalProgramResourceValidator.ValidateDefinition(request);
        ValidateUploads(uploads);
        EnsureUtc(updatedAtUtc);
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        return await SaveLockedAsync(scope, request, uploads, updatedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<ExternalProgramResource> SaveLockedAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> uploads,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var root = GetResourcesRoot(scope);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        var finalDirectory = ResolveInside(root, request.ResourceId);
        var stagingDirectory = ResolveInside(root, $".{request.ResourceId}.staging.{Guid.NewGuid():N}");
        var backupDirectory = ResolveInside(root, $".{request.ResourceId}.backup.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            if (Directory.Exists(finalDirectory))
            {
                CopyTree(finalDirectory, stagingDirectory, cancellationToken);
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
            TryDeleteDirectory(backupDirectory);
        }
    }

    public async ValueTask<ExternalProgramResource> ImportFileAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        ExternalProgramFileUpload upload,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(upload);
        ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
        ValidateUploads([upload]);
        EnsureUtc(updatedAtUtc);
        await using var lockHandle = await ApplicationLocks.AcquireAsync(
            scope.ApplicationRootPath,
            cancellationToken).ConfigureAwait(false);
        var descriptorPath = GetDescriptorPath(scope, resourceId);
        var current = File.Exists(descriptorPath)
            ? await ReadAsync(scope, resourceId, cancellationToken).ConfigureAwait(false)
            : throw new InvalidDataException($"External program resource {resourceId} was not found.");
        var definition = new SaveExternalProgramResourceRequest(
            current.ResourceId,
            current.DisplayName,
            current.CapabilityId,
            current.CommandName,
            current.LaunchKind,
            current.EntryPoint,
            current.ProviderKind,
            current.ProviderKey,
            current.ArgumentTemplates,
            current.InputMappings,
            current.ResultMappings,
            current.OutcomeMapping,
            current.PermissionProfile,
            current.ExecutionLimits);
        return await SaveLockedAsync(scope, definition, [upload], updatedAtUtc, cancellationToken)
            .ConfigureAwait(false);
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
        var directory = GetResourceDirectory(scope, resourceId);
        var tombstone = ResolveInside(
            GetResourcesRoot(scope),
            $".{resourceId}.deleting.{Guid.NewGuid():N}");
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
        {
            return;
        }

        RejectTreeReparsePoints(directory);
        Directory.Move(directory, tombstone);
        TryDeleteDirectory(tombstone);
    }

    private static async ValueTask<ExternalProgramResource> ReadAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var directory = GetResourceDirectory(scope, resourceId);
        RejectReparsePoint(directory);
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

        var actualContentHash = ComputeContentSha256(resource with
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
        var files = await InventoryFilesAsync(resourceDirectory, cancellationToken).ConfigureAwait(false);
        if (request.EntryPoint is not null
            && files.All(file => !string.Equals(file.RelativePath, request.EntryPoint, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"External program entry point '{request.EntryPoint}' was not imported into the resource.");
        }

        var resource = new ExternalProgramResource(
            request.ResourceId,
            request.DisplayName,
            request.CapabilityId,
            request.CommandName,
            request.LaunchKind,
            request.EntryPoint,
            request.ProviderKind,
            request.ProviderKey,
            request.ArgumentTemplates.ToArray(),
            request.InputMappings.ToArray(),
            request.ResultMappings.ToArray(),
            request.OutcomeMapping,
            request.PermissionProfile with
            {
                AllowedEnvironmentVariables = request.PermissionProfile.AllowedEnvironmentVariables
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            },
            request.ExecutionLimits,
            files,
            string.Empty,
            updatedAtUtc);
        resource = resource with { ContentSha256 = ComputeContentSha256(resource) };
        ExternalProgramResourceValidator.ValidateFrozenResource(resource);
        return resource;
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

        RejectTreeReparsePoints(filesDirectory);
        var files = new List<ExternalProgramResourceFile>();
        foreach (var filePath in Directory.EnumerateFiles(filesDirectory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(filePath);
            var info = new FileInfo(filePath);
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var relativePath = Path.GetRelativePath(resourceDirectory, filePath).Replace('\\', '/');
            ExternalProgramResourceContract.CanonicalRelativePath(
                relativePath,
                nameof(relativePath),
                ExternalProgramResourceContract.FilesDirectoryName);
            files.Add(new ExternalProgramResourceFile(
                relativePath,
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }

        return files;
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

    private static string ComputeContentSha256(ExternalProgramResource resource)
    {
        var canonical = new StringBuilder();
        Append(canonical, resource.ResourceId);
        Append(canonical, resource.DisplayName);
        Append(canonical, resource.CapabilityId);
        Append(canonical, resource.CommandName);
        Append(canonical, resource.LaunchKind.ToString());
        Append(canonical, resource.EntryPoint ?? string.Empty);
        Append(canonical, resource.ProviderKind ?? string.Empty);
        Append(canonical, resource.ProviderKey ?? string.Empty);
        foreach (var item in resource.ArgumentTemplates)
        {
            Append(canonical, item);
        }

        foreach (var item in resource.InputMappings.OrderBy(item => item.Target, StringComparer.Ordinal))
        {
            Append(canonical, item.Source);
            Append(canonical, item.Target);
        }

        foreach (var item in resource.ResultMappings.OrderBy(item => item.TargetKey, StringComparer.Ordinal))
        {
            Append(canonical, item.SourcePath);
            Append(canonical, item.TargetKey);
            Append(canonical, item.ValueKind.ToString());
        }

        Append(canonical, resource.OutcomeMapping.SourcePath);
        Append(canonical, resource.OutcomeMapping.PassedToken);
        Append(canonical, resource.OutcomeMapping.FailedToken);
        Append(canonical, resource.OutcomeMapping.AbortedToken);
        Append(canonical, resource.PermissionProfile.ProfileName);
        Append(canonical, resource.PermissionProfile.NetworkAccessAllowed.ToString(CultureInfo.InvariantCulture));
        foreach (var name in resource.PermissionProfile.AllowedEnvironmentVariables.Order(StringComparer.Ordinal))
        {
            Append(canonical, name);
        }

        Append(canonical, resource.ExecutionLimits.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumProcessCount.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumWorkingSetBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumCpuTimeMilliseconds.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumStandardOutputBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumStandardErrorBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumArtifactCount.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumTotalArtifactBytes.ToString(CultureInfo.InvariantCulture));
        foreach (var file in resource.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            Append(canonical, file.RelativePath);
            Append(canonical, file.SizeBytes.ToString(CultureInfo.InvariantCulture));
            Append(canonical, file.Sha256);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');

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

    private static void CopyTree(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        RejectTreeReparsePoints(sourceDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(ResolveInside(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = ResolveInside(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }

        var existingDescriptor = ResolveInside(
            destinationDirectory,
            ExternalProgramResourceContract.DescriptorFileName);
        if (File.Exists(existingDescriptor))
        {
            File.Delete(existingDescriptor);
        }
    }

    private static void CommitDirectory(
        string stagingDirectory,
        string finalDirectory,
        string backupDirectory)
    {
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

    private static void RejectTreeReparsePoints(string root)
    {
        RejectReparsePoint(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(entry);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"External program resource path '{path}' cannot be a symbolic link or reparse point.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
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
        const int maximumFileCount = 256;
        const long maximumFileBytes = 512L * 1024 * 1024;
        const long maximumTotalBytes = 2L * 1024 * 1024 * 1024;
        if (uploads.Count > maximumFileCount)
        {
            throw new ArgumentException("External program upload file count exceeds the repository limit.");
        }

        long totalBytes = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var upload in uploads)
        {
            if (upload is null
                || upload.Content is null
                || !upload.Content.CanRead
                || upload.SizeBytes < 0
                || upload.SizeBytes > maximumFileBytes
                || !ExternalProgramResourceContract.IsSha256(upload.ExpectedSha256))
            {
                throw new ArgumentException("External program upload metadata is invalid.");
            }

            if (totalBytes > maximumTotalBytes - upload.SizeBytes)
            {
                throw new ArgumentException("External program upload total size exceeds the repository limit.");
            }

            totalBytes += upload.SizeBytes;
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
