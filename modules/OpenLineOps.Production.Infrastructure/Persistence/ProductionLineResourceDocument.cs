using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Production.Infrastructure.Persistence;

internal sealed record ProductionLineResourceDocument(
    string SchemaVersion,
    string ResourceKind,
    string ApplicationId,
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    DutModelDocument DutModel,
    WorkstationDocument[] Workstations,
    ProcessStageDocument[] Stages,
    ExternalTestProgramAdapterDocument[] ExternalTestProgramAdapters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchemaVersion = ApplicationResourceSchemaVersions.ProductionLine;

    public const string Kind = "OpenLineOps.ProductionLine";
}

internal sealed record DutModelDocument(
    string DutModelId,
    string ModelCode,
    string IdentityInputKey);

internal sealed record WorkstationDocument(
    string WorkstationId,
    string DisplayName,
    string StationSystemId);

internal sealed record ProcessStageDocument(
    string StageId,
    int Sequence,
    string DisplayName,
    string WorkstationId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    string? ExternalTestProgramAdapterId);

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
