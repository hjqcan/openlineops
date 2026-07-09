using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Snapshots;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Application.Configuration;

internal static class EngineeringConfigurationMapper
{
    public static WorkspaceDetails ToDetails(Workspace workspace)
    {
        return new WorkspaceDetails(
            workspace.Id.Value,
            workspace.DisplayName,
            workspace.CreatedAtUtc);
    }

    public static EngineeringProjectDetails ToDetails(EngineeringProject project)
    {
        return new EngineeringProjectDetails(
            project.Id.Value,
            project.WorkspaceId.Value,
            project.DisplayName,
            project.CreatedAtUtc,
            project.ActiveSnapshotId?.Value,
            project.Snapshots
                .OrderBy(snapshot => snapshot.PublishedAtUtc)
                .Select(ToDetails)
                .ToArray());
    }

    public static RecipeDetails ToDetails(Recipe recipe)
    {
        return new RecipeDetails(
            recipe.Id.Value,
            recipe.VersionId.Value,
            recipe.DisplayName,
            recipe.Status.ToString(),
            recipe.CreatedAtUtc,
            recipe.PublishedAtUtc,
            recipe.Parameters
                .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                .Select(parameter => new RecipeParameterDetails(parameter.Key, parameter.Value))
                .ToArray());
    }

    public static StationProfileDetails ToDetails(StationProfile stationProfile)
    {
        return new StationProfileDetails(
            stationProfile.Id.Value,
            stationProfile.DisplayName,
            stationProfile.DeviceBindings
                .OrderBy(binding => binding.Id.Value, StringComparer.Ordinal)
                .Select(binding => new DeviceBindingDetails(
                    binding.Id.Value,
                    binding.CapabilityId.Value,
                    binding.DeviceKey))
                .ToArray());
    }

    private static ConfigurationSnapshotDetails ToDetails(ConfigurationSnapshot snapshot)
    {
        return new ConfigurationSnapshotDetails(
            snapshot.Id.Value,
            snapshot.ProjectId.Value,
            snapshot.ProcessDefinitionId.Value,
            snapshot.ProcessVersionId.Value,
            snapshot.RecipeId.Value,
            snapshot.RecipeVersionId.Value,
            snapshot.StationProfileId.Value,
            snapshot.Status.ToString(),
            snapshot.PublishedAtUtc,
            snapshot.DeviceBindings
                .OrderBy(binding => binding.DeviceBindingId.Value, StringComparer.Ordinal)
                .Select(binding => new DeviceBindingSnapshotDetails(
                    binding.DeviceBindingId.Value,
                    binding.CapabilityId.Value,
                    binding.DeviceKey))
                .ToArray());
    }
}
