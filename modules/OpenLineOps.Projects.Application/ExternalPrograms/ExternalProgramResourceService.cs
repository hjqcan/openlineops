using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public sealed class ExternalProgramResourceService : IExternalProgramResourceService
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IExternalProgramResourceRepository _repository;
    private readonly IExternalProgramTrialExecutor _trialExecutor;
    private readonly IExternalProgramResourceUsageInspector _usageInspector;
    private readonly IClock _clock;

    public ExternalProgramResourceService(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IExternalProgramResourceRepository repository,
        IExternalProgramTrialExecutor trialExecutor,
        IExternalProgramResourceUsageInspector usageInspector,
        IClock clock)
    {
        _scopeResolver = scopeResolver;
        _repository = repository;
        _trialExecutor = trialExecutor;
        _usageInspector = usageInspector;
        _clock = clock;
    }

    public async Task<Result<IReadOnlyCollection<ExternalProgramResource>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<ExternalProgramResource>>(scope.Error);
        }

        try
        {
            return Result.Success(await _repository.ListAsync(scope.Value, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception) when (IsResourceException(exception))
        {
            return Result.Failure<IReadOnlyCollection<ExternalProgramResource>>(InvalidResource(exception));
        }
    }

    public async Task<Result<ExternalProgramResource>> GetAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<ExternalProgramResource>(scope.Error);
        }

        try
        {
            ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
            var resource = await _repository.GetAsync(scope.Value, resourceId, cancellationToken)
                .ConfigureAwait(false);
            return resource is null
                ? Result.Failure<ExternalProgramResource>(NotFound(resourceId))
                : Result.Success(resource);
        }
        catch (Exception exception) when (IsResourceException(exception))
        {
            return Result.Failure<ExternalProgramResource>(InvalidResource(exception));
        }
    }

    public async Task<Result<ExternalProgramResource>> SaveAsync(
        string projectId,
        string applicationId,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> uploads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(uploads);
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<ExternalProgramResource>(scope.Error);
        }

        try
        {
            ExternalProgramResourceValidator.ValidateDefinition(request);
            ValidateUploads(uploads);
            var resource = await _repository.SaveAsync(
                    scope.Value,
                    request,
                    uploads,
                    _clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return Result.Success(resource);
        }
        catch (Exception exception) when (IsResourceException(exception))
        {
            return Result.Failure<ExternalProgramResource>(InvalidResource(exception));
        }
    }

    public async Task<Result<ExternalProgramResource>> ImportFileAsync(
        string projectId,
        string applicationId,
        string resourceId,
        ExternalProgramFileUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<ExternalProgramResource>(scope.Error);
        }

        try
        {
            ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
            ExternalProgramResourceContract.CanonicalRelativePath(
                upload.ResourceRelativePath,
                nameof(upload.ResourceRelativePath),
                ExternalProgramResourceContract.FilesDirectoryName);
            ValidateUploads([upload]);
            var resource = await _repository.GetAsync(scope.Value, resourceId, cancellationToken)
                .ConfigureAwait(false);
            if (resource is null)
            {
                return Result.Failure<ExternalProgramResource>(NotFound(resourceId));
            }

            return Result.Success(await _repository.ImportFileAsync(
                    scope.Value,
                    resourceId,
                    upload,
                    _clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception) when (IsResourceException(exception))
        {
            return Result.Failure<ExternalProgramResource>(InvalidResource(exception));
        }
    }

    public async Task<Result<bool>> DeleteAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<bool>(scope.Error);
        }

        try
        {
            ExternalProgramResourceContract.PortableId(resourceId, nameof(resourceId));
            if (await _usageInspector.IsReferencedAsync(scope.Value, resourceId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Result.Failure<bool>(ApplicationError.Conflict(
                    "Projects.ExternalProgramResourceInUse",
                    $"External program resource {resourceId} is referenced by an Application Flow."));
            }

            if (await _repository.GetAsync(scope.Value, resourceId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Result.Failure<bool>(NotFound(resourceId));
            }

            await _repository.DeleteAsync(scope.Value, resourceId, cancellationToken).ConfigureAwait(false);
            return Result.Success(true);
        }
        catch (Exception exception) when (IsResourceException(exception))
        {
            return Result.Failure<bool>(InvalidResource(exception));
        }
    }

    public async Task<Result<ExternalProgramProtocolTrialResult>> TrialAsync(
        string projectId,
        string applicationId,
        string resourceId,
        ExternalProgramProtocolTrialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        var resourceResult = await GetAsync(projectId, applicationId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (resourceResult.IsFailure)
        {
            return Result.Failure<ExternalProgramProtocolTrialResult>(resourceResult.Error);
        }

        return await _trialExecutor.ExecuteAsync(
                await ResolveScopeValueAsync(projectId, applicationId, cancellationToken).ConfigureAwait(false),
                resourceResult.Value,
                request,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<ExternalProgramProtocolTrialResult>> TrialDefinitionAsync(
        string projectId,
        string applicationId,
        SaveExternalProgramResourceRequest definition,
        ExternalProgramProtocolTrialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<ExternalProgramProtocolTrialResult>(scope.Error);
        }

        if (definition.LaunchKind != ExternalProgramLaunchKind.Provider)
        {
            return Result.Failure<ExternalProgramProtocolTrialResult>(ApplicationError.Validation(
                "Projects.ExternalProgramDefinitionTrialProviderRequired",
                "An unsaved protocol trial is only available for Provider definitions without executable files."));
        }

        try
        {
            var resource = ExternalProgramResourceFactory.Create(definition, [], _clock.UtcNow);
            return await _trialExecutor.ExecuteAsync(scope.Value, resource, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsResourceException(exception))
        {
            return Result.Failure<ExternalProgramProtocolTrialResult>(InvalidResource(exception));
        }
    }

    private async ValueTask<ProjectApplicationWorkspaceScope> ResolveScopeValueAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        var result = await ResolveScopeAsync(projectId, applicationId, cancellationToken).ConfigureAwait(false);
        return result.Value;
    }

    private static void ValidateUploads(IReadOnlyCollection<ExternalProgramFileUpload> uploads)
    {
        const int maximumFileCount = 256;
        const long maximumFileBytes = 512L * 1024 * 1024;
        const long maximumTotalBytes = 2L * 1024 * 1024 * 1024;
        if (uploads.Count > maximumFileCount
            || uploads.Any(upload => upload is null
                || upload.Content is null
                || !upload.Content.CanRead
                || upload.SizeBytes < 0
                || upload.SizeBytes > maximumFileBytes
                || !ExternalProgramResourceContract.IsSha256(upload.ExpectedSha256)))
        {
            throw new ArgumentException("External program upload count or size exceeds the supported limit.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var upload in uploads)
        {
            if (totalBytes > maximumTotalBytes - upload.SizeBytes)
            {
                throw new ArgumentException("External program upload total size exceeds the supported limit.");
            }

            totalBytes += upload.SizeBytes;
            var path = ExternalProgramResourceContract.CanonicalRelativePath(
                upload.ResourceRelativePath,
                nameof(upload.ResourceRelativePath),
                ExternalProgramResourceContract.FilesDirectoryName);
            if (!paths.Add(path))
            {
                throw new ArgumentException($"External program upload target '{path}' is duplicated.");
            }
        }
    }

    private async ValueTask<Result<ProjectApplicationWorkspaceScope>> ResolveScopeAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(applicationId))
        {
            return Result.Failure<ProjectApplicationWorkspaceScope>(ApplicationError.Validation(
                "Projects.ExternalProgramScopeRequired",
                "ProjectId and ApplicationId are required."));
        }

        var scope = await _scopeResolver.ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        return scope is null
            ? Result.Failure<ProjectApplicationWorkspaceScope>(ApplicationError.NotFound(
                "Projects.ExternalProgramApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."))
            : Result.Success(scope);
    }

    private static bool IsResourceException(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static ApplicationError InvalidResource(Exception exception) =>
        ApplicationError.Validation("Projects.ExternalProgramResourceInvalid", exception.Message);

    private static ApplicationError NotFound(string resourceId) => ApplicationError.NotFound(
        "Projects.ExternalProgramResourceNotFound",
        $"External program resource {resourceId} was not found.");
}
