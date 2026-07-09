using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Snapshots;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

internal static class EngineeringSnapshotMapper
{
    public static PersistedWorkspace ToSnapshot(Workspace workspace)
    {
        return new PersistedWorkspace(
            workspace.Id.Value,
            workspace.DisplayName,
            workspace.CreatedAtUtc);
    }

    public static PersistedEngineeringProject ToSnapshot(EngineeringProject project)
    {
        return new PersistedEngineeringProject(
            project.Id.Value,
            project.WorkspaceId.Value,
            project.DisplayName,
            project.CreatedAtUtc,
            project.ActiveSnapshotId?.Value,
            project.Snapshots.Select(ToSnapshot).ToArray());
    }

    public static PersistedRecipe ToSnapshot(Recipe recipe)
    {
        return new PersistedRecipe(
            recipe.Id.Value,
            recipe.VersionId.Value,
            recipe.DisplayName,
            recipe.Status.ToString(),
            recipe.CreatedAtUtc,
            recipe.PublishedAtUtc,
            recipe.Parameters
                .Select(parameter => new PersistedRecipeParameter(parameter.Key, parameter.Value))
                .ToArray());
    }

    public static PersistedStationProfile ToSnapshot(StationProfile stationProfile)
    {
        return new PersistedStationProfile(
            stationProfile.Id.Value,
            stationProfile.DisplayName,
            stationProfile.DeviceBindings.Select(ToSnapshot).ToArray());
    }

    public static EngineeringProject ToAggregate(PersistedEngineeringProject snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return EngineeringProject.Restore(
            new EngineeringProjectId(snapshot.ProjectId),
            new WorkspaceId(snapshot.WorkspaceId),
            snapshot.DisplayName,
            snapshot.CreatedAtUtc,
            string.IsNullOrWhiteSpace(snapshot.ActiveSnapshotId)
                ? null
                : new ConfigurationSnapshotId(snapshot.ActiveSnapshotId),
            snapshot.Snapshots.Select(ToAggregate));
    }

    public static Workspace ToAggregate(PersistedWorkspace snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Workspace.Restore(
            new WorkspaceId(snapshot.WorkspaceId),
            snapshot.DisplayName,
            snapshot.CreatedAtUtc);
    }

    public static Recipe ToAggregate(PersistedRecipe snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Recipe.Restore(
            new RecipeId(snapshot.RecipeId),
            new RecipeVersionId(snapshot.VersionId),
            snapshot.DisplayName,
            ParseEnum<RecipeStatus>(snapshot.Status, nameof(snapshot.Status)),
            snapshot.CreatedAtUtc,
            snapshot.PublishedAtUtc,
            snapshot.Parameters.Select(parameter => new RecipeParameter(parameter.Key, parameter.Value)));
    }

    public static StationProfile ToAggregate(PersistedStationProfile snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return StationProfile.Restore(
            new StationProfileId(snapshot.StationProfileId),
            snapshot.DisplayName,
            snapshot.DeviceBindings.Select(ToAggregate));
    }

    private static PersistedConfigurationSnapshot ToSnapshot(ConfigurationSnapshot snapshot)
    {
        return new PersistedConfigurationSnapshot(
            snapshot.Id.Value,
            snapshot.ProjectId.Value,
            snapshot.ProcessDefinitionId.Value,
            snapshot.ProcessVersionId.Value,
            snapshot.RecipeId.Value,
            snapshot.RecipeVersionId.Value,
            snapshot.StationProfileId.Value,
            snapshot.Status.ToString(),
            snapshot.PublishedAtUtc,
            snapshot.DeviceBindings.Select(ToSnapshot).ToArray());
    }

    private static PersistedDeviceBinding ToSnapshot(DeviceBinding binding)
    {
        return new PersistedDeviceBinding(
            binding.Id.Value,
            binding.CapabilityId.Value,
            binding.DeviceKey);
    }

    private static PersistedDeviceBindingSnapshot ToSnapshot(DeviceBindingSnapshot binding)
    {
        return new PersistedDeviceBindingSnapshot(
            binding.DeviceBindingId.Value,
            binding.CapabilityId.Value,
            binding.DeviceKey);
    }

    private static ConfigurationSnapshot ToAggregate(PersistedConfigurationSnapshot snapshot)
    {
        var status = ParseEnum<ConfigurationSnapshotStatus>(snapshot.Status, nameof(snapshot.Status));
        if (status != ConfigurationSnapshotStatus.Published)
        {
            throw new InvalidOperationException(
                $"Persisted configuration snapshot {snapshot.SnapshotId} has unsupported status {snapshot.Status}.");
        }

        return ConfigurationSnapshot.Restore(
            new ConfigurationSnapshotId(snapshot.SnapshotId),
            new EngineeringProjectId(snapshot.ProjectId),
            new ProcessDefinitionId(snapshot.ProcessDefinitionId),
            new ProcessVersionId(snapshot.ProcessVersionId),
            new RecipeId(snapshot.RecipeId),
            new RecipeVersionId(snapshot.RecipeVersionId),
            new StationProfileId(snapshot.StationProfileId),
            snapshot.DeviceBindings.Select(ToAggregate).ToArray(),
            snapshot.PublishedAtUtc);
    }

    private static DeviceBinding ToAggregate(PersistedDeviceBinding binding)
    {
        return DeviceBinding.Create(
            new DeviceBindingId(binding.DeviceBindingId),
            new DeviceCapabilityId(binding.CapabilityId),
            binding.DeviceKey);
    }

    private static DeviceBindingSnapshot ToAggregate(PersistedDeviceBindingSnapshot binding)
    {
        return new DeviceBindingSnapshot(
            new DeviceBindingId(binding.DeviceBindingId),
            new DeviceCapabilityId(binding.CapabilityId),
            binding.DeviceKey);
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Persisted {fieldName} value '{value}' is invalid.");
    }
}

internal sealed record PersistedWorkspace(
    string WorkspaceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc);

internal sealed record PersistedEngineeringProject(
    string ProjectId,
    string WorkspaceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    string? ActiveSnapshotId,
    PersistedConfigurationSnapshot[] Snapshots);

internal sealed record PersistedConfigurationSnapshot(
    string SnapshotId,
    string ProjectId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string RecipeId,
    string RecipeVersionId,
    string StationProfileId,
    string Status,
    DateTimeOffset PublishedAtUtc,
    PersistedDeviceBindingSnapshot[] DeviceBindings);

internal sealed record PersistedRecipe(
    string RecipeId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    PersistedRecipeParameter[] Parameters);

internal sealed record PersistedRecipeParameter(string Key, string Value);

internal sealed record PersistedStationProfile(
    string StationProfileId,
    string DisplayName,
    PersistedDeviceBinding[] DeviceBindings);

internal sealed record PersistedDeviceBinding(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);

internal sealed record PersistedDeviceBindingSnapshot(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);
