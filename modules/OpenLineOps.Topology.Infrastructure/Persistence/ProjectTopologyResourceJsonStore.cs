using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal static class ProjectTopologyResourceJsonStore
{
    private static readonly ProjectWorkspaceWriteLockPool WriteLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async ValueTask SaveAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Resource path '{path}' has no parent directory.");
        ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
            directory,
            "Project topology resource directory");

        using var writeLock = await WriteLocks.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                directory,
                "Project topology resource directory");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project topology resource");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                temporaryPath,
                "Temporary project topology resource");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                temporaryPath,
                "Temporary project topology resource");
            ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                directory,
                "Project topology resource directory");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project topology resource");
            File.Move(temporaryPath, path, overwrite: true);
            if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    path,
                    "Project topology resource"))
            {
                throw new InvalidDataException(
                    $"Project topology resource '{path}' was not committed as an ordinary file.");
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
                    "Temporary project topology resource"))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        ProjectWorkspaceFileOperation.ThrowFailures(
            "Project topology resource commit and temporary-file cleanup both failed.",
            operationFailure,
            cleanupFailure);
    }

    public static async ValueTask<T?> LoadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project topology resource"))
        {
            return default;
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

            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Project resource '{path}' is invalid JSON.", exception);
        }
    }
}
