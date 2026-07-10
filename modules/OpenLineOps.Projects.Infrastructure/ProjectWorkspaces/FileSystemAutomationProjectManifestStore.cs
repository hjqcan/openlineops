using System.Text.Json;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

public sealed class FileSystemAutomationProjectManifestStore : IAutomationProjectManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string GetManifestPath(string projectPath)
    {
        return Path.Combine(NormalizeProjectPath(projectPath), AutomationProjectManifest.FileName);
    }

    public async ValueTask SaveAsync(
        AutomationProjectManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var projectPath = NormalizeProjectPath(manifest.ProjectPath);
        Directory.CreateDirectory(projectPath);

        var manifestPath = GetManifestPath(projectPath);
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, Normalize(manifest, projectPath), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async ValueTask<AutomationProjectManifest?> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manifestPath = GetManifestPath(projectPath);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);

        var manifest = await JsonSerializer
            .DeserializeAsync<AutomationProjectManifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return manifest is null
            ? throw new InvalidDataException($"Project manifest '{manifestPath}' is empty or invalid.")
            : Normalize(manifest, NormalizeProjectPath(projectPath));
    }

    private static AutomationProjectManifest Normalize(
        AutomationProjectManifest manifest,
        string projectPath)
    {
        return manifest with
        {
            ProjectPath = projectPath,
            Applications = manifest.Applications ?? [],
            Snapshots = manifest.Snapshots ?? []
        };
    }

    private static string NormalizeProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));
        }

        return Path.GetFullPath(projectPath.Trim());
    }
}
