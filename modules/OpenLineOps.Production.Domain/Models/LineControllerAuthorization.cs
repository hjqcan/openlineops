using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed record LineControllerAuthorization
{
    public LineControllerAuthorization(
        LineControllerAuthorizationId id,
        OperationDefinitionId operationId,
        string actionId,
        string controllerSystemId,
        string controllerBindingId,
        string controllerCapabilityId,
        string controllerAction,
        string targetStationSystemId,
        string targetSystemId,
        string targetBindingId,
        string targetCapabilityId,
        string targetAction)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        ActionId = ProductionIdGuard.NotBlank(actionId, nameof(actionId));
        ControllerSystemId = ProductionIdGuard.NotBlank(controllerSystemId, nameof(controllerSystemId));
        ControllerBindingId = ProductionIdGuard.NotBlank(controllerBindingId, nameof(controllerBindingId));
        ControllerCapabilityId = ProductionIdGuard.NotBlank(controllerCapabilityId, nameof(controllerCapabilityId));
        ControllerAction = ProductionIdGuard.NotBlank(controllerAction, nameof(controllerAction));
        TargetStationSystemId = ProductionIdGuard.NotBlank(targetStationSystemId, nameof(targetStationSystemId));
        TargetSystemId = ProductionIdGuard.NotBlank(targetSystemId, nameof(targetSystemId));
        TargetBindingId = ProductionIdGuard.NotBlank(targetBindingId, nameof(targetBindingId));
        TargetCapabilityId = ProductionIdGuard.NotBlank(targetCapabilityId, nameof(targetCapabilityId));
        TargetAction = ProductionIdGuard.NotBlank(targetAction, nameof(targetAction));
    }

    public LineControllerAuthorizationId Id { get; }

    public OperationDefinitionId OperationId { get; }

    public string ActionId { get; }

    public string ControllerSystemId { get; }

    public string ControllerBindingId { get; }

    public string ControllerCapabilityId { get; }

    public string ControllerAction { get; }

    public string TargetStationSystemId { get; }

    public string TargetSystemId { get; }

    public string TargetBindingId { get; }

    public string TargetCapabilityId { get; }

    public string TargetAction { get; }
}
