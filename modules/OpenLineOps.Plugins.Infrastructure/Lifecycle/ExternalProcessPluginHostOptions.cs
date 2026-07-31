namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalProcessPluginHostOptions
{
    public string ExecutablePath { get; set; } = "OpenLineOps.PluginHost.exe";

    public string ArgumentsTemplate { get; set; } =
        "--openlineops-plugin-host --manifest \"{ManifestPath}\" --entry \"{EntryAssemblyPath}\" --type \"{EntryType}\"";

    public TimeSpan StartupProbeDelay { get; set; } = TimeSpan.Zero;

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public ExternalPluginSandboxOptions Sandbox { get; set; } = new();
}
