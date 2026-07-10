namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record ProductionLineDefinitionDetails(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    DutModelDetails DutModel,
    IReadOnlyCollection<WorkstationDetails> Workstations,
    IReadOnlyCollection<ProcessStageDetails> Stages,
    IReadOnlyCollection<ExternalTestProgramAdapterDetails> ExternalTestProgramAdapters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductionLineDefinitionSummary(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    string DutModelCode,
    int StageCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record DutModelDetails(
    string DutModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record WorkstationDetails(
    string WorkstationId,
    string DisplayName,
    string StationSystemId);

public sealed record ProcessStageDetails(
    string StageId,
    int Sequence,
    string DisplayName,
    string WorkstationId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    string? ExternalTestProgramAdapterId,
    string? NextStageId);

public sealed record ExternalTestProgramAdapterDetails(
    string AdapterId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string LaunchKind,
    string? Executable,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramInputMappingDetails> InputMappings,
    IReadOnlyCollection<ExternalTestProgramResultMappingDetails> ResultMappings,
    ExternalTestProgramOutcomeMappingDetails OutcomeMapping,
    long TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingDetails(string Source, string Target);

public sealed record ExternalTestProgramResultMappingDetails(string SourcePath, string TargetKey);

public sealed record ExternalTestProgramOutcomeMappingDetails(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);
