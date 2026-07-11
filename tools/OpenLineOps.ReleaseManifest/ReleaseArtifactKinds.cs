namespace OpenLineOps.ReleaseManifest;

public static class ReleaseArtifactKinds
{
    public const string Source = "source";
    public const string Api = "api";
    public const string Agent = "agent";
    public const string Runner = "runner";
    public const string PluginHost = "plugin-host";
    public const string ScriptWorker = "script-worker";
    public const string SamplePlugin = "sample-plugin";
    public const string Desktop = "desktop";

    public static string Parse(string? value)
    {
        foreach (var kind in CanonicalKinds)
        {
            if (string.Equals(value, kind, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        throw new InvalidOperationException(
            $"Unsupported release artifact kind '{value}'. Expected exactly one of: "
            + $"'{Source}', '{Api}', '{Agent}', '{Runner}', '{PluginHost}', '{ScriptWorker}', "
            + $"'{SamplePlugin}', '{Desktop}'.");
    }

    public static string FromRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release artifact '{relativePath}' must use forward slashes.");
        }

        var separatorIndex = relativePath.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            throw new InvalidOperationException(
                $"Release artifact '{relativePath}' must be stored under a canonical "
                + "top-level artifact-kind directory.");
        }

        return Parse(relativePath[..separatorIndex]);
    }

    private static readonly string[] CanonicalKinds =
    [
        Source,
        Api,
        Agent,
        Runner,
        PluginHost,
        ScriptWorker,
        SamplePlugin,
        Desktop
    ];
}
