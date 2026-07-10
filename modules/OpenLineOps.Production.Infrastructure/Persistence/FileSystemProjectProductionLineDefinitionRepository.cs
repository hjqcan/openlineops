using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Production.Application.Persistence;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Infrastructure.Persistence;

public sealed class FileSystemProjectProductionLineDefinitionRepository
    : IProjectProductionLineDefinitionRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ProductionLineResourcePath.GetLinePath(scope, definition.Id.Value);
        EnsureResourcePathSafe(scope, path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            ProductionLineResourceMapper.FromAggregate(scope, definition),
            JsonOptions);
        await SaveAtomicallyAsync(
                ProductionLineResourcePath.GetLinesDirectory(scope),
                definition.Id.Value,
                path,
                bytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ProductionLineDefinition?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var path = ProductionLineResourcePath.GetLinePath(scope, definitionId.Value);
        EnsureResourcePathSafe(scope, path);
        var document = await LoadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (!string.Equals(document.LineDefinitionId, definitionId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Production line resource '{path}' contains id {document.LineDefinitionId}, not {definitionId}.");
        }

        return ProductionLineResourceMapper.ToAggregate(scope, document);
    }

    public async ValueTask<IReadOnlyCollection<ProductionLineDefinition>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var linesDirectory = ProductionLineResourcePath.GetLinesDirectory(scope);
        if (!Directory.Exists(linesDirectory))
        {
            return [];
        }

        EnsureResourcePathSafe(scope, linesDirectory);

        var definitions = new List<ProductionLineDefinition>();
        foreach (var path in Directory.EnumerateFiles(linesDirectory, "line.json", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureResourcePathSafe(scope, path);
            var relativePath = Path.GetRelativePath(linesDirectory, path);
            if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length != 2)
            {
                continue;
            }

            var document = await LoadDocumentAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Production line resource '{path}' is empty.");
            ProductionLineDefinitionId resourceId;
            try
            {
                resourceId = new ProductionLineDefinitionId(document.LineDefinitionId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Production line resource '{path}' contains an invalid id.",
                    exception);
            }

            var expectedPath = ProductionLineResourcePath.GetLinePath(scope, resourceId.Value);
            if (!string.Equals(
                    Path.GetFullPath(expectedPath),
                    Path.GetFullPath(path),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Production line resource '{path}' is not in the directory for id {document.LineDefinitionId}.");
            }

            definitions.Add(ProductionLineResourceMapper.ToAggregate(scope, document));
        }

        if (definitions.Select(definition => definition.Id.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count)
        {
            throw new InvalidDataException("Production line resource ids must be unique ignoring case.");
        }

        return definitions;
    }

    private static async ValueTask<ProductionLineResourceDocument?> LoadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<ProductionLineResourceDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Production line resource '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Production line resource '{path}' contains invalid JSON: {exception.Message}",
                exception);
        }
    }

    private static async ValueTask SaveAtomicallyAsync(
        string linesDirectory,
        string lineDefinitionId,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Production line resource '{path}' has no parent directory.");
        Directory.CreateDirectory(linesDirectory);
        var writeLock = WriteLocks.GetOrAdd(linesDirectory, static _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var conflictingDirectory = Directory.EnumerateDirectories(linesDirectory)
                .Select(Path.GetFileName)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate, lineDefinitionId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidate, lineDefinitionId, StringComparison.Ordinal));
            if (conflictingDirectory is not null)
            {
                throw new InvalidDataException(
                    $"Production line id {lineDefinitionId} conflicts with existing id {conflictingDirectory} ignoring case.");
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(path)
                && (await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))
                .AsSpan().SequenceEqual(bytes))
            {
                return;
            }

            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            writeLock.Release();
        }
    }

    private static void EnsureResourcePathSafe(
        ProjectApplicationWorkspaceScope scope,
        string path)
    {
        var root = Path.GetFullPath(scope.ApplicationRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectReparsePoint(root);
        var relativePath = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Production line resource path '{path}' escapes the portable Application.");
        }

        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Production line resource path '{path}' cannot be a symbolic link or reparse point.");
        }
    }
}
