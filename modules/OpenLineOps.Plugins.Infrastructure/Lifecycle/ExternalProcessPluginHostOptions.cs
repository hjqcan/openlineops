namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalProcessPluginHostOptions
{
    public string ExecutablePath { get; set; } = "dotnet";

    public string ArgumentsTemplate { get; set; } =
        "\"{EntryAssemblyPath}\" --openlineops-plugin-host --manifest \"{ManifestPath}\"";

    public TimeSpan StartupProbeDelay { get; set; } = TimeSpan.Zero;

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public ExternalPluginSandboxOptions Sandbox { get; set; } = new();
}
