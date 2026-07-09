using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Projects.Domain.Identifiers;

namespace OpenLineOps.Projects.Domain.Snapshots;

public sealed class PublishedProjectSnapshot : Entity<PublishedProjectSnapshotId>
{
    private readonly List<SnapshotCapabilityBinding> _capabilityBindings;
    private readonly List<ProjectTargetReference> _targetReferences;
    private readonly List<string> _blockVersionIds;

    private PublishedProjectSnapshot(
        PublishedProjectSnapshotId id,
        AutomationProjectId projectId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        DateTimeOffset publishedAtUtc)
        : base(id)
    {
        ProjectId = projectId;
        ApplicationId = applicationId;
        TopologyId = topologyId;
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
        PublishedAtUtc = publishedAtUtc;
    }

    public AutomationProjectId ProjectId { get; }

    public ProjectApplicationId ApplicationId { get; }

    public AutomationTopologyId TopologyId { get; }

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public DateTimeOffset PublishedAtUtc { get; }

    public IReadOnlyCollection<SnapshotCapabilityBinding> CapabilityBindings => _capabilityBindings.AsReadOnly();

    public IReadOnlyCollection<ProjectTargetReference> TargetReferences => _targetReferences.AsReadOnly();

    public IReadOnlyCollection<string> BlockVersionIds => _blockVersionIds.AsReadOnly();

    public static PublishedProjectSnapshot Publish(
        PublishedProjectSnapshotId id,
        AutomationProjectId projectId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(capabilityBindings);
        ArgumentNullException.ThrowIfNull(targetReferences);
        ArgumentNullException.ThrowIfNull(blockVersionIds);

        return new PublishedProjectSnapshot(
            id,
            projectId,
            applicationId,
            topologyId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            publishedAtUtc);
    }

    public static PublishedProjectSnapshot Restore(
        PublishedProjectSnapshotId id,
        AutomationProjectId projectId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(capabilityBindings);
        ArgumentNullException.ThrowIfNull(targetReferences);
        ArgumentNullException.ThrowIfNull(blockVersionIds);

        return new PublishedProjectSnapshot(
            id,
            projectId,
            applicationId,
            topologyId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            publishedAtUtc);
    }
}
