using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Application.Configuration;

public sealed class DeviceConfigurationService : IDeviceConfigurationService
{
    private readonly IDeviceDefinitionRepository _definitionRepository;
    private readonly IDeviceInstanceRepository _instanceRepository;
    private readonly IClock _clock;

    public DeviceConfigurationService(
        IDeviceDefinitionRepository definitionRepository,
        IDeviceInstanceRepository instanceRepository,
        IClock clock)
    {
        _definitionRepository = definitionRepository;
        _instanceRepository = instanceRepository;
        _clock = clock;
    }

    public async Task<Result<DeviceDefinitionDetails>> CreateDefinitionAsync(
        CreateDeviceDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateDefinitionRequest(request);
        if (validation is not null)
        {
            return Result.Failure<DeviceDefinitionDetails>(validation);
        }

        try
        {
            var definitionId = new DeviceDefinitionId(request.DeviceDefinitionId);
            var existing = await _definitionRepository
                .GetByIdAsync(definitionId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return Result.Failure<DeviceDefinitionDetails>(ApplicationError.Conflict(
                    "Devices.DefinitionAlreadyExists",
                    $"Device definition {definitionId} already exists."));
            }

            var definition = DeviceDefinition.Create(
                definitionId,
                request.DisplayName,
                request.PluginId,
                _clock.UtcNow);

            foreach (var capabilityRequest in request.Capabilities)
            {
                var result = definition.AddCapability(DeviceCapability.Create(
                    new DeviceCapabilityId(capabilityRequest.CapabilityId),
                    capabilityRequest.DisplayName));
                if (!result.Succeeded)
                {
                    return Result.Failure<DeviceDefinitionDetails>(
                        ApplicationError.Validation(result.Code, result.Message));
                }
            }

            foreach (var commandRequest in request.Commands)
            {
                var result = definition.AddCommand(DeviceCommandDefinition.Create(
                    new DeviceCommandDefinitionId(commandRequest.CommandDefinitionId),
                    new DeviceCapabilityId(commandRequest.CapabilityId),
                    commandRequest.CommandName,
                    commandRequest.InputSchema,
                    commandRequest.OutputSchema,
                    TimeSpan.FromSeconds(commandRequest.TimeoutSeconds),
                    commandRequest.MaxRetries));
                if (!result.Succeeded)
                {
                    return Result.Failure<DeviceDefinitionDetails>(
                        ApplicationError.Validation(result.Code, result.Message));
                }
            }

            await _definitionRepository.SaveAsync(definition, cancellationToken).ConfigureAwait(false);

            return Result.Success(DeviceConfigurationMapper.ToDetails(definition));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<DeviceDefinitionDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<DeviceDefinitionDetails>> GetDefinitionAsync(
        string deviceDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await FindDefinitionAsync(deviceDefinitionId, cancellationToken).ConfigureAwait(false);

        return definition is null
            ? Result.Failure<DeviceDefinitionDetails>(DefinitionNotFound(deviceDefinitionId))
            : Result.Success(DeviceConfigurationMapper.ToDetails(definition));
    }

    public async Task<Result<IReadOnlyCollection<DeviceDefinitionDetails>>> ListDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await _definitionRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var details = definitions
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .Select(DeviceConfigurationMapper.ToDetails)
            .ToArray();

        return Result.Success<IReadOnlyCollection<DeviceDefinitionDetails>>(details);
    }

    public async Task<Result<DeviceInstanceDetails>> RegisterInstanceAsync(
        RegisterDeviceInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateRegisterInstanceRequest(request);
        if (validation is not null)
        {
            return Result.Failure<DeviceInstanceDetails>(validation);
        }

        try
        {
            var instanceId = new DeviceInstanceId(request.DeviceInstanceId);
            var definitionId = new DeviceDefinitionId(request.DeviceDefinitionId);
            var existing = await _instanceRepository
                .GetByIdAsync(instanceId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return Result.Failure<DeviceInstanceDetails>(ApplicationError.Conflict(
                    "Devices.InstanceAlreadyExists",
                    $"Device instance {instanceId} already exists."));
            }

            var definition = await _definitionRepository
                .GetByIdAsync(definitionId, cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
            {
                return Result.Failure<DeviceInstanceDetails>(DefinitionNotFound(request.DeviceDefinitionId));
            }

            var instance = DeviceInstance.Register(
                instanceId,
                definitionId,
                request.StationId,
                request.DisplayName,
                new DeviceEndpoint(request.Protocol, request.Address),
                _clock.UtcNow);

            await _instanceRepository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

            return Result.Success(DeviceConfigurationMapper.ToDetails(instance));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<DeviceInstanceDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<DeviceInstanceDetails>> GetInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await FindInstanceAsync(deviceInstanceId, cancellationToken).ConfigureAwait(false);

        return instance is null
            ? Result.Failure<DeviceInstanceDetails>(InstanceNotFound(deviceInstanceId))
            : Result.Success(DeviceConfigurationMapper.ToDetails(instance));
    }

    public async Task<Result<IReadOnlyCollection<DeviceInstanceDetails>>> ListInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        var instances = await _instanceRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var details = instances
            .OrderBy(instance => instance.Id.Value, StringComparer.Ordinal)
            .Select(DeviceConfigurationMapper.ToDetails)
            .ToArray();

        return Result.Success<IReadOnlyCollection<DeviceInstanceDetails>>(details);
    }

    public async Task<Result<DeviceInstanceDetails>> ConnectInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken = default)
    {
        return await ChangeInstanceAsync(
            deviceInstanceId,
            instance =>
            {
                var requested = instance.RequestConnection();
                if (!requested.Succeeded)
                {
                    return requested;
                }

                return instance.ConfirmConnected(_clock.UtcNow);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DeviceInstanceDetails>> DisconnectInstanceAsync(
        string deviceInstanceId,
        DeviceStatusChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await ChangeInstanceAsync(
            deviceInstanceId,
            instance => instance.Disconnect(_clock.UtcNow, request.Reason ?? "Disconnected from device management."),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DeviceInstanceDetails>> FaultInstanceAsync(
        string deviceInstanceId,
        DeviceStatusChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await ChangeInstanceAsync(
            deviceInstanceId,
            instance => instance.MarkFaulted(request.Reason ?? "Faulted from device management."),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DeviceInstanceDetails>> ResetFaultAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken = default)
    {
        return await ChangeInstanceAsync(
            deviceInstanceId,
            instance => instance.ResetFault(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<DeviceInstanceDetails>> ChangeInstanceAsync(
        string deviceInstanceId,
        Func<DeviceInstance, Domain.Operations.DeviceOperationResult> change,
        CancellationToken cancellationToken)
    {
        var instance = await FindInstanceAsync(deviceInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Failure<DeviceInstanceDetails>(InstanceNotFound(deviceInstanceId));
        }

        var result = change(instance);
        if (!result.Succeeded)
        {
            return Result.Failure<DeviceInstanceDetails>(ApplicationError.Validation(
                result.Code,
                result.Message));
        }

        await _instanceRepository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        return Result.Success(DeviceConfigurationMapper.ToDetails(instance));
    }

    private async Task<DeviceDefinition?> FindDefinitionAsync(
        string deviceDefinitionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceDefinitionId))
        {
            return null;
        }

        return await _definitionRepository
            .GetByIdAsync(new DeviceDefinitionId(deviceDefinitionId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DeviceInstance?> FindInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId))
        {
            return null;
        }

        return await _instanceRepository
            .GetByIdAsync(new DeviceInstanceId(deviceInstanceId), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ApplicationError? ValidateCreateDefinitionRequest(CreateDeviceDefinitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceDefinitionId))
        {
            return ApplicationError.Validation("Devices.DefinitionIdRequired", "DeviceDefinitionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ApplicationError.Validation("Devices.DisplayNameRequired", "DisplayName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            return ApplicationError.Validation("Devices.PluginIdRequired", "PluginId is required.");
        }

        if (request.Capabilities is null || request.Capabilities.Count == 0)
        {
            return ApplicationError.Validation(
                "Devices.CapabilitiesRequired",
                "At least one capability is required.");
        }

        if (request.Commands is null)
        {
            return ApplicationError.Validation("Devices.CommandsRequired", "Commands collection is required.");
        }

        if (request.Commands.Any(command => command.TimeoutSeconds <= 0))
        {
            return ApplicationError.Validation(
                "Devices.CommandTimeoutInvalid",
                "Command timeouts must be positive.");
        }

        return null;
    }

    private static ApplicationError? ValidateRegisterInstanceRequest(RegisterDeviceInstanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceInstanceId))
        {
            return ApplicationError.Validation("Devices.InstanceIdRequired", "DeviceInstanceId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DeviceDefinitionId))
        {
            return ApplicationError.Validation("Devices.DefinitionIdRequired", "DeviceDefinitionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.StationId))
        {
            return ApplicationError.Validation("Devices.StationIdRequired", "StationId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ApplicationError.Validation("Devices.DisplayNameRequired", "DisplayName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Protocol))
        {
            return ApplicationError.Validation("Devices.ProtocolRequired", "Protocol is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Address))
        {
            return ApplicationError.Validation("Devices.AddressRequired", "Address is required.");
        }

        return null;
    }

    private static ApplicationError DefinitionNotFound(string deviceDefinitionId)
    {
        return ApplicationError.NotFound(
            "Devices.DefinitionNotFound",
            $"Device definition {deviceDefinitionId} was not found.");
    }

    private static ApplicationError InstanceNotFound(string deviceInstanceId)
    {
        return ApplicationError.NotFound(
            "Devices.InstanceNotFound",
            $"Device instance {deviceInstanceId} was not found.");
    }

    private static ApplicationError InvalidInput(ArgumentException exception)
    {
        return ApplicationError.Validation(
            "Devices.InvalidInput",
            exception.Message);
    }
}
