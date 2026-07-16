using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Api.Models;
using OpenLineOps.Plugins.Application.Extensions;
using OpenLineOps.Plugins.Infrastructure.Extensions;

namespace OpenLineOps.Plugins.Api.Controllers;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Plugins)]
[Route(OpenLineOpsApiRoutes.ProjectApplicationExtensions)]
public sealed class ApplicationExtensionsController : ControllerBase
{
    private const int MaximumFormValueBytes = 16 * 1024;

    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IApplicationExtensionPackageService _service;

    public ApplicationExtensionsController(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IApplicationExtensionPackageService service)
    {
        _scopeResolver = scopeResolver;
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ApplicationExtensionPackageResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ApplicationExtensionPackageResponse>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return ApplicationNotFound(projectId, applicationId);
        }

        var result = await _service.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(ToResponse).ToArray());
    }

    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(
        MultipartBodyLengthLimit = FileSystemApplicationExtensionPackageService.MaximumMultipartBodyBytes,
        ValueLengthLimit = MaximumFormValueBytes,
        KeyLengthLimit = 128,
        MultipartHeadersLengthLimit = 16 * 1024)]
    [RequestSizeLimit(FileSystemApplicationExtensionPackageService.MaximumMultipartBodyBytes)]
    [ProducesResponseType<ApplicationExtensionPackageResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ApplicationExtensionPackageResponse>> ImportAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return ApplicationNotFound(projectId, applicationId);
        }

        if (!Request.HasFormContentType)
        {
            return BadRequestProblem(
                "Plugins.ExtensionImportFormRequired",
                "Multipart form data is required.");
        }

        var form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var portableIds = form["portableId"];
        var files = form.Files.Where(file => string.Equals(
                file.Name,
                "package",
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (portableIds.Count != 1
            || string.IsNullOrWhiteSpace(portableIds[0])
            || files.Length != 1
            || form.Files.Count != 1
            || form.Keys.Any(key => key != "portableId"))
        {
            return BadRequestProblem(
                "Plugins.ExtensionImportIncomplete",
                "Exactly one portableId form value and one package ZIP file are required.");
        }

        var file = files[0];
        await using var stream = file.OpenReadStream();
        var result = await _service.ImportAsync(
                scope,
                new ImportApplicationExtensionPackageRequest(
                    portableIds[0]!,
                    stream,
                    file.Length),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        return Created(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/applications/{Uri.EscapeDataString(applicationId)}/extensions/{Uri.EscapeDataString(response.PluginId)}",
            response);
    }

    [HttpDelete("{pluginId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveAsync(
        string projectId,
        string applicationId,
        string pluginId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return ApplicationNotFound(projectId, applicationId);
        }

        var result = await _service.RemoveAsync(scope, pluginId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : NoContent();
    }

    [HttpPost("validate")]
    [ProducesResponseType<IReadOnlyCollection<ApplicationExtensionPackageResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ApplicationExtensionPackageResponse>>> ValidateAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return ApplicationNotFound(projectId, applicationId);
        }

        var result = await _service.ValidateAsync(scope, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(result.Value.Select(ToResponse).ToArray());
    }

    private async ValueTask<ProjectApplicationWorkspaceScope?> ResolveScopeAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        return await _scopeResolver.ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private ObjectResult ApplicationNotFound(string projectId, string applicationId) => Problem(
        title: "NotFound.Plugins.ExtensionApplicationNotFound",
        detail: $"Application '{applicationId}' was not found in Project '{projectId}'.",
        statusCode: StatusCodes.Status404NotFound);

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

    private static ApplicationExtensionPackageResponse ToResponse(
        ApplicationExtensionPackageDetails details)
    {
        return new ApplicationExtensionPackageResponse(
            details.PortableId,
            details.Reference.PluginId,
            details.Reference.Version,
            details.Reference.ManifestPath,
            details.Reference.ContentSha256,
            details.Package.ManifestSha256,
            details.Package.EntryAssemblySha256,
            (details.Package.Files ?? [])
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => new ApplicationExtensionPackageFileResponse(
                    file.RelativePath,
                    file.SizeBytes,
                    file.Sha256))
                .ToArray(),
            details.Validation.IsValid,
            details.Validation.Issues.Select(issue => new PluginValidationIssueResponse(
                issue.Code,
                issue.Message)).ToArray(),
            ToResponse(details.Package.Manifest));
    }

    private static PluginManifestResponse ToResponse(PluginManifest manifest)
    {
        return new PluginManifestResponse(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Kind.ToString(),
            manifest.EntryAssembly,
            manifest.EntryType,
            manifest.ContractVersion,
            manifest.MinimumPlatformVersion,
            manifest.RuntimeIdentifier,
            manifest.AbiVersion,
            manifest.Capabilities?.ToArray() ?? [],
            manifest.DeviceCommands?.Select(ToResponse).ToArray() ?? [],
            manifest.ProcessCommands?.Select(ToResponse).ToArray() ?? []);
    }

    private static PluginCommandDefinitionResponse ToResponse(PluginDeviceCommandDefinition command) => new(
        command.Id,
        command.Capability,
        command.CommandName,
        command.InputSchema,
        command.OutputSchema,
        command.TimeoutMilliseconds,
        command.MaxRetries);

    private static PluginCommandDefinitionResponse ToResponse(PluginProcessCommandDefinition command) => new(
        command.Id,
        command.Capability,
        command.CommandName,
        command.InputSchema,
        command.OutputSchema,
        command.TimeoutMilliseconds,
        command.MaxRetries);
}
