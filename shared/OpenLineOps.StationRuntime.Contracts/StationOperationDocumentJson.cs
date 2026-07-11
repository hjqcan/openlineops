using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.StationRuntime.Contracts;

public static class StationOperationDocumentJson
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static void Validate(StationOperationRequestDocument request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                request.Schema,
                StationOperationDocumentContract.RequestSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station operation request schema is invalid.");
        }

        RequireGuid(request.JobId, nameof(request.JobId));
        RequireGuid(request.ProductionRunId, nameof(request.ProductionRunId));
        RequireGuid(request.ProductionUnitId, nameof(request.ProductionUnitId));
        RequireGuid(request.RuntimeSessionId, nameof(request.RuntimeSessionId));
        RequireCanonical(request.IdempotencyKey, nameof(request.IdempotencyKey));
        RequireCanonical(request.AgentId, nameof(request.AgentId));
        RequireCanonical(request.StationId, nameof(request.StationId));
        RequireCanonical(request.StationSystemId, nameof(request.StationSystemId));
        RequireCanonical(request.ProductionLineDefinitionId, nameof(request.ProductionLineDefinitionId));
        RequireCanonical(request.TopologyId, nameof(request.TopologyId));
        RequireCanonical(request.ActorId, nameof(request.ActorId));
        RequireCanonical(request.OperationRunId, nameof(request.OperationRunId));
        RequireCanonical(request.ProductModelId, nameof(request.ProductModelId));
        RequireCanonical(
            request.ProductionUnitIdentityInputKey,
            nameof(request.ProductionUnitIdentityInputKey));
        RequireCanonical(
            request.ProductionUnitIdentityValue,
            nameof(request.ProductionUnitIdentityValue));
        RequireOptionalCanonical(request.LotId, nameof(request.LotId));
        RequireOptionalCanonical(request.CarrierId, nameof(request.CarrierId));
        RequireCanonical(request.ProjectId, nameof(request.ProjectId));
        RequireCanonical(request.ApplicationId, nameof(request.ApplicationId));
        RequireCanonical(request.ProjectSnapshotId, nameof(request.ProjectSnapshotId));
        RequireSha256(request.PackageContentSha256, nameof(request.PackageContentSha256));
        RequireCanonical(request.PackageContentDirectory, nameof(request.PackageContentDirectory), 4096);
        if (!Path.IsPathFullyQualified(request.PackageContentDirectory))
        {
            throw new InvalidDataException("Package content directory must be an absolute path.");
        }

        RequireCanonical(request.OperationId, nameof(request.OperationId));
        RequireCanonical(request.FlowDefinitionId, nameof(request.FlowDefinitionId));
        RequireCanonical(request.FlowVersionId, nameof(request.FlowVersionId));
        RequireCanonical(request.ConfigurationSnapshotId, nameof(request.ConfigurationSnapshotId));
        RequireCanonical(request.RecipeSnapshotId, nameof(request.RecipeSnapshotId));
        ArgumentNullException.ThrowIfNull(request.ResourceFenceAuthority);
        RequirePipeName(request.ResourceFenceAuthority.PipeName);
        RequireSha256(request.ResourceFenceAuthority.AccessToken, nameof(request.ResourceFenceAuthority.AccessToken));
        if (request.OperationAttempt <= 0)
        {
            throw new InvalidDataException("Operation attempt must be positive.");
        }

        RequireUtc(request.RequestedAtUtc, nameof(request.RequestedAtUtc));
        if (request.Inputs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Station operation inputs must be one JSON object.");
        }

        ValidateResourceFences(request.ResourceFences);
    }

    public static void Validate(StationResourceFenceValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                request.Schema,
                StationOperationDocumentContract.ResourceFenceValidationRequestSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station resource fence validation request schema is invalid.");
        }

        RequireSha256(request.AccessToken, nameof(request.AccessToken));
        RequireGuid(request.JobId, nameof(request.JobId));
        RequireGuid(request.ProductionRunId, nameof(request.ProductionRunId));
        RequireCanonical(request.OperationRunId, nameof(request.OperationRunId));
        ValidateResourceFences(request.ResourceFences);
    }

    public static void Validate(StationResourceFenceValidationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!string.Equals(
                response.Schema,
                StationOperationDocumentContract.ResourceFenceValidationResponseSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station resource fence validation response schema is invalid.");
        }

        RequireOptionalCanonical(response.RejectionReason, nameof(response.RejectionReason), 4096);
        if (response.Accepted == (response.RejectionReason is not null))
        {
            throw new InvalidDataException(
                "Accepted fence validation cannot contain a reason; rejection requires one.");
        }
    }

    public static void Validate(StationOperationResultDocument result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(
                result.Schema,
                StationOperationDocumentContract.ResultSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station operation result schema is invalid.");
        }

        RequireGuid(result.JobId, nameof(result.JobId));
        RequireGuid(result.RuntimeSessionId, nameof(result.RuntimeSessionId));
        if (result.ExecutionStatus is ExecutionStatus.Pending or ExecutionStatus.Running)
        {
            throw new InvalidDataException("Station operation result must be terminal.");
        }

        var axesAreValid = (result.ExecutionStatus, result.Judgement) switch
        {
            (ExecutionStatus.Completed, ResultJudgement.Passed) => true,
            (ExecutionStatus.Completed, ResultJudgement.Failed) => true,
            (ExecutionStatus.Completed, ResultJudgement.Aborted) => true,
            (ExecutionStatus.Completed, ResultJudgement.NotApplicable) => true,
            (ExecutionStatus.Canceled, ResultJudgement.Aborted) => true,
            (ExecutionStatus.Failed, ResultJudgement.Unknown) => true,
            (ExecutionStatus.TimedOut, ResultJudgement.Unknown) => true,
            (ExecutionStatus.Rejected, ResultJudgement.Unknown) => true,
            _ => false
        };
        if (!axesAreValid)
        {
            throw new InvalidDataException(
                $"Execution status {result.ExecutionStatus} and judgement {result.Judgement} are inconsistent.");
        }

        var hasFailure = result.FailureCode is not null && result.FailureReason is not null;
        if ((result.ExecutionStatus == ExecutionStatus.Completed) == hasFailure)
        {
            throw new InvalidDataException(
                "Only unsuccessful station execution may contain system failure details.");
        }

        RequireOptionalCanonical(result.FailureCode, nameof(result.FailureCode));
        RequireOptionalCanonical(result.FailureReason, nameof(result.FailureReason), 4096);
        RequireUtc(result.StartedAtUtc, nameof(result.StartedAtUtc));
        RequireUtc(result.CompletedAtUtc, nameof(result.CompletedAtUtc));
        if (result.CompletedAtUtc < result.StartedAtUtc)
        {
            throw new InvalidDataException("Station operation completion precedes its start.");
        }

        ValidateOutputs(result.Outputs);
        ArgumentNullException.ThrowIfNull(result.Steps);
        ArgumentNullException.ThrowIfNull(result.Commands);
        ArgumentNullException.ThrowIfNull(result.Incidents);
        ArgumentNullException.ThrowIfNull(result.Artifacts);
        if (result.Steps.Count > 100_000
            || result.Commands.Count > 100_000
            || result.Incidents.Count > 10_000
            || result.Artifacts.Count > 10_000)
        {
            throw new InvalidDataException("Station operation evidence count exceeds its protocol limit.");
        }

        ValidateSteps(result.Steps);
        ValidateCommands(result.Commands, result.Steps);
        ValidateIncidents(result.Incidents);
        ValidateArtifacts(result.Artifacts);
        if (result.CompletedStepCount != result.Steps.Count(step =>
                string.Equals(step.Status, "Completed", StringComparison.Ordinal))
            || result.CommandCount != result.Commands.Count
            || result.IncidentCount != result.Incidents.Count)
        {
            throw new InvalidDataException("Station operation evidence counts are inconsistent.");
        }
    }

    private static void ValidateOutputs(JsonElement outputs)
    {
        if (outputs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Station operation outputs must be one JSON object.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs.EnumerateObject())
        {
            RequireCanonical(output.Name, "output key");
            if (!keys.Add(output.Name)
                || output.Value.ValueKind != JsonValueKind.Object
                || output.Value.EnumerateObject().Count() != 2
                || !output.Value.TryGetProperty("kind", out var kindElement)
                || !output.Value.TryGetProperty("value", out var valueElement)
                || kindElement.ValueKind != JsonValueKind.String
                || valueElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Station output '{output.Name}' must contain only string kind and value fields.");
            }

            var kindToken = kindElement.GetString();
            if (!Enum.TryParse<ProductionContextValueKind>(kindToken, false, out var kind)
                || !Enum.IsDefined(kind)
                || !string.Equals(kind.ToString(), kindToken, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Station output '{output.Name}' has an invalid kind.");
            }

            _ = new ProductionContextValue(kind, valueElement.GetString()!);
        }
    }

    private static void ValidateResourceFences(
        IReadOnlyList<StationOperationResourceFence> fences)
    {
        ArgumentNullException.ThrowIfNull(fences);
        if (fences.Count == 0 || fences.Count > 1024)
        {
            throw new InvalidDataException(
                "Station operation requires a bounded non-empty resource fence collection.");
        }

        var resources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fence in fences)
        {
            ArgumentNullException.ThrowIfNull(fence);
            RequireCanonical(fence.ResourceKind, nameof(fence.ResourceKind));
            RequireCanonical(fence.ResourceId, nameof(fence.ResourceId));
            if (fence.FencingToken <= 0)
            {
                throw new InvalidDataException("Resource fencing token must be positive.");
            }

            RequireUtc(fence.ExpiresAtUtc, nameof(fence.ExpiresAtUtc));
            if (!resources.Add($"{fence.ResourceKind}\0{fence.ResourceId}"))
            {
                throw new InvalidDataException("Station operation resources must be unique.");
            }
        }
    }

    private static void RequirePipeName(string value)
    {
        RequireCanonical(value, "resource fence authority pipe name");
        if (value.Length > 200
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '-' or '_')))
        {
            throw new InvalidDataException(
                "Resource fence authority pipe name contains unsupported characters.");
        }
    }

    private static void ValidateSteps(IReadOnlyList<StationOperationStepEvidence> steps)
    {
        var ids = new HashSet<Guid>();
        foreach (var step in steps)
        {
            RequireGuid(step.StepId, nameof(step.StepId));
            if (!ids.Add(step.StepId))
            {
                throw new InvalidDataException("Station step evidence ids must be unique.");
            }

            RequireCanonical(step.NodeId, nameof(step.NodeId));
            RequireCanonical(step.ActionId, nameof(step.ActionId));
            RequireCanonical(step.TargetKind, nameof(step.TargetKind));
            RequireCanonical(step.TargetId, nameof(step.TargetId));
            RequireCanonical(step.DisplayName, nameof(step.DisplayName));
            RequireToken(step.Status, nameof(step.Status));
            RequireUtc(step.StartedAtUtc, nameof(step.StartedAtUtc));
            RequireOptionalUtc(step.CompletedAtUtc, nameof(step.CompletedAtUtc));
            RequireOptionalCanonical(step.FailureReason, nameof(step.FailureReason), 4096);
        }
    }

    private static void ValidateCommands(
        IReadOnlyList<StationOperationCommandEvidence> commands,
        IReadOnlyList<StationOperationStepEvidence> steps)
    {
        var stepIds = steps.Select(step => step.StepId).ToHashSet();
        var ids = new HashSet<Guid>();
        foreach (var command in commands)
        {
            RequireGuid(command.CommandId, nameof(command.CommandId));
            RequireGuid(command.StepId, nameof(command.StepId));
            if (!ids.Add(command.CommandId) || !stepIds.Contains(command.StepId))
            {
                throw new InvalidDataException(
                    "Station command evidence must have a unique id and reference an existing step.");
            }

            RequireCanonical(command.NodeId, nameof(command.NodeId));
            RequireCanonical(command.ActionId, nameof(command.ActionId));
            RequireCanonical(command.TargetKind, nameof(command.TargetKind));
            RequireCanonical(command.TargetId, nameof(command.TargetId));
            RequireCanonical(command.CapabilityId, nameof(command.CapabilityId));
            RequireCanonical(command.CommandName, nameof(command.CommandName));
            RequireToken(command.Status, nameof(command.Status));
            RequireUtc(command.CreatedAtUtc, nameof(command.CreatedAtUtc));
            RequireUtc(command.DeadlineAtUtc, nameof(command.DeadlineAtUtc));
            RequireOptionalUtc(command.AcceptedAtUtc, nameof(command.AcceptedAtUtc));
            RequireOptionalUtc(command.StartedAtUtc, nameof(command.StartedAtUtc));
            RequireOptionalUtc(command.CompletedAtUtc, nameof(command.CompletedAtUtc));
            RequireOptionalCanonical(command.ResultPayload, nameof(command.ResultPayload), 8 * 1024 * 1024);
            RequireOptionalCanonical(command.FailureReason, nameof(command.FailureReason), 4096);
        }
    }

    private static void ValidateIncidents(IReadOnlyList<StationOperationIncidentEvidence> incidents)
    {
        var ids = new HashSet<Guid>();
        foreach (var incident in incidents)
        {
            RequireGuid(incident.IncidentId, nameof(incident.IncidentId));
            if (!ids.Add(incident.IncidentId))
            {
                throw new InvalidDataException("Station incident evidence ids must be unique.");
            }

            RequireToken(incident.Severity, nameof(incident.Severity));
            RequireCanonical(incident.Code, nameof(incident.Code));
            RequireCanonical(incident.Message, nameof(incident.Message), 4096);
            RequireUtc(incident.OccurredAtUtc, nameof(incident.OccurredAtUtc));
        }
    }

    private static void ValidateArtifacts(IReadOnlyList<StationOperationArtifactEvidence> artifacts)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            RequireRelativePath(artifact.RelativePath, nameof(artifact.RelativePath));
            if (!paths.Add(artifact.RelativePath))
            {
                throw new InvalidDataException("Station artifact paths must be unique.");
            }

            RequireCanonical(artifact.Name, nameof(artifact.Name));
            RequireToken(artifact.Kind, nameof(artifact.Kind));
            RequireOptionalCanonical(artifact.MediaType, nameof(artifact.MediaType));
            if (artifact.SizeBytes < 0)
            {
                throw new InvalidDataException("Station artifact size cannot be negative.");
            }

            RequireSha256(artifact.Sha256, nameof(artifact.Sha256));
        }
    }

    private static void RequireRelativePath(string value, string parameterName)
    {
        RequireCanonical(value, parameterName, 2048);
        if (Path.IsPathRooted(value)
            || value.Contains('\\')
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"{parameterName} must be a safe '/' relative path.");
        }
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{parameterName} must be lowercase SHA-256 hexadecimal.");
        }
    }

    private static void RequireToken(string value, string parameterName)
    {
        RequireCanonical(value, parameterName);
        if (value.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new InvalidDataException($"{parameterName} must contain only ASCII letters.");
        }
    }

    private static void RequireGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new InvalidDataException($"{parameterName} must be a non-empty GUID.");
        }
    }

    private static void RequireOptionalCanonical(
        string? value,
        string parameterName,
        int maximumLength = 512)
    {
        if (value is not null)
        {
            RequireCanonical(value, parameterName, maximumLength);
        }
    }

    private static void RequireCanonical(
        string value,
        string parameterName,
        int maximumLength = 512)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{parameterName} must be bounded canonical text.");
        }
    }

    private static void RequireOptionalUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is not null)
        {
            RequireUtc(value.Value, parameterName);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"{parameterName} must use UTC offset zero.");
        }
    }
}
