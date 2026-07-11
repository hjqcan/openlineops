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
    ExternalTestProgramAdapterDocument[] ExternalTestProgramAdapters,
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
    string ConfigurationSnapshotId);

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

internal sealed record ExternalTestProgramAdapterDocument(
    string AdapterId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string? Executable,
    string? ProviderKey,
    string[] ArgumentTemplates,
    ExternalTestProgramInputMappingDocument[] InputMappings,
    ExternalTestProgramResultMappingDocument[] ResultMappings,
    ExternalTestProgramOutcomeMappingDocument OutcomeMapping,
    long TimeoutMilliseconds);

internal sealed record ExternalTestProgramInputMappingDocument(string Source, string Target);

internal sealed record ExternalTestProgramResultMappingDocument(string SourcePath, string TargetKey);

internal sealed record ExternalTestProgramOutcomeMappingDocument(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);
