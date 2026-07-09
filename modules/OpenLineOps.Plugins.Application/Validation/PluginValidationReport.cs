using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Application.Validation;

public sealed record PluginValidationReport(
    PluginManifest Manifest,
    IReadOnlyCollection<PluginValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}
