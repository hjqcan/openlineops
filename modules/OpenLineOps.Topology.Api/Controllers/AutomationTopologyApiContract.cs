using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Application.Topologies;
using ApiAddCapabilityRequest = OpenLineOps.Topology.Api.Models.AddCapabilityContractRequest;
using ApiAddDriverBindingRequest = OpenLineOps.Topology.Api.Models.AddDriverBindingRequest;
using ApiAddSystemRequest = OpenLineOps.Topology.Api.Models.AddAutomationSystemRequest;
using ApiAddSlotGroupRequest = OpenLineOps.Topology.Api.Models.AddSlotGroupRequest;
using ApiAddSlotRequest = OpenLineOps.Topology.Api.Models.AddSlotDefinitionRequest;
using ApiCreateTopologyRequest = OpenLineOps.Topology.Api.Models.CreateAutomationTopologyRequest;
using ApiUpdateSystemRequest = OpenLineOps.Topology.Api.Models.UpdateAutomationSystemRequest;
using ApiUpdateSlotGroupRequest = OpenLineOps.Topology.Api.Models.UpdateSlotGroupRequest;
using ApiUpdateSlotRequest = OpenLineOps.Topology.Api.Models.UpdateSlotDefinitionRequest;

namespace OpenLineOps.Topology.Api.Controllers;

internal static class AutomationTopologyApiContract
{
    public static AutomationTopologyResponse ToResponse(AutomationTopologyDetails topology)
    {
        return new AutomationTopologyResponse(
            topology.TopologyId,
            topology.DisplayName,
            topology.CreatedAtUtc,
            topology.Systems.Select(system => new AutomationSystemResponse(
                system.SystemId,
                system.ParentSystemId,
                system.Kind,
                system.SystemType,
                system.DisplayName,
                system.RequiredCapabilityIds,
                system.ProvidedCapabilityIds,
                system.Metadata)).ToArray(),
            topology.Capabilities.Select(capability => new CapabilityContractResponse(
                capability.CapabilityId,
                capability.CommandName,
                capability.Version,
                capability.InputSchema,
                capability.OutputSchema,
                capability.TimeoutSeconds,
                capability.SafetyClass)).ToArray(),
            topology.DriverBindings.Select(binding => new DriverBindingResponse(
                binding.BindingId,
                binding.CapabilityId,
                binding.ProviderKind,
                binding.ProviderKey)).ToArray(),
            topology.SlotGroups.Select(group => new SlotGroupResponse(
                group.SlotGroupId,
                group.ParentSystemId,
                group.DisplayName,
                group.Kind,
                group.Capacity,
                group.SlotIds)).ToArray(),
            topology.Slots.Select(slot => new SlotDefinitionResponse(
                slot.SlotId,
                slot.SlotGroupId,
                slot.ParentSystemId,
                slot.Address,
                slot.DisplayName,
                slot.MaterialKind,
                slot.IsEnabled)).ToArray());
    }

    public static TopologyTargetDeletionResponse ToResponse(TopologyTargetDeletionDetails deletion)
    {
        return new TopologyTargetDeletionResponse(
            ToResponse(deletion.Topology),
            deletion.UpdatedLayoutCount,
            deletion.RemovedLayoutElementCount,
            deletion.PublicationImpact);
    }

    public static Dictionary<string, string[]> Validate(ApiCreateTopologyRequest? request)
    {
        var errors = NewErrors(request);
        if (request is not null)
        {
            AddRequired(errors, nameof(request.TopologyId), request.TopologyId);
            AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiAddSystemRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.SystemId), request.SystemId);
        AddRequired(errors, nameof(request.Kind), request.Kind);
        AddRequired(errors, nameof(request.SystemType), request.SystemType);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequiredCollection(errors, nameof(request.RequiredCapabilityIds), request.RequiredCapabilityIds);
        AddRequiredCollection(errors, nameof(request.ProvidedCapabilityIds), request.ProvidedCapabilityIds);
        if (request.Metadata is null)
        {
            errors[nameof(request.Metadata)] = ["Object is required."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiUpdateSystemRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddPatchRequired(errors, request.SystemType, request.DisplayName, request.Metadata);
        AddOptionalRequired(errors, nameof(request.SystemType), request.SystemType);
        AddOptionalRequired(errors, nameof(request.DisplayName), request.DisplayName);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiAddCapabilityRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.CapabilityId), request.CapabilityId);
        AddRequired(errors, nameof(request.CommandName), request.CommandName);
        AddRequired(errors, nameof(request.Version), request.Version);
        AddRequired(errors, nameof(request.SafetyClass), request.SafetyClass);
        AddPositive(errors, nameof(request.TimeoutSeconds), request.TimeoutSeconds);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiAddDriverBindingRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.BindingId), request.BindingId);
        AddRequired(errors, nameof(request.CapabilityId), request.CapabilityId);
        AddRequired(errors, nameof(request.ProviderKind), request.ProviderKind);
        AddRequired(errors, nameof(request.ProviderKey), request.ProviderKey);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiAddSlotGroupRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.SlotGroupId), request.SlotGroupId);
        AddRequired(errors, nameof(request.ParentSystemId), request.ParentSystemId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.Kind), request.Kind);
        AddPositive(errors, nameof(request.Capacity), request.Capacity);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiUpdateSlotGroupRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddPatchRequired(errors, request.DisplayName, request.Kind, request.Capacity);
        AddOptionalRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddOptionalRequired(errors, nameof(request.Kind), request.Kind);
        if (request.Capacity is <= 0)
        {
            errors[nameof(request.Capacity)] = ["Value must be positive when supplied."];
        }
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiAddSlotRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddRequired(errors, nameof(request.SlotGroupId), request.SlotGroupId);
        AddRequired(errors, nameof(request.SlotId), request.SlotId);
        AddRequired(errors, nameof(request.ParentSystemId), request.ParentSystemId);
        AddRequired(errors, nameof(request.Address), request.Address);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.MaterialKind), request.MaterialKind);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiUpdateSlotRequest? request)
    {
        var errors = NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddPatchRequired(errors, request.Address, request.DisplayName, request.MaterialKind, request.IsEnabled);
        AddOptionalRequired(errors, nameof(request.Address), request.Address);
        AddOptionalRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddOptionalRequired(errors, nameof(request.MaterialKind), request.MaterialKind);
        return errors;
    }

    public static Dictionary<string, string[]> NewErrors(object? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
        }

        return errors;
    }

    public static void AddRequired(Dictionary<string, string[]> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }

    private static void AddOptionalRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value cannot be blank when supplied."];
        }
    }

    private static void AddPatchRequired(
        Dictionary<string, string[]> errors,
        params object?[] values)
    {
        if (values.All(value => value is null))
        {
            errors["request"] = ["At least one mutable property is required."];
        }
    }

    private static void AddPositive(Dictionary<string, string[]> errors, string key, int? value)
    {
        if (value is null or <= 0)
        {
            errors[key] = ["Value must be positive."];
        }
    }

    private static void AddRequiredCollection(
        Dictionary<string, string[]> errors,
        string key,
        IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            errors[key] = ["Collection is required."];
        }
    }
}
