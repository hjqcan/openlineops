using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record SaveProductionLineDefinitionRequest(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelRequest ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<OperationDefinitionRequest> Operations,
    IReadOnlyCollection<RouteTransitionRequest> Transitions,
    IReadOnlyCollection<LineControllerAuthorizationRequest> LineControllerAuthorizations);

public sealed record ProductModelRequest(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record OperationDefinitionRequest(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    IReadOnlyCollection<OperationResourceBindingRequest> Resources);

public sealed record OperationResourceBindingRequest(
    string BindingId,
    OperationResourceKind Kind,
    string TopologyTargetId,
    OperationResourceResolution Resolution);

public sealed record LineControllerAuthorizationRequest(
    string AuthorizationId,
    string OperationId,
    string ActionId,
    string ControllerSystemId,
    string ControllerBindingId,
    string ControllerCapabilityId,
    string ControllerAction,
    string TargetStationSystemId,
    string TargetSystemId,
    string TargetBindingId,
    string TargetCapabilityId,
    string TargetAction);

public sealed record RouteTransitionRequest(
    string TransitionId,
    string SourceOperationId,
    string? TargetOperationId,
    TerminalDisposition? TerminalDisposition,
    RouteTransitionKind Kind,
    RouteJudgement? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    ProductionContextValueKind? ExpectedOutputKind,
    string? ExpectedOutputValue);
