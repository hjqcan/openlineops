namespace OpenLineOps.Production.Api.Models;

public sealed record SaveProductionLineRequest(
    string? LineDefinitionId,
    string? DisplayName,
    string? TopologyId,
    DutModelRequest? DutModel,
    IReadOnlyCollection<WorkstationRequest?>? Workstations,
    IReadOnlyCollection<ProcessStageRequest?>? Stages,
    IReadOnlyCollection<ExternalTestProgramAdapterRequest?>? ExternalTestProgramAdapters);

public sealed record DutModelRequest(string? DutModelId, string? ModelCode, string? IdentityInputKey);

public sealed record WorkstationRequest(
    string? WorkstationId,
    string? DisplayName,
    string? TopologyStationNodeId,
    string? TopologySystemModuleId);

public sealed record ProcessStageRequest(
    string? StageId,
    int? Sequence,
    string? DisplayName,
    string? WorkstationId,
    string? FlowDefinitionId,
    string? ExternalTestProgramAdapterId);

public sealed record ExternalTestProgramAdapterRequest(
    string? AdapterId,
    string? DisplayName,
    string? CapabilityId,
    string? CommandName,
    string? Executable,
    string? ProviderKey,
    IReadOnlyCollection<string?>? ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramInputMappingRequest?>? InputMappings,
    IReadOnlyCollection<ExternalTestProgramResultMappingRequest?>? ResultMappings,
    long? TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingRequest(string? Source, string? Target);

public sealed record ExternalTestProgramResultMappingRequest(string? SourcePath, string? TargetKey);

public sealed record ProductionLineResponse(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    DutModelResponse DutModel,
    IReadOnlyCollection<WorkstationResponse> Workstations,
    IReadOnlyCollection<ProcessStageResponse> Stages,
    IReadOnlyCollection<ExternalTestProgramAdapterResponse> ExternalTestProgramAdapters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductionLineSummaryResponse(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    string DutModelCode,
    int StageCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record DutModelResponse(string DutModelId, string ModelCode, string IdentityInputKey);

public sealed record WorkstationResponse(
    string WorkstationId,
    string DisplayName,
    string TopologyStationNodeId,
    string TopologySystemModuleId);

public sealed record ProcessStageResponse(
    string StageId,
    int Sequence,
    string DisplayName,
    string WorkstationId,
    string FlowDefinitionId,
    string? ExternalTestProgramAdapterId,
    string? NextStageId);

public sealed record ExternalTestProgramAdapterResponse(
    string AdapterId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string LaunchKind,
    string? Executable,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramInputMappingResponse> InputMappings,
    IReadOnlyCollection<ExternalTestProgramResultMappingResponse> ResultMappings,
    long TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingResponse(string Source, string Target);

public sealed record ExternalTestProgramResultMappingResponse(string SourcePath, string TargetKey);
