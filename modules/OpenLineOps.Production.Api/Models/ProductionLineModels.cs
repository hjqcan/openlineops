using System.Text.Json.Serialization;

namespace OpenLineOps.Production.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveProductionLineRequest(
    string? LineDefinitionId,
    string? DisplayName,
    string? TopologyId,
    DutModelRequest? DutModel,
    IReadOnlyCollection<WorkstationRequest?>? Workstations,
    IReadOnlyCollection<ProcessStageRequest?>? Stages,
    IReadOnlyCollection<ExternalTestProgramAdapterRequest?>? ExternalTestProgramAdapters);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DutModelRequest(string? DutModelId, string? ModelCode, string? IdentityInputKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkstationRequest(
    string? WorkstationId,
    string? DisplayName,
    string? StationSystemId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcessStageRequest(
    string? StageId,
    int? Sequence,
    string? DisplayName,
    string? WorkstationId,
    string? FlowDefinitionId,
    string? ExternalTestProgramAdapterId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalTestProgramInputMappingRequest(string? Source, string? Target);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
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
    string StationSystemId);

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
