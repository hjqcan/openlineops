using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class FileSystemProjectProcessBlocklyBlockDefinitionRepository :
    IProjectProcessBlocklyBlockDefinitionRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BlockLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var customBlocksDirectory = ProjectProcessResourcePath.GetCustomBlocksDirectory(scope);
        if (!Directory.Exists(customBlocksDirectory))
        {
            return [];
        }

        var latest = new List<ProcessBlocklyBlockDefinitionRecord>();
        foreach (var blockDirectory in Directory
                     .EnumerateDirectories(customBlocksDirectory, "block-*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versions = await ListVersionsFromDirectoryAsync(scope, blockDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (versions.Count > 0)
            {
                latest.Add(versions.First());
            }
        }

        return latest
            .OrderBy(record => record.BlockType, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        CancellationToken cancellationToken = default)
    {
        var versions = await ListVersionsAsync(scope, blockType, cancellationToken).ConfigureAwait(false);
        return versions.FirstOrDefault();
    }

    public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(blockType))
        {
            throw new ArgumentException("Blockly block type cannot be empty.", nameof(blockType));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var blockDirectory = ProjectProcessResourcePath.GetCustomBlockDirectory(scope, blockType.Trim());

        return ListVersionsFromDirectoryAsync(
            scope,
            blockDirectory,
            cancellationToken,
            expectedBlockType: blockType.Trim());
    }

    public async ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string executionMode,
        string runtimeActionContractSchemaVersion,
        string runtimeActionContractJson,
        string runtimeActionContractSha256,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(blockType))
        {
            throw new ArgumentException("Blockly block type cannot be empty.", nameof(blockType));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedBlockType = blockType.Trim();
        var blockDirectory = ProjectProcessResourcePath.GetCustomBlockDirectory(scope, normalizedBlockType);
        var blockLock = BlockLocks.GetOrAdd(blockDirectory, static _ => new SemaphoreSlim(1, 1));
        await blockLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var versions = await ListVersionsFromDirectoryAsync(
                    scope,
                    blockDirectory,
                    cancellationToken,
                    normalizedBlockType)
                .ConfigureAwait(false);
            var latest = versions.FirstOrDefault();
            var version = latest?.Version + 1 ?? 1;
            var createdAtUtc = latest?.CreatedAtUtc ?? recordedAtUtc;
            var document = new ProjectProcessBlocklyBlockVersionDocument(
                ProjectProcessBlocklyBlockVersionDocument.CurrentSchema,
                ProjectProcessBlocklyBlockVersionDocument.CurrentSchemaVersion,
                scope.ApplicationId,
                normalizedBlockType,
                version,
                category,
                displayName,
                blocklyJson,
                executionMode,
                runtimeActionContractSchemaVersion,
                runtimeActionContractJson,
                runtimeActionContractSha256,
                createdAtUtc,
                recordedAtUtc);
            var versionPath = ProjectProcessResourcePath.GetCustomBlockVersionPath(
                scope,
                normalizedBlockType,
                version);

            await ProjectProcessResourceFileStore
                .SaveNewJsonAsync(versionPath, document, cancellationToken)
                .ConfigureAwait(false);

            return ToRecord(document);
        }
        finally
        {
            blockLock.Release();
        }
    }

    private static async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsFromDirectoryAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockDirectory,
        CancellationToken cancellationToken,
        string? expectedBlockType = null)
    {
        var versionsDirectory = Path.Combine(blockDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return [];
        }

        var records = new List<ProcessBlocklyBlockDefinitionRecord>();
        foreach (var versionPath in Directory
                     .EnumerateFiles(versionsDirectory, "version-*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedVersion = ParseVersionFromPath(versionPath);
            var document = await ProjectProcessResourceFileStore
                .LoadJsonAsync<ProjectProcessBlocklyBlockVersionDocument>(versionPath, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Project Blockly block resource '{versionPath}' is empty.");

            ValidateDocument(scope, versionPath, document, expectedBlockType, expectedVersion);
            records.Add(ToRecord(document));
        }

        return records
            .OrderByDescending(record => record.Version)
            .ToArray();
    }

    private static void ValidateDocument(
        ProjectApplicationWorkspaceScope scope,
        string versionPath,
        ProjectProcessBlocklyBlockVersionDocument document,
        string? expectedBlockType,
        int expectedVersion)
    {
        if (!string.Equals(
                document.Schema,
                ProjectProcessBlocklyBlockVersionDocument.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw InvalidResource(versionPath, $"schema '{document.Schema}' is not supported");
        }

        if (document.SchemaVersion != ProjectProcessBlocklyBlockVersionDocument.CurrentSchemaVersion)
        {
            throw InvalidResource(versionPath, $"schema version {document.SchemaVersion} is not supported");
        }

        if (!string.Equals(document.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            throw InvalidResource(
                versionPath,
                $"application is {document.ApplicationId}, expected {scope.ApplicationId}");
        }

        if (string.IsNullOrWhiteSpace(document.BlockType))
        {
            throw InvalidResource(versionPath, "block type is empty");
        }

        if (expectedBlockType is not null
            && !string.Equals(document.BlockType, expectedBlockType, StringComparison.Ordinal))
        {
            throw InvalidResource(
                versionPath,
                $"block type is {document.BlockType}, expected {expectedBlockType}");
        }

        var expectedBlockDirectory = ProjectProcessResourcePath.GetCustomBlockDirectory(scope, document.BlockType);
        var actualBlockDirectory = Directory.GetParent(Path.GetDirectoryName(versionPath)!)?.FullName;
        if (!string.Equals(
                Path.GetFullPath(actualBlockDirectory ?? string.Empty),
                Path.GetFullPath(expectedBlockDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResource(versionPath, "block identity does not match its directory");
        }

        if (document.Version != expectedVersion || document.Version <= 0)
        {
            throw InvalidResource(
                versionPath,
                $"version is {document.Version}, expected {expectedVersion}");
        }

        if (string.IsNullOrWhiteSpace(document.Category)
            || string.IsNullOrWhiteSpace(document.DisplayName)
            || string.IsNullOrWhiteSpace(document.BlocklyJson)
            || !string.Equals(
                document.ExecutionMode,
                ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.RuntimeActionContractSchemaVersion)
            || string.IsNullOrWhiteSpace(document.RuntimeActionContractJson)
            || string.IsNullOrWhiteSpace(document.RuntimeActionContractSha256))
        {
            throw InvalidResource(versionPath, "required block definition content is empty");
        }

        if (document.CreatedAtUtc > document.UpdatedAtUtc)
        {
            throw InvalidResource(versionPath, "created timestamp is after updated timestamp");
        }

        ValidateBlocklyJson(versionPath, document.BlockType, document.BlocklyJson);
        ValidateRuntimeActionContract(versionPath, document);
    }

    private static void ValidateRuntimeActionContract(
        string versionPath,
        ProjectProcessBlocklyBlockVersionDocument document)
    {
        var serializer = new RuntimeActionContractCanonicalSerializer();
        var contract = serializer.Deserialize(document.RuntimeActionContractJson);
        if (contract.IsFailure)
        {
            throw InvalidResource(versionPath, $"contains invalid Runtime Action Contract: {contract.Error.Message}");
        }

        var artifact = serializer.Serialize(contract.Value);
        if (artifact.IsFailure
            || !string.Equals(artifact.Value.SchemaVersion, document.RuntimeActionContractSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(artifact.Value.Sha256, document.RuntimeActionContractSha256, StringComparison.Ordinal))
        {
            throw InvalidResource(versionPath, "Runtime Action Contract metadata does not match its canonical content");
        }
    }

    private static void ValidateBlocklyJson(string versionPath, string blockType, string blocklyJson)
    {
        try
        {
            using var json = JsonDocument.Parse(blocklyJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !string.Equals(typeElement.GetString(), blockType, StringComparison.Ordinal))
            {
                throw InvalidResource(versionPath, "Blockly JSON type does not match the block identity");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Project Blockly block resource '{versionPath}' contains invalid Blockly JSON.",
                exception);
        }
    }

    private static int ParseVersionFromPath(string versionPath)
    {
        const string prefix = "version-";
        var name = Path.GetFileNameWithoutExtension(versionPath);
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length != prefix.Length + 6
            || !int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version <= 0)
        {
            throw new InvalidDataException(
                $"Project Blockly block version file '{versionPath}' does not use version-000001.json naming.");
        }

        return version;
    }

    private static ProcessBlocklyBlockDefinitionRecord ToRecord(
        ProjectProcessBlocklyBlockVersionDocument document)
    {
        return new ProcessBlocklyBlockDefinitionRecord(
            document.BlockType,
            document.Category,
            document.DisplayName,
            document.BlocklyJson,
            document.ExecutionMode,
            document.RuntimeActionContractSchemaVersion,
            document.RuntimeActionContractJson,
            document.RuntimeActionContractSha256,
            document.Version,
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
    }

    private static InvalidDataException InvalidResource(string path, string message)
    {
        return new InvalidDataException($"Project Blockly block resource '{path}' {message}.");
    }
}
