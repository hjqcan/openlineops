namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record ProductionLineDefinitionDetails(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelDetails ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<OperationDefinitionDetails> Operations,
    IReadOnlyCollection<RouteTransitionDetails> Transitions,
    IReadOnlyCollection<LineControllerAuthorizationDetails> LineControllerAuthorizations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductionLineDefinitionSummary(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    string ProductModelCode,
    int OperationCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductModelDetails(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record OperationDefinitionDetails(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    IReadOnlyCollection<OperationResourceBindingDetails> Resources);

public sealed record OperationResourceBindingDetails(
    string BindingId,
    string Kind,
    string TopologyTargetId,
    string Resolution);

public sealed record LineControllerAuthorizationDetails(
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

public sealed record RouteTransitionDetails(
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
