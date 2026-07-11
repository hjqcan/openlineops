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
    IReadOnlyCollection<ExternalTestProgramAdapterRequest?>? ExternalTestProgramAdapters);

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
    string? ConfigurationSnapshotId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RouteTransitionRequest(
    string? TransitionId,
    string? SourceOperationId,
    string? TargetOperationId,
    string? Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);

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
    ExternalTestProgramOutcomeMappingRequest? OutcomeMapping,
    long? TimeoutMilliseconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalTestProgramInputMappingRequest(string? Source, string? Target);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalTestProgramResultMappingRequest(string? SourcePath, string? TargetKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalTestProgramOutcomeMappingRequest(
    string? SourcePath,
    string? PassedToken,
    string? FailedToken,
    string? AbortedToken);

public sealed record ProductionLineResponse(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelResponse ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<OperationDefinitionResponse> Operations,
    IReadOnlyCollection<RouteTransitionResponse> Transitions,
    IReadOnlyCollection<ExternalTestProgramAdapterResponse> ExternalTestProgramAdapters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

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

public sealed record OperationDefinitionResponse(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId);

public sealed record RouteTransitionResponse(
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
    ExternalTestProgramOutcomeMappingResponse OutcomeMapping,
    long TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingResponse(string Source, string Target);

public sealed record ExternalTestProgramResultMappingResponse(string SourcePath, string TargetKey);

public sealed record ExternalTestProgramOutcomeMappingResponse(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);
