using System.Text.Json.Serialization;

namespace OpenLineOps.Production.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveProductionLineRequest(
    string? LineDefinitionId,
    string? DisplayName,
    string? TopologyId,
    ProductModelRequest? ProductModel,
    string? EntryOperationId,
    IReadOnlyCollection<OperationDefinitionRequest?>? Operations,
    IReadOnlyCollection<RouteTransitionRequest?>? Transitions,
    IReadOnlyCollection<LineControllerAuthorizationRequest?>? LineControllerAuthorizations,
    ProductionRouteLayoutRequest? RouteLayout);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionRouteLayoutRequest(
    IReadOnlyCollection<OperationCanvasPositionRequest?>? OperationPositions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationCanvasPositionRequest(
    string? OperationId,
    int X,
    int Y);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductModelRequest(
    string? ProductModelId,
    string? ModelCode,
    string? IdentityInputKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationDefinitionRequest(
    string? OperationId,
    string? DisplayName,
    string? StationSystemId,
    string? FlowDefinitionId,
    string? ConfigurationSnapshotId,
    IReadOnlyCollection<OperationResourceBindingRequest?>? Resources,
    IReadOnlyCollection<OperationInputMappingRequest?>? InputMappings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationInputMappingRequest(
    string? TargetInputKey,
    string? SourceOperationId,
    string? SourceOutputKey,
    string? ExpectedValueKind);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationResourceBindingRequest(
    string? BindingId,
    string? Kind,
    string? TopologyTargetId,
    string? Resolution);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LineControllerAuthorizationRequest(
    string? AuthorizationId,
    string? OperationId,
    string? ActionId,
    string? ControllerSystemId,
    string? ControllerBindingId,
    string? ControllerCapabilityId,
    string? ControllerAction,
    string? TargetStationSystemId,
    string? TargetSystemId,
    string? TargetBindingId,
    string? TargetCapabilityId,
    string? TargetAction);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RouteTransitionRequest(
    string? TransitionId,
    string? SourceOperationId,
    string? TargetOperationId,
    string? TerminalDisposition,
    string? Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);

public sealed record ProductionLineResponse(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelResponse ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<OperationDefinitionResponse> Operations,
    IReadOnlyCollection<RouteTransitionResponse> Transitions,
    IReadOnlyCollection<LineControllerAuthorizationResponse> LineControllerAuthorizations,
    ProductionRouteLayoutResponse RouteLayout,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Revision);

public sealed record ProductionLineSummaryResponse(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    string ProductModelCode,
    int OperationCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductModelResponse(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record ProductionRouteLayoutResponse(
    IReadOnlyCollection<OperationCanvasPositionResponse> OperationPositions);

public sealed record OperationCanvasPositionResponse(
    string OperationId,
    int X,
    int Y);

public sealed record OperationDefinitionResponse(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    IReadOnlyCollection<OperationResourceBindingResponse> Resources,
    IReadOnlyCollection<OperationInputMappingResponse> InputMappings);

public sealed record OperationInputMappingResponse(
    string TargetInputKey,
    string SourceOperationId,
    string SourceOutputKey,
    string ExpectedValueKind);

public sealed record OperationResourceBindingResponse(
    string BindingId,
    string Kind,
    string TopologyTargetId,
    string Resolution);

public sealed record LineControllerAuthorizationResponse(
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

public sealed record RouteTransitionResponse(
    string TransitionId,
    string SourceOperationId,
    string? TargetOperationId,
    string? TerminalDisposition,
    string Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);
