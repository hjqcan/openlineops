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

    public async Task<Result<ExternalProgramResource>> SaveDefinitionAsync(
        string projectId,
        string applicationId,
        SaveExternalProgramResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<ExternalProgramResource>(scope.Error);
        }

        try
        {
            ExternalProgramResourceValidator.ValidateDefinition(request);
            var resource = await _repository.SaveDefinitionAsync(
                    scope.Value,
                    request,
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

    public async Task<Result<ExternalProgramResource>> ImportDirectoryAsync(
        string projectId,
        string applicationId,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        var scope = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result.Failure<ExternalProgramResource>(scope.Error);
        }

        try
        {
            ExternalProgramResourceValidator.ValidateDefinition(request);
            if (request.LaunchKind != ExternalProgramLaunchKind.ApplicationExecutable)
            {
                throw new ArgumentException(
                    "External program directory imports require ApplicationExecutable launch kind.",
                    nameof(request));
            }
            ValidateUploads(files);
            return Result.Success(await _repository.ImportDirectoryAsync(
                    scope.Value,
                    request,
                    files,
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
        if (uploads.Count is 0 or > ExternalProgramResourceContract.MaximumFrozenFileCount
            || uploads.Any(upload => upload is null
                || upload.Content is null
                || !upload.Content.CanRead
                || upload.SizeBytes < 0
                || upload.SizeBytes > ExternalProgramResourceContract.MaximumFrozenFileBytes
                || !ExternalProgramResourceContract.IsSha256(upload.ExpectedSha256)))
        {
            throw new ArgumentException("External program upload count or size exceeds the supported limit.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var upload in uploads)
        {
            try
            {
                totalBytes = ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                    paths.Count,
                    totalBytes,
                    upload.SizeBytes);
            }
            catch (InvalidDataException exception)
            {
                throw new ArgumentException(
                    "External program upload total size exceeds the supported limit.",
                    nameof(uploads),
                    exception);
            }
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
