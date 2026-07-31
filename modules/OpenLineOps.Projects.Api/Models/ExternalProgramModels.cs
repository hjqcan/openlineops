using System.Text.Json.Serialization;

namespace OpenLineOps.Projects.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveExternalProgramResourceApiRequest(
    string? ResourceId,
    string? DisplayName,
    string? CapabilityId,
    string? CommandName,
    string? LaunchKind,
    string? EntryPoint,
    string? ProviderKind,
    string? ProviderKey,
    IReadOnlyCollection<string?>? ArgumentTemplates,
    IReadOnlyCollection<ExternalProgramInputMappingApiRequest?>? InputMappings,
    IReadOnlyCollection<ExternalProgramResultMappingApiRequest?>? ResultMappings,
    ExternalProgramOutcomeMappingApiRequest? OutcomeMapping,
    ExternalProgramPermissionProfileApiRequest? PermissionProfile,
    ExternalProgramExecutionLimitsApiRequest? ExecutionLimits);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramInputMappingApiRequest(string? Source, string? Target);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramResultMappingApiRequest(
    string? SourcePath,
    string? TargetKey,
    string? ValueKind);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramOutcomeMappingApiRequest(
    string? SourcePath,
    string? PassedToken,
    string? FailedToken,
    string? AbortedToken);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramPermissionProfileApiRequest(
    string? ProfileName,
    bool? NetworkAccessAllowed,
    IReadOnlyCollection<string?>? AllowedEnvironmentVariables);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramExecutionLimitsApiRequest(
    long? TimeoutMilliseconds,
    int? MaximumProcessCount,
    long? MaximumWorkingSetBytes,
    long? MaximumCpuTimeMilliseconds,
    int? MaximumStandardOutputBytes,
    int? MaximumStandardErrorBytes,
    int? MaximumArtifactCount,
    long? MaximumArtifactBytes,
    long? MaximumTotalArtifactBytes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramTrialApiRequest(
    IReadOnlyDictionary<string, ExternalProgramTrialInputApiRequest?>? Inputs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramDefinitionTrialApiRequest(
    SaveExternalProgramResourceApiRequest? Definition,
    IReadOnlyDictionary<string, ExternalProgramTrialInputApiRequest?>? Inputs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramTrialInputApiRequest(string? Kind, string? CanonicalValue);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalProgramUploadManifestItemApiRequest(
    string? FieldName,
    string? ResourceRelativePath,
    long? SizeBytes,
    string? Sha256);

public sealed record ExternalProgramResourceApiResponse(
    string ResourceId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string LaunchKind,
    string? EntryPoint,
    string? ProviderKind,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalProgramInputMappingApiResponse> InputMappings,
    IReadOnlyCollection<ExternalProgramResultMappingApiResponse> ResultMappings,
    ExternalProgramOutcomeMappingApiResponse OutcomeMapping,
    ExternalProgramPermissionProfileApiResponse PermissionProfile,
    ExternalProgramExecutionLimitsApiResponse ExecutionLimits,
    IReadOnlyCollection<ExternalProgramFileApiResponse> Files,
    string ContentSha256,
    DateTimeOffset UpdatedAtUtc,
    string Revision);

public sealed record ExternalProgramInputMappingApiResponse(string Source, string Target);

public sealed record ExternalProgramResultMappingApiResponse(
    string SourcePath,
    string TargetKey,
    string ValueKind);

public sealed record ExternalProgramOutcomeMappingApiResponse(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);

public sealed record ExternalProgramPermissionProfileApiResponse(
    string ProfileName,
    bool NetworkAccessAllowed,
    IReadOnlyCollection<string> AllowedEnvironmentVariables);

public sealed record ExternalProgramExecutionLimitsApiResponse(
    long TimeoutMilliseconds,
    int MaximumProcessCount,
    long MaximumWorkingSetBytes,
    long MaximumCpuTimeMilliseconds,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int MaximumArtifactCount,
    long MaximumArtifactBytes,
    long MaximumTotalArtifactBytes);

public sealed record ExternalProgramFileApiResponse(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ExternalProgramTrialApiResponse(
    string ResourceId,
    string LaunchKind,
    string ContentSha256,
    string ExecutionStatus,
    string Judgement,
    string? ResultPayload,
    string? FailureReason,
    IReadOnlyCollection<ExternalProgramTrialArtifactApiResponse> Artifacts);

public sealed record ExternalProgramTrialArtifactApiResponse(
    string Name,
    string Kind,
    string? MediaType,
    long SizeBytes,
    string Sha256);
