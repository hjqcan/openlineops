namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginProcessCommandDefinition(
    string Id,
    string Capability,
    string CommandName,
    string? InputSchema = null,
    string? OutputSchema = null,
    int TimeoutMilliseconds = 30000,
    int MaxRetries = 0);
