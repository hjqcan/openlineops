using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Application.Lifecycle;

public sealed record PluginActivationResult(IOpenLineOpsPlugin? Plugin, string? FailureReason)
{
    public bool Succeeded => Plugin is not null;

    public static PluginActivationResult Success(IOpenLineOpsPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        return new PluginActivationResult(plugin, null);
    }

    public static PluginActivationResult Failure(string failureReason)
    {
        return new PluginActivationResult(
            null,
            string.IsNullOrWhiteSpace(failureReason)
                ? "Plugin activation failed."
                : failureReason.Trim());
    }
}
