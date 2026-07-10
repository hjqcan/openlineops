using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Projects.Domain.Identifiers;

namespace OpenLineOps.Projects.Domain.Snapshots;

public sealed class PublishedProjectSnapshot : Entity<PublishedProjectSnapshotId>
{
    private readonly List<SnapshotCapabilityBinding> _capabilityBindings;
    private readonly List<ProjectTargetReference> _targetReferences;
    private readonly List<string> _blockVersionIds;
    private readonly List<string> _layoutIds;

    private PublishedProjectSnapshot(
        PublishedProjectSnapshotId id,
        AutomationProjectId projectId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        IEnumerable<string> layoutIds,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        string releaseManifestPath,
        string releaseContentSha256,
        DateTimeOffset publishedAtUtc)
        : base(id)
    {
        ProjectId = projectId;
        ApplicationId = applicationId;
        TopologyId = topologyId;
        _layoutIds = NormalizeDistinct(layoutIds);
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        _capabilityBindings = capabilityBindings.ToList();
        _targetReferences = targetReferences.ToList();
        _blockVersionIds = blockVersionIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReleaseManifestPath = NormalizeReleaseManifestPath(releaseManifestPath);
        ReleaseContentSha256 = NormalizeReleaseContentSha256(releaseContentSha256);
        PublishedAtUtc = publishedAtUtc;
    }

    public AutomationProjectId ProjectId { get; }

    public ProjectApplicationId ApplicationId { get; }

    public AutomationTopologyId TopologyId { get; }

    public IReadOnlyCollection<string> LayoutIds => _layoutIds.AsReadOnly();

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public DateTimeOffset PublishedAtUtc { get; }

    public IReadOnlyCollection<SnapshotCapabilityBinding> CapabilityBindings => _capabilityBindings.AsReadOnly();

    public IReadOnlyCollection<ProjectTargetReference> TargetReferences => _targetReferences.AsReadOnly();

    public IReadOnlyCollection<string> BlockVersionIds => _blockVersionIds.AsReadOnly();

    public string ReleaseManifestPath { get; }

    public string ReleaseContentSha256 { get; }

    public static PublishedProjectSnapshot Publish(
        PublishedProjectSnapshotId id,
        AutomationProjectId projectId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        IEnumerable<string> layoutIds,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        string releaseManifestPath,
        string releaseContentSha256,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(layoutIds);
        ArgumentNullException.ThrowIfNull(capabilityBindings);
        ArgumentNullException.ThrowIfNull(targetReferences);
        ArgumentNullException.ThrowIfNull(blockVersionIds);

        if (string.IsNullOrWhiteSpace(releaseManifestPath))
        {
            throw new ArgumentException("Release manifest path cannot be empty.", nameof(releaseManifestPath));
        }

        if (string.IsNullOrWhiteSpace(releaseContentSha256))
        {
            throw new ArgumentException("Release content SHA-256 cannot be empty.", nameof(releaseContentSha256));
        }

        return new PublishedProjectSnapshot(
            id,
            projectId,
            applicationId,
            topologyId,
            layoutIds,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            releaseManifestPath,
            releaseContentSha256,
            publishedAtUtc);
    }

    public static PublishedProjectSnapshot Restore(
        PublishedProjectSnapshotId id,
        AutomationProjectId projectId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        IEnumerable<string> layoutIds,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        string releaseManifestPath,
        string releaseContentSha256,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(layoutIds);
        ArgumentNullException.ThrowIfNull(capabilityBindings);
        ArgumentNullException.ThrowIfNull(targetReferences);
        ArgumentNullException.ThrowIfNull(blockVersionIds);

        return new PublishedProjectSnapshot(
            id,
            projectId,
            applicationId,
            topologyId,
            layoutIds,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            releaseManifestPath,
            releaseContentSha256,
            publishedAtUtc);
    }

    private static List<string> NormalizeDistinct(IEnumerable<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeReleaseManifestPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\')
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Release manifest path must be a canonical forward-slash relative path.",
                nameof(value));
        }

        return value;
    }

    private static string NormalizeReleaseContentSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || !value.All(Uri.IsHexDigit)
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Release content SHA-256 must be a lowercase 64-character value.",
                nameof(value));
        }

        return value;
    }
}
