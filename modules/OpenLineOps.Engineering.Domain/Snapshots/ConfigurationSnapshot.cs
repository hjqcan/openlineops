using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Stations;

namespace OpenLineOps.Engineering.Domain.Snapshots;

public sealed class ConfigurationSnapshot : Entity<ConfigurationSnapshotId>
{
    private ConfigurationSnapshot(
        ConfigurationSnapshotId id,
        EngineeringProjectId projectId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        RecipeId recipeId,
        RecipeVersionId recipeVersionId,
        StationProfileId stationProfileId,
        IReadOnlyCollection<DeviceBindingSnapshot> deviceBindings,
        DateTimeOffset publishedAtUtc)
        : base(id)
    {
        ProjectId = projectId;
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        RecipeId = recipeId;
        RecipeVersionId = recipeVersionId;
        StationProfileId = stationProfileId;
        DeviceBindings = deviceBindings.ToArray();
        PublishedAtUtc = publishedAtUtc;
        Status = ConfigurationSnapshotStatus.Published;
    }

    public EngineeringProjectId ProjectId { get; }

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public RecipeId RecipeId { get; }

    public RecipeVersionId RecipeVersionId { get; }

    public StationProfileId StationProfileId { get; }

    public IReadOnlyCollection<DeviceBindingSnapshot> DeviceBindings { get; }

    public DateTimeOffset PublishedAtUtc { get; }

    public ConfigurationSnapshotStatus Status { get; }

    public bool IsPublished => Status == ConfigurationSnapshotStatus.Published;

    public static ConfigurationSnapshot Publish(
        ConfigurationSnapshotId id,
        EngineeringProjectId projectId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        RecipeId recipeId,
        RecipeVersionId recipeVersionId,
        StationProfile stationProfile,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(stationProfile);

        return new ConfigurationSnapshot(
            id,
            projectId,
            processDefinitionId,
            processVersionId,
            recipeId,
            recipeVersionId,
            stationProfile.Id,
            stationProfile.DeviceBindings
                .Select(binding => new DeviceBindingSnapshot(
                    binding.Id,
                    binding.OwnerSystemId,
                    binding.CapabilityId,
                    binding.DeviceKey))
                .ToArray(),
            publishedAtUtc);
    }

    public static ConfigurationSnapshot Restore(
        ConfigurationSnapshotId id,
        EngineeringProjectId projectId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        RecipeId recipeId,
        RecipeVersionId recipeVersionId,
        StationProfileId stationProfileId,
        IReadOnlyCollection<DeviceBindingSnapshot> deviceBindings,
        DateTimeOffset publishedAtUtc)
    {
        return new ConfigurationSnapshot(
            id,
            projectId,
            processDefinitionId,
            processVersionId,
            recipeId,
            recipeVersionId,
            stationProfileId,
            deviceBindings,
            publishedAtUtc);
    }
}
