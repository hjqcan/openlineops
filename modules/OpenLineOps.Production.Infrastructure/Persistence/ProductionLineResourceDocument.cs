using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Production.Infrastructure.Persistence;

internal sealed record ProductionLineResourceDocument(
    string SchemaVersion,
    string ResourceKind,
    string ApplicationId,
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelDocument ProductModel,
    string EntryOperationId,
    OperationDefinitionDocument[] Operations,
    RouteTransitionDocument[] Transitions,
    LineControllerAuthorizationDocument[] LineControllerAuthorizations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchemaVersion = ApplicationResourceSchemaVersions.ProductionLine;

    public const string Kind = "OpenLineOps.ProductionLine";
}

internal sealed record ProductModelDocument(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

internal sealed record OperationDefinitionDocument(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    OperationResourceBindingDocument[] Resources);

internal sealed record OperationResourceBindingDocument(
    string BindingId,
    string Kind,
    string TopologyTargetId,
    string Resolution);

internal sealed record LineControllerAuthorizationDocument(
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

internal sealed record RouteTransitionDocument(
    string TransitionId,
    string SourceOperationId,
    string TargetOperationId,
    string Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);
