namespace OpenLineOps.Plugin.Abstractions;

public interface IOpenLineOpsPlugin : IAsyncDisposable
{
    PluginManifest Manifest { get; }

    ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default);
}
