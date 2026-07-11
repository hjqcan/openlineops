using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Devices.Api.Models;
using OpenLineOps.Devices.Application.Configuration;
using ApiCreateDefinitionRequest = OpenLineOps.Devices.Api.Models.CreateDeviceDefinitionRequest;
using ApiRegisterInstanceRequest = OpenLineOps.Devices.Api.Models.RegisterDeviceInstanceRequest;
using ApiStatusChangeRequest = OpenLineOps.Devices.Api.Models.DeviceStatusChangeRequest;
using ApplicationCreateCapabilityRequest = OpenLineOps.Devices.Application.Configuration.CreateDeviceCapabilityRequest;
using ApplicationCreateCommandRequest = OpenLineOps.Devices.Application.Configuration.CreateDeviceCommandDefinitionRequest;
using ApplicationCreateDefinitionRequest = OpenLineOps.Devices.Application.Configuration.CreateDeviceDefinitionRequest;
using ApplicationRegisterInstanceRequest = OpenLineOps.Devices.Application.Configuration.RegisterDeviceInstanceRequest;
using ApplicationStatusChangeRequest = OpenLineOps.Devices.Application.Configuration.DeviceStatusChangeRequest;

namespace OpenLineOps.Devices.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Devices)]
[Route(OpenLineOpsApiRoutes.Devices)]
public sealed class DeviceConfigurationController : ControllerBase
{
    private readonly IDeviceConfigurationService _configurationService;

    public DeviceConfigurationController(IDeviceConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    [HttpPost("definitions")]
    [ProducesResponseType<DeviceDefinitionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceDefinitionResponse>> CreateDefinitionAsync(
        ApiCreateDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .CreateDefinitionAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/devices/definitions/{response.DeviceDefinitionId}", response);
    }

    [HttpGet("definitions")]
    [ProducesResponseType<IReadOnlyCollection<DeviceDefinitionResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<DeviceDefinitionResponse>>> ListDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        var result = await _configurationService.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("definitions/{deviceDefinitionId}")]
    [ProducesResponseType<DeviceDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDefinitionResponse>> GetDefinitionAsync(
        string deviceDefinitionId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetDefinitionAsync(deviceDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("instances")]
    [ProducesResponseType<DeviceInstanceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceInstanceResponse>> RegisterInstanceAsync(
        ApiRegisterInstanceRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _configurationService
            .RegisterInstanceAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/devices/instances/{response.DeviceInstanceId}", response);
    }

    [HttpGet("instances")]
    [ProducesResponseType<IReadOnlyCollection<DeviceInstanceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<DeviceInstanceResponse>>> ListInstancesAsync(
        CancellationToken cancellationToken)
    {
        var result = await _configurationService.ListInstancesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("instances/{deviceInstanceId}")]
    [ProducesResponseType<DeviceInstanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceInstanceResponse>> GetInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .GetInstanceAsync(deviceInstanceId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("instances/{deviceInstanceId}/connect")]
    [ProducesResponseType<DeviceInstanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceInstanceResponse>> ConnectInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ConnectInstanceAsync(deviceInstanceId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("instances/{deviceInstanceId}/disconnect")]
    [ProducesResponseType<DeviceInstanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceInstanceResponse>> DisconnectInstanceAsync(
        string deviceInstanceId,
        ApiStatusChangeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .DisconnectInstanceAsync(
                deviceInstanceId,
                new ApplicationStatusChangeRequest(request.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("instances/{deviceInstanceId}/faults")]
    [ProducesResponseType<DeviceInstanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceInstanceResponse>> FaultInstanceAsync(
        string deviceInstanceId,
        ApiStatusChangeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .FaultInstanceAsync(
                deviceInstanceId,
                new ApplicationStatusChangeRequest(request.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("instances/{deviceInstanceId}/fault-reset")]
    [ProducesResponseType<DeviceInstanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceInstanceResponse>> ResetFaultAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService
            .ResetFaultAsync(deviceInstanceId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    private static ApplicationCreateDefinitionRequest ToApplicationRequest(ApiCreateDefinitionRequest request)
    {
        return new ApplicationCreateDefinitionRequest(
            request.DeviceDefinitionId!,
            request.DisplayName!,
            request.PluginId!,
            request.Capabilities!
                .Select(capability => new ApplicationCreateCapabilityRequest(
                    capability.CapabilityId!,
                    capability.DisplayName!))
                .ToArray(),
            request.Commands!
                .Select(command => new ApplicationCreateCommandRequest(
                    command.CommandDefinitionId!,
                    command.CapabilityId!,
                    command.CommandName!,
                    command.InputSchema,
                    command.OutputSchema,
                    command.TimeoutSeconds!.Value,
                    command.MaxRetries ?? 0))
                .ToArray());
    }

    private static ApplicationRegisterInstanceRequest ToApplicationRequest(ApiRegisterInstanceRequest request)
    {
        return new ApplicationRegisterInstanceRequest(
            request.DeviceInstanceId!,
            request.DeviceDefinitionId!,
            request.StationId!,
            request.DisplayName!,
            request.Protocol!,
            request.Address!);
    }

    private static DeviceDefinitionResponse ToResponse(DeviceDefinitionDetails definition)
    {
        return new DeviceDefinitionResponse(
            definition.DeviceDefinitionId,
            definition.DisplayName,
            definition.PluginId,
            definition.CreatedAtUtc,
            definition.Capabilities
                .Select(capability => new DeviceCapabilityResponse(
                    capability.CapabilityId,
                    capability.DisplayName))
                .ToArray(),
            definition.Commands
                .Select(command => new DeviceCommandDefinitionResponse(
                    command.CommandDefinitionId,
                    command.CapabilityId,
                    command.CommandName,
                    command.InputSchema,
                    command.OutputSchema,
                    command.TimeoutSeconds,
                    command.MaxRetries))
                .ToArray());
    }

    private static DeviceInstanceResponse ToResponse(DeviceInstanceDetails instance)
    {
        return new DeviceInstanceResponse(
            instance.DeviceInstanceId,
            instance.DeviceDefinitionId,
            instance.StationId,
            instance.DisplayName,
            instance.Protocol,
            instance.Address,
            instance.RegisteredAtUtc,
            instance.Status,
            instance.ConnectedAtUtc,
            instance.LastDisconnectedAtUtc,
            instance.FaultReason);
    }

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }

    private static Dictionary<string, string[]> Validate(ApiCreateDefinitionRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.DeviceDefinitionId), request.DeviceDefinitionId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.PluginId), request.PluginId);

        if (request.Capabilities is null)
        {
            errors[nameof(request.Capabilities)] = ["Capabilities collection is required."];
        }
        else
        {
            var index = 0;
            foreach (var capability in request.Capabilities)
            {
                AddRequired(errors, $"Capabilities[{index}].{nameof(capability.CapabilityId)}", capability.CapabilityId);
                AddRequired(errors, $"Capabilities[{index}].{nameof(capability.DisplayName)}", capability.DisplayName);
                index++;
            }
        }

        if (request.Commands is null)
        {
            errors[nameof(request.Commands)] = ["Commands collection is required."];
        }
        else
        {
            var index = 0;
            foreach (var command in request.Commands)
            {
                var prefix = $"Commands[{index}]";
                AddRequired(errors, $"{prefix}.{nameof(command.CommandDefinitionId)}", command.CommandDefinitionId);
                AddRequired(errors, $"{prefix}.{nameof(command.CapabilityId)}", command.CapabilityId);
                AddRequired(errors, $"{prefix}.{nameof(command.CommandName)}", command.CommandName);
                if (command.TimeoutSeconds is null or <= 0)
                {
                    errors[$"{prefix}.{nameof(command.TimeoutSeconds)}"] = ["Value must be positive."];
                }

                index++;
            }
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ApiRegisterInstanceRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.DeviceInstanceId), request.DeviceInstanceId);
        AddRequired(errors, nameof(request.DeviceDefinitionId), request.DeviceDefinitionId);
        AddRequired(errors, nameof(request.StationId), request.StationId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.Protocol), request.Protocol);
        AddRequired(errors, nameof(request.Address), request.Address);

        return errors;
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
