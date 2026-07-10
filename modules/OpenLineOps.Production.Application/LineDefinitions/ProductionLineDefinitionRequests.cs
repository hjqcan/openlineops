namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record SaveProductionLineDefinitionRequest(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    DutModelRequest DutModel,
    IReadOnlyCollection<WorkstationRequest> Workstations,
    IReadOnlyCollection<ProcessStageRequest> Stages,
    IReadOnlyCollection<ExternalTestProgramAdapterRequest> ExternalTestProgramAdapters);

public sealed record DutModelRequest(
    string DutModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record WorkstationRequest(
    string WorkstationId,
    string DisplayName,
    string StationSystemId);

public sealed record ProcessStageRequest(
    string StageId,
    int Sequence,
    string DisplayName,
    string WorkstationId,
    string FlowDefinitionId,
    string? ExternalTestProgramAdapterId);

public sealed record ExternalTestProgramAdapterRequest(
    string AdapterId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string? Executable,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramInputMappingRequest> InputMappings,
    IReadOnlyCollection<ExternalTestProgramResultMappingRequest> ResultMappings,
    long TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingRequest(string Source, string Target);

public sealed record ExternalTestProgramResultMappingRequest(string SourcePath, string TargetKey);
