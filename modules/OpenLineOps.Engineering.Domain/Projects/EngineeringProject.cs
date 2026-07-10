using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Snapshots;
using OpenLineOps.Engineering.Domain.Stations;

namespace OpenLineOps.Engineering.Domain.Projects;

public sealed class EngineeringProject : AggregateRoot<EngineeringProjectId>
{
    private readonly List<ConfigurationSnapshot> _snapshots = [];

    private EngineeringProject(
        EngineeringProjectId id,
        WorkspaceId workspaceId,
        string displayName,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        WorkspaceId = workspaceId;
        DisplayName = EngineeringIdGuard.NotBlank(displayName, nameof(displayName));
        CreatedAtUtc = createdAtUtc;
    }

    public WorkspaceId WorkspaceId { get; }

    public string DisplayName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public ConfigurationSnapshotId? ActiveSnapshotId { get; private set; }

    public IReadOnlyCollection<ConfigurationSnapshot> Snapshots => _snapshots.AsReadOnly();

    public static EngineeringProject Create(
        EngineeringProjectId id,
        WorkspaceId workspaceId,
        string displayName,
        DateTimeOffset createdAtUtc)
    {
        return new EngineeringProject(id, workspaceId, displayName, createdAtUtc);
    }

    public static EngineeringProject Restore(
        EngineeringProjectId id,
        WorkspaceId workspaceId,
        string displayName,
        DateTimeOffset createdAtUtc,
        ConfigurationSnapshotId? activeSnapshotId,
        IEnumerable<ConfigurationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var project = new EngineeringProject(id, workspaceId, displayName, createdAtUtc)
        {
            ActiveSnapshotId = activeSnapshotId
        };

        project._snapshots.AddRange(snapshots);
        return project;
    }

    public EngineeringOperationResult PublishSnapshot(
        ConfigurationSnapshotId snapshotId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        Recipe recipe,
        StationProfile stationProfile,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(stationProfile);

        if (_snapshots.Any(snapshot => snapshot.Id == snapshotId))
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.SnapshotAlreadyExists",
                $"Configuration snapshot {snapshotId} already exists.");
        }

        if (!recipe.IsPublished)
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.RecipeNotPublished",
                $"Recipe {recipe.Id} must be published before it can be included in a configuration snapshot.");
        }

        if (stationProfile.DeviceBindings.Count == 0)
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.StationHasNoDeviceBindings",
                $"Station profile {stationProfile.Id} must bind at least one device capability.");
        }

        var snapshot = ConfigurationSnapshot.Publish(
            snapshotId,
            Id,
            processDefinitionId,
            processVersionId,
            recipe.Id,
            recipe.VersionId,
            stationProfile,
            publishedAtUtc);

        _snapshots.Add(snapshot);
        ActiveSnapshotId = snapshot.Id;

        return EngineeringOperationResult.Accepted("Configuration snapshot published.");
    }

    public EngineeringOperationResult RollbackToSnapshot(ConfigurationSnapshotId snapshotId)
    {
        var snapshot = _snapshots.SingleOrDefault(candidate => candidate.Id == snapshotId);
        if (snapshot is null)
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.SnapshotNotFound",
                $"Configuration snapshot {snapshotId} was not found in project {Id}.");
        }

        if (!snapshot.IsPublished)
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.SnapshotNotPublished",
                $"Configuration snapshot {snapshotId} is not published.");
        }

        ActiveSnapshotId = snapshot.Id;
        return EngineeringOperationResult.Accepted("Engineering project rolled back.");
    }
}
