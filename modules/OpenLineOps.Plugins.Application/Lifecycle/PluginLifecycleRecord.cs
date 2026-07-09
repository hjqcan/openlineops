using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Application.Lifecycle;

public sealed record PluginLifecycleRecord(
    PluginManifest Manifest,
    PluginLifecycleState State,
    PluginInitializationStatus InitializationStatus,
    IReadOnlyCollection<PluginValidationIssue> ValidationIssues,
    string? FailureReason)
{
    public static PluginLifecycleRecord Invalid(
        PluginManifest manifest,
        IReadOnlyCollection<PluginValidationIssue> validationIssues)
    {
        return new PluginLifecycleRecord(
            manifest,
            PluginLifecycleState.Invalid,
            PluginInitializationStatus.NotStarted,
            validationIssues,
            "Plugin manifest validation failed.");
    }

    public static PluginLifecycleRecord Initialized(PluginManifest manifest)
    {
        return new PluginLifecycleRecord(
            manifest,
            PluginLifecycleState.Initialized,
            PluginInitializationStatus.Initialized,
            [],
            null);
    }

    public static PluginLifecycleRecord Degraded(PluginManifest manifest)
    {
        return new PluginLifecycleRecord(
            manifest,
            PluginLifecycleState.Degraded,
            PluginInitializationStatus.Degraded,
            [],
            "Plugin initialized in degraded mode.");
    }

    public static PluginLifecycleRecord Failed(PluginManifest manifest, string failureReason)
    {
        return new PluginLifecycleRecord(
            manifest,
            PluginLifecycleState.Failed,
            PluginInitializationStatus.Failed,
            [],
            string.IsNullOrWhiteSpace(failureReason) ? "Plugin failed." : failureReason.Trim());
    }

    public static PluginLifecycleRecord Stopped(PluginManifest manifest)
    {
        return new PluginLifecycleRecord(
            manifest,
            PluginLifecycleState.Stopped,
            PluginInitializationStatus.NotStarted,
            [],
            null);
    }
}
