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
    private static readonly ProjectWorkspaceWriteLockPool WriteLocks = new();

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
        ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
            path,
            "Production line resource");
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
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                linesDirectory,
                "Production lines directory"))
        {
            return [];
        }

        var definitions = new List<ProductionLineDefinition>();
        foreach (var lineDirectory in Directory.EnumerateDirectories(linesDirectory)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                lineDirectory,
                "Production line definition directory");
            var path = Path.Combine(lineDirectory, "line.json");
            if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    path,
                    "Production line resource"))
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
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Production line resource"))
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
        ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
            linesDirectory,
            "Production lines directory");
        using var writeLock = await WriteLocks
            .AcquireAsync(linesDirectory, cancellationToken)
            .ConfigureAwait(false);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                linesDirectory,
                "Production lines directory");
            var lineDirectories = Directory.EnumerateDirectories(linesDirectory).ToArray();
            foreach (var candidateDirectory in lineDirectories)
            {
                ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                    candidateDirectory,
                    "Production line definition directory");
            }

            var conflictingDirectory = lineDirectories
                .Select(Path.GetFileName)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate, lineDefinitionId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidate, lineDefinitionId, StringComparison.Ordinal));
            if (conflictingDirectory is not null)
            {
                throw new InvalidDataException(
                    $"Production line id {lineDefinitionId} conflicts with existing id {conflictingDirectory} ignoring case.");
            }

            ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                directory,
                "Production line definition directory");
            var targetExists = ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Production line resource");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                temporaryPath,
                "Temporary production line resource");
            var writeRequired = !targetExists
                || !(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))
                .AsSpan().SequenceEqual(bytes);
            if (writeRequired)
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    useAsync: true))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    temporaryPath,
                    "Temporary production line resource");
                ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                    directory,
                    "Production line definition directory");
                ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    path,
                    "Production line resource");
                File.Move(temporaryPath, path, overwrite: true);
                if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                        path,
                        "Production line resource"))
                {
                    throw new InvalidDataException(
                        $"Production line resource '{path}' was not committed as an ordinary file.");
                }
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            if (ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    temporaryPath,
                    "Temporary production line resource"))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        ProjectWorkspaceFileOperation.ThrowFailures(
            "Production line resource commit and temporary-file cleanup both failed.",
            operationFailure,
            cleanupFailure);
    }
}
