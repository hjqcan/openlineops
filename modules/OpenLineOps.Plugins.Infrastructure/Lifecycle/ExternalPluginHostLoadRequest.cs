namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed record ExternalPluginHostLoadRequest(
    string ManifestPath,
    string? EntryAssemblyPath = null,
    string? EntryType = null);
