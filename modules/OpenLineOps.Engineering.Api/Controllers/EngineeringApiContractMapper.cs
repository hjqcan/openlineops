using OpenLineOps.Engineering.Api.Models;
using OpenLineOps.Engineering.Application.Configuration;
using CreateApiDeviceBindingRequest = OpenLineOps.Engineering.Api.Models.CreateDeviceBindingRequest;
using CreateApiEngineeringProjectRequest = OpenLineOps.Engineering.Api.Models.CreateEngineeringProjectRequest;
using CreateApiRecipeRequest = OpenLineOps.Engineering.Api.Models.CreateRecipeRequest;
using CreateApiStationProfileRequest = OpenLineOps.Engineering.Api.Models.CreateStationProfileRequest;
using CreateApiWorkspaceRequest = OpenLineOps.Engineering.Api.Models.CreateWorkspaceRequest;
using CreateApplicationDeviceBindingRequest = OpenLineOps.Engineering.Application.Configuration.CreateDeviceBindingRequest;
using CreateApplicationEngineeringProjectRequest = OpenLineOps.Engineering.Application.Configuration.CreateEngineeringProjectRequest;
using CreateApplicationRecipeRequest = OpenLineOps.Engineering.Application.Configuration.CreateRecipeRequest;
using CreateApplicationStationProfileRequest = OpenLineOps.Engineering.Application.Configuration.CreateStationProfileRequest;
using CreateApplicationWorkspaceRequest = OpenLineOps.Engineering.Application.Configuration.CreateWorkspaceRequest;
using PublishApiConfigurationSnapshotRequest = OpenLineOps.Engineering.Api.Models.PublishConfigurationSnapshotRequest;
using PublishApplicationConfigurationSnapshotRequest = OpenLineOps.Engineering.Application.Configuration.PublishConfigurationSnapshotRequest;
using RecipeApplicationParameterRequest = OpenLineOps.Engineering.Application.Configuration.RecipeParameterRequest;

namespace OpenLineOps.Engineering.Api.Controllers;

internal static class EngineeringApiContractMapper
{
    public static CreateApplicationEngineeringProjectRequest ToApplicationRequest(
        CreateApiEngineeringProjectRequest request)
    {
        return new CreateApplicationEngineeringProjectRequest(
            request.ProjectId!,
            request.WorkspaceId!,
            request.DisplayName!);
    }

    public static CreateApplicationWorkspaceRequest ToApplicationRequest(CreateApiWorkspaceRequest request)
    {
        return new CreateApplicationWorkspaceRequest(
            request.WorkspaceId!,
            request.DisplayName!);
    }

    public static CreateApplicationRecipeRequest ToApplicationRequest(CreateApiRecipeRequest request)
    {
        return new CreateApplicationRecipeRequest(
            request.RecipeId!,
            request.VersionId!,
            request.DisplayName!,
            request.Parameters!
                .Select(parameter => new RecipeApplicationParameterRequest(
                    parameter.Key!,
                    parameter.Value!))
                .ToArray());
    }

    public static CreateApplicationStationProfileRequest ToApplicationRequest(
        CreateApiStationProfileRequest request)
    {
        return new CreateApplicationStationProfileRequest(
            request.StationProfileId!,
            request.StationSystemId!,
            request.DisplayName!,
            request.DeviceBindings!
                .Select(binding => new CreateApplicationDeviceBindingRequest(
                    binding.DeviceBindingId!,
                    binding.OwnerSystemId!,
                    binding.CapabilityId!,
                    binding.DeviceKey!))
                .ToArray());
    }

    public static PublishApplicationConfigurationSnapshotRequest ToApplicationRequest(
        PublishApiConfigurationSnapshotRequest request)
    {
        return new PublishApplicationConfigurationSnapshotRequest(
            request.SnapshotId!,
            request.ProcessDefinitionId!,
            request.ProcessVersionId!,
            request.RecipeId!,
            request.StationProfileId!);
    }

    public static EngineeringProjectResponse ToResponse(EngineeringProjectDetails project)
    {
        return new EngineeringProjectResponse(
            project.ProjectId,
            project.WorkspaceId,
            project.DisplayName,
            project.CreatedAtUtc,
            project.ActiveSnapshotId,
            project.Snapshots.Select(ToResponse).ToArray());
    }

    public static WorkspaceResponse ToResponse(WorkspaceDetails workspace)
    {
        return new WorkspaceResponse(
            workspace.WorkspaceId,
            workspace.DisplayName,
            workspace.CreatedAtUtc);
    }

    public static ConfigurationSnapshotResponse ToResponse(ConfigurationSnapshotDetails snapshot)
    {
        return new ConfigurationSnapshotResponse(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.RecipeId,
            snapshot.RecipeVersionId,
            snapshot.StationProfileId,
            snapshot.Status,
            snapshot.PublishedAtUtc,
            snapshot.DeviceBindings
                .Select(binding => new DeviceBindingSnapshotResponse(
                    binding.DeviceBindingId,
                    binding.OwnerSystemId,
                    binding.CapabilityId,
                    binding.DeviceKey))
                .ToArray());
    }

    public static RecipeResponse ToResponse(RecipeDetails recipe)
    {
        return new RecipeResponse(
            recipe.RecipeId,
            recipe.VersionId,
            recipe.DisplayName,
            recipe.Status,
            recipe.CreatedAtUtc,
            recipe.PublishedAtUtc,
            recipe.Parameters
                .Select(parameter => new RecipeParameterResponse(parameter.Key, parameter.Value))
                .ToArray());
    }

    public static StationProfileResponse ToResponse(StationProfileDetails stationProfile)
    {
        return new StationProfileResponse(
            stationProfile.StationProfileId,
            stationProfile.StationSystemId,
            stationProfile.DisplayName,
            stationProfile.DeviceBindings
                .Select(binding => new DeviceBindingResponse(
                    binding.DeviceBindingId,
                    binding.OwnerSystemId,
                    binding.CapabilityId,
                    binding.DeviceKey))
                .ToArray());
    }

    public static ConfigurationSnapshotDiffResponse ToResponse(
        ConfigurationSnapshotDiffDetails diff)
    {
        return new ConfigurationSnapshotDiffResponse(
            diff.ProjectId,
            diff.FromSnapshotId,
            diff.ToSnapshotId,
            diff.Changes
                .Select(change => new ConfigurationSnapshotDiffItemResponse(
                    change.Area,
                    change.Field,
                    change.FromValue,
                    change.ToValue,
                    change.ChangeType))
                .ToArray());
    }

    public static Dictionary<string, string[]> Validate(CreateApiEngineeringProjectRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ProjectId), request.ProjectId);
        AddRequired(errors, nameof(request.WorkspaceId), request.WorkspaceId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateApiWorkspaceRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.WorkspaceId), request.WorkspaceId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateApiRecipeRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.RecipeId), request.RecipeId);
        AddRequired(errors, nameof(request.VersionId), request.VersionId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        if (request.Parameters is null)
        {
            errors[nameof(request.Parameters)] = ["Parameters collection is required."];
        }
        else
        {
            ValidateRecipeParameters(errors, request.Parameters);
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateApiStationProfileRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.StationProfileId), request.StationProfileId);
        AddRequired(errors, nameof(request.StationSystemId), request.StationSystemId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        if (request.DeviceBindings is null)
        {
            errors[nameof(request.DeviceBindings)] = ["DeviceBindings collection is required."];
        }
        else
        {
            ValidateDeviceBindings(errors, request.DeviceBindings);
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(
        PublishApiConfigurationSnapshotRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.SnapshotId), request.SnapshotId);
        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.ProcessVersionId), request.ProcessVersionId);
        AddRequired(errors, nameof(request.RecipeId), request.RecipeId);
        AddRequired(errors, nameof(request.StationProfileId), request.StationProfileId);
        return errors;
    }

    private static void ValidateRecipeParameters(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<Models.RecipeParameterRequest> parameters)
    {
        var index = 0;
        foreach (var parameter in parameters)
        {
            var prefix = $"Parameters[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(parameter.Key)}", parameter.Key);
            AddRequired(errors, $"{prefix}.{nameof(parameter.Value)}", parameter.Value);
            index++;
        }
    }

    private static void ValidateDeviceBindings(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<CreateApiDeviceBindingRequest> deviceBindings)
    {
        var index = 0;
        foreach (var binding in deviceBindings)
        {
            var prefix = $"DeviceBindings[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(binding.DeviceBindingId)}", binding.DeviceBindingId);
            AddRequired(errors, $"{prefix}.{nameof(binding.CapabilityId)}", binding.CapabilityId);
            AddRequired(errors, $"{prefix}.{nameof(binding.DeviceKey)}", binding.DeviceKey);
            index++;
        }
    }

    private static void AddRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }
}
