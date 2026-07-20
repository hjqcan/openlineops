using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Projects)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationExternalPrograms)]
public sealed class ExternalProgramResourcesController : ControllerBase
{
    private static readonly JsonSerializerOptions ImportSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IExternalProgramResourceService _service;

    public ExternalProgramResourcesController(IExternalProgramResourceService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ExternalProgramResourceApiResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ExternalProgramResourceApiResponse>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await _service.ListAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpGet("{resourceId}")]
    [ProducesResponseType<ExternalProgramResourceApiResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ExternalProgramResourceApiResponse>> GetAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var result = await _service.GetAsync(projectId, applicationId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        Response.SetEditorDocumentRevision(response.Revision);
        return Ok(response);
    }

    [HttpPut("{resourceId}")]
    [ProducesResponseType<ExternalProgramResourceApiResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ExternalProgramResourceApiResponse>> SaveAsync(
        string projectId,
        string applicationId,
        string resourceId,
        SaveExternalProgramResourceApiRequest request,
        CancellationToken cancellationToken)
    {
        var documentKey = $"external-program:{projectId}:{applicationId}:{resourceId}";
        await using var lease = await EditorDocumentConcurrency
            .AcquireAsync(documentKey, cancellationToken)
            .ConfigureAwait(false);
        var precondition = await RequireCurrentRevisionWhenPresentAsync(
                projectId,
                applicationId,
                resourceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (precondition is not null)
        {
            return precondition;
        }

        var mapped = ToApplicationRequest(request, resourceId);
        if (mapped.IsFailure)
        {
            return ToProblem(mapped.Error);
        }

        var result = await _service.SaveDefinitionAsync(
                projectId,
                applicationId,
                mapped.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        Response.SetEditorDocumentRevision(response.Revision);
        return Ok(response);
    }

    [HttpPost("directory-import")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ExternalProgramResourceApiResponse>(StatusCodes.Status201Created)]
    [RequestFormLimits(
        MultipartBodyLengthLimit = ExternalProgramDirectoryImportLimits.MaximumRequestBytes,
        ValueLengthLimit = ExternalProgramDirectoryImportLimits.MaximumFormValueBytes,
        KeyLengthLimit = 256,
        MultipartHeadersLengthLimit = 16 * 1024)]
    [RequestSizeLimit(ExternalProgramDirectoryImportLimits.MaximumRequestBytes)]
    public async Task<ActionResult<ExternalProgramResourceApiResponse>> ImportDirectoryAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return BadRequestProblem("Projects.ExternalProgramImportFormRequired", "Multipart form data is required.");
        }

        var form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var definitionValues = form["definition"];
        var uploadManifestValues = form["uploadManifest"];
        if (definitionValues.Count != 1
            || uploadManifestValues.Count != 1
            || string.IsNullOrWhiteSpace(definitionValues[0])
            || string.IsNullOrWhiteSpace(uploadManifestValues[0])
            || form.Files.Count == 0
            || form.Files.Count > ExternalProgramDirectoryImportLimits.MaximumFileCount
            || form.Keys.Any(key => key is not "definition" and not "uploadManifest"))
        {
            return BadRequestProblem(
                "Projects.ExternalProgramImportIncomplete",
                "One definition, one uploadManifest, and bounded uploaded files are required.");
        }

        var definitionJson = definitionValues[0]!;
        var uploadManifestJson = uploadManifestValues[0]!;

        IReadOnlyCollection<ExternalProgramUploadManifestItemApiRequest>? uploadManifest;
        try
        {
            uploadManifest = JsonSerializer.Deserialize<IReadOnlyCollection<ExternalProgramUploadManifestItemApiRequest>>(
                uploadManifestJson,
                ImportSerializerOptions);
        }
        catch (JsonException exception)
        {
            return BadRequestProblem("Projects.ExternalProgramUploadManifestInvalid", exception.Message);
        }

        var manifestValidation = ValidateUploadManifest(uploadManifest, form.Files);
        if (manifestValidation.IsFailure)
        {
            return ToProblem(manifestValidation.Error);
        }

        SaveExternalProgramResourceApiRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SaveExternalProgramResourceApiRequest>(
                definitionJson,
                ImportSerializerOptions);
        }
        catch (JsonException exception)
        {
            return BadRequestProblem("Projects.ExternalProgramImportDefinitionInvalid", exception.Message);
        }

        var mapped = ToApplicationRequest(request, request?.ResourceId);
        if (mapped.IsFailure)
        {
            return ToProblem(mapped.Error);
        }

        var streams = new List<Stream>(form.Files.Count);
        try
        {
            var uploads = new List<ExternalProgramFileUpload>(form.Files.Count);
            foreach (var item in manifestValidation.Value)
            {
                var stream = item.File.OpenReadStream();
                streams.Add(stream);
                uploads.Add(new ExternalProgramFileUpload(
                    item.Manifest.ResourceRelativePath!,
                    stream,
                    item.Manifest.SizeBytes!.Value,
                    item.Manifest.Sha256!));
            }

            var documentKey = $"external-program:{projectId}:{applicationId}:{mapped.Value.ResourceId}";
            await using var lease = await EditorDocumentConcurrency
                .AcquireAsync(documentKey, cancellationToken)
                .ConfigureAwait(false);
            var current = await _service.GetAsync(
                    projectId,
                    applicationId,
                    mapped.Value.ResourceId,
                    cancellationToken)
                .ConfigureAwait(false);
            var replacingExisting = current.IsSuccess;
            if (current.IsFailure
                && !current.Error.Code.StartsWith("NotFound.", StringComparison.Ordinal))
            {
                return ToProblem(current.Error);
            }

            if (replacingExisting)
            {
                var precondition = EvaluateRevision(ToResponse(current.Value).Revision);
                if (precondition is not null)
                {
                    return precondition;
                }
            }

            var result = await _service.ImportDirectoryAsync(
                    projectId,
                    applicationId,
                    mapped.Value,
                    uploads,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return ToProblem(result.Error);
            }

            var response = ToResponse(result.Value);
            Response.SetEditorDocumentRevision(response.Revision);
            return replacingExisting
                ? Ok(response)
                : Created(
                    $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/external-programs/{Uri.EscapeDataString(response.ResourceId)}",
                    response);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [HttpPost("{resourceId}/trial")]
    [ProducesResponseType<ExternalProgramTrialApiResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ExternalProgramTrialApiResponse>> TrialAsync(
        string projectId,
        string applicationId,
        string resourceId,
        ExternalProgramTrialApiRequest request,
        CancellationToken cancellationToken)
    {
        var trialRequest = ToTrialRequest(request.Inputs);
        if (trialRequest.IsFailure)
        {
            return ToProblem(trialRequest.Error);
        }

        var result = await _service.TrialAsync(
                projectId,
                applicationId,
                resourceId,
                trialRequest.Value,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpPost("trial")]
    [ProducesResponseType<ExternalProgramTrialApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ExternalProgramTrialApiResponse>> TrialDefinitionAsync(
        string projectId,
        string applicationId,
        ExternalProgramDefinitionTrialApiRequest request,
        CancellationToken cancellationToken)
    {
        var definition = ToApplicationRequest(request.Definition, request.Definition?.ResourceId);
        if (definition.IsFailure)
        {
            return ToProblem(definition.Error);
        }

        var trialRequest = ToTrialRequest(request.Inputs);
        if (trialRequest.IsFailure)
        {
            return ToProblem(trialRequest.Error);
        }

        var result = await _service.TrialDefinitionAsync(
                projectId,
                applicationId,
                definition.Value,
                trialRequest.Value,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpDelete("{resourceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var documentKey = $"external-program:{projectId}:{applicationId}:{resourceId}";
        await using var lease = await EditorDocumentConcurrency
            .AcquireAsync(documentKey, cancellationToken)
            .ConfigureAwait(false);
        var precondition = await RequireCurrentRevisionAsync(
                projectId,
                applicationId,
                resourceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (precondition is not null)
        {
            return precondition;
        }

        var result = await _service.DeleteAsync(projectId, applicationId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : NoContent();
    }

    private static Result<SaveExternalProgramResourceRequest> ToApplicationRequest(
        SaveExternalProgramResourceApiRequest? request,
        string? routeResourceId)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.ResourceId)
            || !string.Equals(request.ResourceId, routeResourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.CapabilityId)
            || string.IsNullOrWhiteSpace(request.CommandName)
            || !Enum.TryParse<ExternalProgramLaunchKind>(request.LaunchKind, ignoreCase: false, out var launchKind)
            || !Enum.IsDefined(launchKind)
            || !string.Equals(request.LaunchKind, launchKind.ToString(), StringComparison.Ordinal)
            || request.ArgumentTemplates is null
            || request.InputMappings is null
            || request.ResultMappings is null
            || request.OutcomeMapping is null
            || request.PermissionProfile is null
            || request.ExecutionLimits is null
            || request.ArgumentTemplates.Any(string.IsNullOrWhiteSpace)
            || request.InputMappings.Any(mapping => mapping is null
                || string.IsNullOrWhiteSpace(mapping.Source)
                || string.IsNullOrWhiteSpace(mapping.Target))
            || request.ResultMappings.Any(mapping => mapping is null
                || string.IsNullOrWhiteSpace(mapping.SourcePath)
                || string.IsNullOrWhiteSpace(mapping.TargetKey)
                || !Enum.TryParse<ProductionContextValueKind>(
                    mapping.ValueKind,
                    ignoreCase: false,
                    out var valueKind)
                || !Enum.IsDefined(valueKind)
                || !string.Equals(mapping.ValueKind, valueKind.ToString(), StringComparison.Ordinal))
            || string.IsNullOrWhiteSpace(request.OutcomeMapping.SourcePath)
            || string.IsNullOrWhiteSpace(request.OutcomeMapping.PassedToken)
            || string.IsNullOrWhiteSpace(request.OutcomeMapping.FailedToken)
            || string.IsNullOrWhiteSpace(request.OutcomeMapping.AbortedToken)
            || string.IsNullOrWhiteSpace(request.PermissionProfile.ProfileName)
            || request.PermissionProfile.NetworkAccessAllowed is null
            || request.PermissionProfile.AllowedEnvironmentVariables is null
            || request.PermissionProfile.AllowedEnvironmentVariables.Any(string.IsNullOrWhiteSpace)
            || request.ExecutionLimits.TimeoutMilliseconds is null
            || request.ExecutionLimits.MaximumProcessCount is null
            || request.ExecutionLimits.MaximumWorkingSetBytes is null
            || request.ExecutionLimits.MaximumCpuTimeMilliseconds is null
            || request.ExecutionLimits.MaximumStandardOutputBytes is null
            || request.ExecutionLimits.MaximumStandardErrorBytes is null
            || request.ExecutionLimits.MaximumArtifactCount is null
            || request.ExecutionLimits.MaximumArtifactBytes is null
            || request.ExecutionLimits.MaximumTotalArtifactBytes is null)
        {
            return Result.Failure<SaveExternalProgramResourceRequest>(ApplicationError.Validation(
                "Projects.ExternalProgramRequestIncomplete",
                "External program resource request is incomplete or uses a non-exact launch kind."));
        }

        return Result.Success(new SaveExternalProgramResourceRequest(
            request.ResourceId,
            request.DisplayName,
            request.CapabilityId,
            request.CommandName,
            launchKind,
            request.EntryPoint,
            request.ProviderKind,
            request.ProviderKey,
            request.ArgumentTemplates.Select(item => item!).ToArray(),
            request.InputMappings.Select(item => new ExternalProgramInputMapping(
                item!.Source!,
                item.Target!)).ToArray(),
            request.ResultMappings.Select(item => new ExternalProgramResultMapping(
                item!.SourcePath!,
                item.TargetKey!,
                Enum.Parse<ProductionContextValueKind>(item.ValueKind!, ignoreCase: false))).ToArray(),
            new ExternalProgramOutcomeMapping(
                request.OutcomeMapping.SourcePath,
                request.OutcomeMapping.PassedToken,
                request.OutcomeMapping.FailedToken,
                request.OutcomeMapping.AbortedToken),
            new ExternalProgramPermissionProfile(
                request.PermissionProfile.ProfileName,
                request.PermissionProfile.NetworkAccessAllowed.Value,
                request.PermissionProfile.AllowedEnvironmentVariables.Select(item => item!).ToArray()),
            new ExternalProgramExecutionLimits(
                request.ExecutionLimits.TimeoutMilliseconds.Value,
                request.ExecutionLimits.MaximumProcessCount.Value,
                request.ExecutionLimits.MaximumWorkingSetBytes.Value,
                request.ExecutionLimits.MaximumCpuTimeMilliseconds.Value,
                request.ExecutionLimits.MaximumStandardOutputBytes.Value,
                request.ExecutionLimits.MaximumStandardErrorBytes.Value,
                request.ExecutionLimits.MaximumArtifactCount.Value,
                request.ExecutionLimits.MaximumArtifactBytes.Value,
                request.ExecutionLimits.MaximumTotalArtifactBytes.Value)));
    }

    private static Result<ExternalProgramProtocolTrialRequest> ToTrialRequest(
        IReadOnlyDictionary<string, ExternalProgramTrialInputApiRequest?>? inputs)
    {
        if (inputs is null
            || inputs.Any(item => item.Value is null
                || string.IsNullOrWhiteSpace(item.Key)
                || string.IsNullOrWhiteSpace(item.Value.Kind)
                || string.IsNullOrWhiteSpace(item.Value.CanonicalValue)
                || !Enum.TryParse<ExternalProgramTrialInputKind>(
                    item.Value.Kind,
                    ignoreCase: false,
                    out var kind)
                || !Enum.IsDefined(kind)
                || !string.Equals(item.Value.Kind, kind.ToString(), StringComparison.Ordinal)))
        {
            return Result.Failure<ExternalProgramProtocolTrialRequest>(ApplicationError.Validation(
                "Projects.ExternalProgramTrialInputInvalid",
                "Typed trial inputs are required."));
        }

        return Result.Success(new ExternalProgramProtocolTrialRequest(inputs.ToDictionary(
            item => item.Key,
            item => new ExternalProgramTrialInputValue(
                Enum.Parse<ExternalProgramTrialInputKind>(item.Value!.Kind!, ignoreCase: false),
                item.Value.CanonicalValue!),
            StringComparer.Ordinal)));
    }

    private static Result<IReadOnlyCollection<ValidatedUpload>> ValidateUploadManifest(
        IReadOnlyCollection<ExternalProgramUploadManifestItemApiRequest>? manifest,
        IFormFileCollection files)
    {
        if (manifest is null
            || manifest.Count == 0
            || manifest.Count != files.Count
            || manifest.Count > ExternalProgramDirectoryImportLimits.MaximumFileCount
            || manifest.Any(item => item is null
                || string.IsNullOrWhiteSpace(item.FieldName)
                || string.IsNullOrWhiteSpace(item.ResourceRelativePath)
                || item.SizeBytes is null
                || item.SizeBytes < 0
                || item.SizeBytes > ExternalProgramDirectoryImportLimits.MaximumFileBytes
                || string.IsNullOrWhiteSpace(item.Sha256)
                || !ExternalProgramResourceContract.IsSha256(item.Sha256)))
        {
            return Result.Failure<IReadOnlyCollection<ValidatedUpload>>(ApplicationError.Validation(
                "Projects.ExternalProgramUploadManifestInvalid",
                "Upload manifest count, fields, sizes, or SHA-256 values are invalid."));
        }

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<ValidatedUpload>(manifest.Count);
        long totalBytes = 0;
        try
        {
            foreach (var item in manifest)
            {
                if (!ExternalProgramDirectoryImportLimits.CanAccumulateContentBytes(
                        totalBytes,
                        item.SizeBytes!.Value))
                {
                    throw new InvalidDataException("Upload manifest total size exceeds the HTTP import limit.");
                }

                totalBytes += item.SizeBytes.Value;
                var relativePath = ExternalProgramResourceContract.CanonicalRelativePath(
                    item.ResourceRelativePath!,
                    nameof(item.ResourceRelativePath),
                    ExternalProgramResourceContract.FilesDirectoryName);
                if (!fieldNames.Add(item.FieldName!) || !resourcePaths.Add(relativePath))
                {
                    throw new InvalidDataException(
                        "Upload manifest field names and resource paths must be unique.");
                }

                var matches = files.Where(file => string.Equals(
                        file.Name,
                        item.FieldName,
                        StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (matches.Length != 1 || matches[0].Length != item.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"Upload part '{item.FieldName}' does not match its declared size or is missing/duplicated.");
                }

                validated.Add(new ValidatedUpload(
                    item with { ResourceRelativePath = relativePath },
                    matches[0]));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return Result.Failure<IReadOnlyCollection<ValidatedUpload>>(ApplicationError.Validation(
                "Projects.ExternalProgramUploadManifestInvalid",
                exception.Message));
        }

        return Result.Success<IReadOnlyCollection<ValidatedUpload>>(validated);
    }

    private async Task<ObjectResult?> RequireCurrentRevisionWhenPresentAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var current = await _service.GetAsync(projectId, applicationId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsFailure)
        {
            return current.Error.Code.StartsWith("NotFound.", StringComparison.Ordinal)
                ? null
                : ToProblem(current.Error);
        }

        return EvaluateRevision(ToResponse(current.Value).Revision);
    }

    private async Task<ObjectResult?> RequireCurrentRevisionAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var current = await _service.GetAsync(projectId, applicationId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        return current.IsFailure
            ? ToProblem(current.Error)
            : EvaluateRevision(ToResponse(current.Value).Revision);
    }

    private ObjectResult? EvaluateRevision(string currentRevision)
    {
        var precondition = EditorDocumentConcurrency.Evaluate(
            Request.Headers[EditorDocumentConcurrency.IfMatchHeaderName].ToString(),
            Request.Headers[EditorDocumentConcurrency.ConflictResolutionHeaderName].ToString(),
            currentRevision);
        return precondition == EditorDocumentPrecondition.Satisfied
            ? null
            : this.EditorDocumentPreconditionProblem(precondition, currentRevision);
    }

    private static ExternalProgramResourceApiResponse ToResponse(ExternalProgramResource resource) => new(
        resource.ResourceId,
        resource.DisplayName,
        resource.CapabilityId,
        resource.CommandName,
        resource.LaunchKind.ToString(),
        resource.EntryPoint,
        resource.ProviderKind,
        resource.ProviderKey,
        resource.ArgumentTemplates,
        resource.InputMappings.Select(item => new ExternalProgramInputMappingApiResponse(
            item.Source,
            item.Target)).ToArray(),
        resource.ResultMappings.Select(item => new ExternalProgramResultMappingApiResponse(
            item.SourcePath,
            item.TargetKey,
            item.ValueKind.ToString())).ToArray(),
        new ExternalProgramOutcomeMappingApiResponse(
            resource.OutcomeMapping.SourcePath,
            resource.OutcomeMapping.PassedToken,
            resource.OutcomeMapping.FailedToken,
            resource.OutcomeMapping.AbortedToken),
        new ExternalProgramPermissionProfileApiResponse(
            resource.PermissionProfile.ProfileName,
            resource.PermissionProfile.NetworkAccessAllowed,
            resource.PermissionProfile.AllowedEnvironmentVariables),
        new ExternalProgramExecutionLimitsApiResponse(
            resource.ExecutionLimits.TimeoutMilliseconds,
            resource.ExecutionLimits.MaximumProcessCount,
            resource.ExecutionLimits.MaximumWorkingSetBytes,
            resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
            resource.ExecutionLimits.MaximumStandardOutputBytes,
            resource.ExecutionLimits.MaximumStandardErrorBytes,
            resource.ExecutionLimits.MaximumArtifactCount,
            resource.ExecutionLimits.MaximumArtifactBytes,
            resource.ExecutionLimits.MaximumTotalArtifactBytes),
        resource.Files.Select(item => new ExternalProgramFileApiResponse(
            item.RelativePath,
            item.SizeBytes,
            item.Sha256)).ToArray(),
        resource.ContentSha256,
        resource.UpdatedAtUtc,
        resource.ContentSha256);

    private static ExternalProgramTrialApiResponse ToResponse(ExternalProgramProtocolTrialResult result) => new(
        result.ResourceId,
        result.LaunchKind,
        result.ContentSha256,
        result.ExecutionStatus,
        result.Judgement,
        result.ResultPayload,
        result.FailureReason,
        result.Artifacts.Select(item => new ExternalProgramTrialArtifactApiResponse(
            item.Name,
            item.Kind,
            item.MediaType,
            item.SizeBytes,
            item.Sha256)).ToArray());

    private ObjectResult BadRequestProblem(string code, string message) => Problem(
        title: code,
        detail: message,
        statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return Problem(title: error.Code, detail: error.Message, statusCode: statusCode);
    }

    private sealed record ValidatedUpload(
        ExternalProgramUploadManifestItemApiRequest Manifest,
        IFormFile File);
}
