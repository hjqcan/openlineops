using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Application.Releases;

public sealed class ProjectReleasePublisher : IProjectReleasePublisher
{
    private readonly IAutomationProjectService _projectService;
    private readonly IAutomationProjectWorkspaceService _workspaceService;
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectReleaseSourceResolver _sourceResolver;
    private readonly IProjectReleaseArtifactStore _artifactStore;
    private readonly IProjectReleaseStationPackagePublisher _stationPackagePublisher;
    private readonly IClock _clock;

    public ProjectReleasePublisher(
        IAutomationProjectService projectService,
        IAutomationProjectWorkspaceService workspaceService,
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseSourceResolver sourceResolver,
        IProjectReleaseArtifactStore artifactStore,
        IProjectReleaseStationPackagePublisher stationPackagePublisher,
        IClock clock)
    {
        _projectService = projectService;
        _workspaceService = workspaceService;
        _scopeResolver = scopeResolver;
        _sourceResolver = sourceResolver;
        _artifactStore = artifactStore;
        _stationPackagePublisher = stationPackagePublisher;
        _clock = clock;
    }

    public async Task<Result<AutomationProjectDetails>> PublishAsync(
        string projectId,
        PublishProjectReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = Validate(projectId, request);
        if (validation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(validation);
        }

        var projectResult = await _projectService
            .GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(projectResult.Error);
        }

        var project = projectResult.Value;
        var application = project.Applications.SingleOrDefault(candidate => string.Equals(
            candidate.ApplicationId,
            request.ApplicationId,
            StringComparison.Ordinal));
        if (application is null)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.NotFound(
                "Projects.ProjectApplicationNotFound",
                $"Application {request.ApplicationId} was not found in project {projectId}."));
        }

        if (project.Snapshots.Any(snapshot => string.Equals(
                snapshot.SnapshotId,
                request.SnapshotId,
                StringComparison.Ordinal)))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.SnapshotAlreadyExists",
                $"Project snapshot {request.SnapshotId} already exists in project {projectId}."));
        }

        if (string.IsNullOrWhiteSpace(application.TopologyId))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.TopologyNotLinked",
                $"Application {application.ApplicationId} does not have a linked topology."));
        }

        // Synchronize the Application project file before freezing its source
        // tree so the release contains the same topology entry link
        // that were validated from the aggregate.
        var sourceManifestResult = await _workspaceService
            .SaveManifestAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceManifestResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(sourceManifestResult.Error);
        }

        var scope = await _scopeResolver
            .ResolveAsync(projectId, application.ApplicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.NotFound(
                "Projects.ProjectApplicationNotFound",
                $"Application {application.ApplicationId} was not found in project {projectId}."));
        }

        var sourceResult = await _sourceResolver
            .ResolveAsync(
                scope,
                application.TopologyId,
                request.ProductionLineDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(sourceResult.Error);
        }

        var metadata = sourceResult.Value;
        var sourceValidation = ValidateResolvedSource(application.TopologyId, request, metadata);
        if (sourceValidation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(sourceValidation);
        }

        try
        {
            await _stationPackagePublisher.ValidateConfigurationAsync(
                    new ProjectReleaseStationPackagePreflightRequest(scope, request.SnapshotId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsArtifactStorageException(exception))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.StationPackageConfigurationInvalid",
                exception.Message));
        }

        ProjectReleaseArtifactDescriptor release;
        try
        {
            release = await _artifactStore
                .PublishAsync(scope, request.SnapshotId, _clock.UtcNow, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsArtifactStorageException(exception))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactPublicationFailed",
                exception.Message));
        }

        await using var transaction = new ProjectReleasePublicationTransaction(
            _workspaceService,
            _artifactStore,
            _stationPackagePublisher,
            scope,
            release);
        var descriptorValidation = ValidateReleaseDescriptor(scope, request.SnapshotId, release);
        if (descriptorValidation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(descriptorValidation);
        }

        ProjectApplicationWorkspaceScope releaseScope;
        try
        {
            releaseScope = new ProjectApplicationWorkspaceScope(
                scope.ProjectId,
                scope.ApplicationId,
                release.SourceRootPath,
                release.ApplicationProjectRelativePath);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactDescriptorInvalid",
                exception.Message));
        }

        var copiedSourceResult = await _sourceResolver
            .ResolveAsync(
                releaseScope,
                metadata.TopologyId,
                metadata.ProductionLine.LineDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (copiedSourceResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactSourceInvalid",
                $"Copied release source could not be resolved: {copiedSourceResult.Error.Message}"));
        }

        if (!SemanticMetadataEquals(metadata, copiedSourceResult.Value))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactSourceMismatch",
                "Copied release source metadata differs from the source resolved before publication."));
        }

        var relativeManifestPathResult = GetProjectRelativeManifestPath(scope, release.ManifestPath);
        if (relativeManifestPathResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(relativeManifestPathResult.Error);
        }

        ProjectReleaseStationPackageSet stationPackages;
        try
        {
            stationPackages = await _stationPackagePublisher.PublishAsync(
                    new ProjectReleaseStationPackageRequest(
                        release,
                        copiedSourceResult.Value,
                        release.PublishedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsArtifactStorageException(exception))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.StationPackagePublicationFailed",
                exception.Message));
        }

        transaction.SetStationPackages(stationPackages);

        var stationPackageValidation = ValidateStationPackages(release, metadata, stationPackages);
        if (stationPackageValidation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(stationPackageValidation);
        }

        transaction.MarkAggregateMutationPossible();
        var publishResult = await _projectService
            .PublishSnapshotAsync(
                projectId,
                new PublishProjectSnapshotRequest(
                    request.SnapshotId,
                    application.ApplicationId,
                    metadata.TopologyId,
                    metadata.LayoutIds,
                    metadata.ProductionLine.LineDefinitionId,
                    metadata.CapabilityBindings.Select(binding => new SnapshotCapabilityBindingRequest(
                        binding.CapabilityId,
                        binding.BindingId,
                        binding.ProviderKind,
                        binding.ProviderKey,
                        binding.OwnerSystemId,
                        binding.OwnerStationSystemId)).ToArray(),
                    metadata.TargetReferences.Select(target => new ProjectTargetReferenceRequest(
                        target.Kind,
                        target.TargetId)).ToArray(),
                    metadata.BlockVersionIds,
                    relativeManifestPathResult.Value,
                    release.ContentSha256),
                cancellationToken)
            .ConfigureAwait(false);
        if (publishResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(publishResult.Error);
        }

        var manifestResult = await _workspaceService
            .SaveManifestAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (manifestResult.IsSuccess)
        {
            transaction.Commit();
            return Result.Success(manifestResult.Value.Project);
        }

        return Result.Failure<AutomationProjectDetails>(manifestResult.Error);
    }

    private static ApplicationError? Validate(
        string projectId,
        PublishProjectReleaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Required("Projects.ProjectIdRequired", "ProjectId");
        }

        if (string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            return Required("Projects.SnapshotIdRequired", "SnapshotId");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Required("Projects.ApplicationIdRequired", "ApplicationId");
        }

        if (string.IsNullOrWhiteSpace(request.ProductionLineDefinitionId))
        {
            return Required("Projects.ProductionLineDefinitionIdRequired", "ProductionLineDefinitionId");
        }

        return null;
    }

    private static ApplicationError? ValidateResolvedSource(
        string topologyId,
        PublishProjectReleaseRequest request,
        ProjectReleaseSourceMetadata metadata)
    {
        if (!string.Equals(metadata.TopologyId, topologyId, StringComparison.Ordinal)
            || metadata.ProductionLine is null
            || !string.Equals(
                metadata.ProductionLine.LineDefinitionId,
                request.ProductionLineDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                metadata.ProductionLine.TopologyId,
                topologyId,
                StringComparison.Ordinal))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseSourceIdentityMismatch",
                "Resolved release source identity does not match the requested project application resources.");
        }

        if (metadata.LayoutIds is null || metadata.LayoutIds.Count == 0)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseLayoutsMissing",
                "Resolved release source does not contain a site layout.");
        }

        if (metadata.ProductionLine.Operations is null
            || metadata.ProductionLine.Operations.Count == 0
            || metadata.ProductionLine.Transitions is null
            || metadata.ProductionLine.ProductModel is null
            || string.IsNullOrWhiteSpace(metadata.ProductionLine.EntryOperationId)
            || metadata.ProductionLine.Operations.All(operation => !string.Equals(
                operation.OperationId,
                metadata.ProductionLine.EntryOperationId,
                StringComparison.Ordinal)))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseProductionLineInvalid",
                "Resolved release source does not contain complete frozen Production line semantics.");
        }

        if (metadata.ExternalProgramResources is null
            || metadata.ExternalProgramResources.Any(resource =>
                resource is null
                || string.IsNullOrWhiteSpace(resource.ResourceId)
                || string.IsNullOrWhiteSpace(resource.ContentSha256)
                || resource.Files is null
                || resource.PermissionProfile is null
                || resource.ExecutionLimits is null))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseExternalProgramResourcesInvalid",
                "Resolved release source contains incomplete external program resources.");
        }

        if (metadata.CapabilityBindings is null
            || metadata.CapabilityBindings.Count == 0
            || metadata.CapabilityBindings.Any(binding => binding is null
                || string.IsNullOrWhiteSpace(binding.OwnerSystemId)
                || string.IsNullOrWhiteSpace(binding.OwnerStationSystemId))
            || metadata.CapabilityBindings
                .Select(binding => (binding.OwnerSystemId, binding.CapabilityId))
                .Distinct()
                .Count() != metadata.CapabilityBindings.Count)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseCapabilityBindingsMissing",
                "Resolved release source does not contain a capability binding.");
        }

        if (metadata.TargetReferences is null || metadata.TargetReferences.Count == 0)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseTargetsMissing",
                "Resolved release source does not contain a runtime target.");
        }

        if (metadata.ProductionLine.Operations.Any(operation =>
                string.IsNullOrWhiteSpace(operation.StationSystemId)
                || string.IsNullOrWhiteSpace(operation.FlowDefinitionId)
                || string.IsNullOrWhiteSpace(operation.FlowVersionId)
                || string.IsNullOrWhiteSpace(operation.ConfigurationSnapshotId)
                || string.IsNullOrWhiteSpace(operation.FlowIrSchema)
                || string.IsNullOrWhiteSpace(operation.FlowIrCanonicalJson)
                || !IsSha256(operation.FlowIrSha256)
                || operation.BlockVersionIds is null
                || operation.Resources is null
                || operation.Resources.Count == 0
                || operation.AuthorizedActions is null
                || operation.Resources.Count(resource =>
                    string.Equals(resource.Kind, "Station", StringComparison.Ordinal)
                    && string.Equals(resource.Resolution, "Fixed", StringComparison.Ordinal)
                    && string.Equals(
                        resource.TopologyTargetId,
                        operation.StationSystemId,
                        StringComparison.Ordinal)) != 1))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseProductionOperationInvalid",
                "Every resolved Production operation must contain its Station, configuration, Flow identity, canonical Flow IR, SHA-256, block locks, resources, and authorized actions.");
        }

        if (metadata.ProductionLine.LineControllerAuthorizations is null
            || metadata.ProductionLine.LineControllerAuthorizations.Any(authorization =>
                authorization is null
                || metadata.ProductionLine.Operations.Count(operation => string.Equals(
                    operation.OperationId,
                    authorization.OperationId,
                    StringComparison.Ordinal)) != 1
                || metadata.ProductionLine.Operations
                    .Single(operation => string.Equals(
                        operation.OperationId,
                        authorization.OperationId,
                        StringComparison.Ordinal))
                    .AuthorizedActions.Count(action => string.Equals(
                        action.ActionId,
                        authorization.ActionId,
                        StringComparison.Ordinal)
                        && string.Equals(
                            action.LineControllerAuthorizationId,
                            authorization.AuthorizationId,
                            StringComparison.Ordinal)) != 1))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseLineControllerAuthorizationInvalid",
                "Every Line Controller authorization must bind one exact frozen Operation Flow action.");
        }

        if (metadata.BlockVersionIds is null)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseBlockVersionsMissing",
                "Resolved release source does not contain a block version collection.");
        }

        if (metadata.PackageDependencies is null)
        {
            return ApplicationError.Conflict(
                "Projects.ReleasePackageDependenciesMissing",
                "Resolved release source does not contain a package dependency lock collection.");
        }

        return metadata.PackageDependencies.All(dependency => metadata.CapabilityBindings.Count(binding =>
                   string.Equals(binding.CapabilityId, dependency.CapabilityId, StringComparison.Ordinal)
                   && string.Equals(binding.BindingId, dependency.BindingId, StringComparison.Ordinal)
                   && string.Equals(binding.ProviderKind, dependency.ProviderKind, StringComparison.Ordinal)
                   && string.Equals(binding.ProviderKey, dependency.ProviderKey, StringComparison.Ordinal)
                   && string.Equals(binding.OwnerSystemId, dependency.OwnerSystemId, StringComparison.Ordinal)
                   && string.Equals(
                       binding.OwnerStationSystemId,
                       dependency.OwnerStationSystemId,
                       StringComparison.Ordinal)) == 1)
            ? null
            : ApplicationError.Conflict(
                "Projects.ReleasePackageDependencyCoverageMismatch",
                "Every frozen package dependency lock must match exactly one capability binding.");
    }

    private static ApplicationError? ValidateReleaseDescriptor(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        ProjectReleaseArtifactDescriptor release)
    {
        if (!string.Equals(release.SnapshotId, snapshotId, StringComparison.Ordinal)
            || !string.Equals(release.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            || !string.Equals(release.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseArtifactIdentityMismatch",
                "Published release artifact identity does not match its project snapshot request.");
        }

        if (string.IsNullOrWhiteSpace(release.ManifestPath)
            || string.IsNullOrWhiteSpace(release.ContentSha256)
            || string.IsNullOrWhiteSpace(release.SourceRootPath))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseArtifactDescriptorInvalid",
                "Published release artifact descriptor is missing its source root, manifest path, or content SHA-256.");
        }

        return null;
    }

    private static ApplicationError? ValidateStationPackages(
        ProjectReleaseArtifactDescriptor release,
        ProjectReleaseSourceMetadata metadata,
        ProjectReleaseStationPackageSet packages)
    {
        var expectedStations = metadata.ProductionLine.Operations
            .Select(operation => operation.StationSystemId)
            .Concat(metadata.ProductionLine.LineControllerAuthorizations.Select(
                authorization => authorization.TargetStationSystemId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualStations = packages.Packages
            .Select(package => package.StationSystemId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(packages.ProjectId, release.ProjectId, StringComparison.Ordinal)
            || !string.Equals(packages.ApplicationId, release.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(packages.ProjectSnapshotId, release.SnapshotId, StringComparison.Ordinal)
            || !expectedStations.SequenceEqual(actualStations, StringComparer.Ordinal)
            || packages.Packages.Any(package =>
                !IsSha256(package.PackageContentSha256)
                || string.IsNullOrWhiteSpace(package.PackagePath)
                || string.IsNullOrWhiteSpace(package.DeploymentCatalogPath)))
        {
            return ApplicationError.Conflict(
                "Projects.StationPackagePublicationInvalid",
                "Published Station packages do not exactly cover the frozen Production line Stations.");
        }

        return null;
    }

    private static bool SemanticMetadataEquals(
        ProjectReleaseSourceMetadata left,
        ProjectReleaseSourceMetadata right)
    {
        if (!string.Equals(left.TopologyId, right.TopologyId, StringComparison.Ordinal)
            || !ProductionLineEquals(left.ProductionLine, right.ProductionLine)
            || !JsonSerializer.SerializeToUtf8Bytes(left.ExternalProgramResources).AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(right.ExternalProgramResources)))
        {
            return false;
        }

        if (!SequenceEqualOrdinal(left.LayoutIds, right.LayoutIds)
            || !SequenceEqualOrdinal(left.BlockVersionIds, right.BlockVersionIds)
            || !PackageDependenciesEqual(left.PackageDependencies, right.PackageDependencies))
        {
            return false;
        }

        var leftBindings = left.CapabilityBindings
            .OrderBy(binding => binding.OwnerSystemId, StringComparer.Ordinal)
            .ThenBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ThenBy(binding => binding.OwnerStationSystemId, StringComparer.Ordinal);
        var rightBindings = right.CapabilityBindings
            .OrderBy(binding => binding.OwnerSystemId, StringComparer.Ordinal)
            .ThenBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ThenBy(binding => binding.OwnerStationSystemId, StringComparer.Ordinal);
        if (!leftBindings.SequenceEqual(rightBindings))
        {
            return false;
        }

        var leftTargets = left.TargetReferences
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal);
        var rightTargets = right.TargetReferences
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal);

        return leftTargets.SequenceEqual(rightTargets);
    }

    private static bool ProductionLineEquals(
        ProjectReleaseProductionLine left,
        ProjectReleaseProductionLine right)
    {
        return JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(right));
    }

    private static bool PackageDependenciesEqual(
        IReadOnlyCollection<ProjectReleasePackageDependencyLock> left,
        IReadOnlyCollection<ProjectReleasePackageDependencyLock> right)
    {
        var leftLocks = left
            .OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ThenBy(item => item.BindingId, StringComparer.Ordinal)
            .ToArray();
        var rightLocks = right
            .OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ThenBy(item => item.BindingId, StringComparer.Ordinal)
            .ToArray();
        if (leftLocks.Length != rightLocks.Length)
        {
            return false;
        }

        for (var index = 0; index < leftLocks.Length; index++)
        {
            var leftLock = leftLocks[index];
            var rightLock = rightLocks[index];
            if (!string.Equals(leftLock.CapabilityId, rightLock.CapabilityId, StringComparison.Ordinal)
                || !string.Equals(leftLock.BindingId, rightLock.BindingId, StringComparison.Ordinal)
                || !string.Equals(leftLock.ProviderKind, rightLock.ProviderKind, StringComparison.Ordinal)
                || !string.Equals(leftLock.ProviderKey, rightLock.ProviderKey, StringComparison.Ordinal)
                || !string.Equals(leftLock.PackageId, rightLock.PackageId, StringComparison.Ordinal)
                || !string.Equals(leftLock.PluginId, rightLock.PluginId, StringComparison.Ordinal)
                || !string.Equals(leftLock.PackageVersion, rightLock.PackageVersion, StringComparison.Ordinal)
                || !string.Equals(leftLock.PackageContentSha256, rightLock.PackageContentSha256, StringComparison.Ordinal)
                || !string.Equals(leftLock.ManifestSha256, rightLock.ManifestSha256, StringComparison.Ordinal)
                || !string.Equals(leftLock.EntryAssemblySha256, rightLock.EntryAssemblySha256, StringComparison.Ordinal)
                || !string.Equals(leftLock.ContractVersion, rightLock.ContractVersion, StringComparison.Ordinal)
                || !string.Equals(leftLock.RuntimeIdentifier, rightLock.RuntimeIdentifier, StringComparison.Ordinal)
                || !string.Equals(leftLock.AbiVersion, rightLock.AbiVersion, StringComparison.Ordinal)
                || !string.Equals(leftLock.PackageRelativePath, rightLock.PackageRelativePath, StringComparison.Ordinal)
                || !string.Equals(leftLock.ManifestRelativePath, rightLock.ManifestRelativePath, StringComparison.Ordinal)
                || !string.Equals(leftLock.EntryAssemblyRelativePath, rightLock.EntryAssemblyRelativePath, StringComparison.Ordinal))
            {
                return false;
            }

            var leftCommands = leftLock.Commands
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.CommandDefinitionId, StringComparer.Ordinal);
            var rightCommands = rightLock.Commands
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.CommandDefinitionId, StringComparer.Ordinal);
            var leftFiles = leftLock.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal);
            var rightFiles = rightLock.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal);
            if (!leftCommands.SequenceEqual(rightCommands) || !leftFiles.SequenceEqual(rightFiles))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSha256(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Length == 64
               && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
               && value.All(Uri.IsHexDigit);
    }

    private static bool SequenceEqualOrdinal(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        return left
            .Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static Result<string> GetProjectRelativeManifestPath(
        ProjectApplicationWorkspaceScope scope,
        string manifestPath)
    {
        try
        {
            var projectRoot = Path.GetFullPath(scope.ProjectPath);
            var fullManifestPath = Path.GetFullPath(manifestPath);
            var relativePath = Path.GetRelativePath(projectRoot, fullManifestPath);
            if (Path.IsPathRooted(relativePath)
                || string.Equals(relativePath, "..", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return Result.Failure<string>(ApplicationError.Conflict(
                    "Projects.ReleaseArtifactPathOutsideProject",
                    $"Release manifest path '{manifestPath}' is outside project root '{scope.ProjectPath}'."));
            }

            return Result.Success(relativePath.Replace('\\', '/'));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return Result.Failure<string>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactDescriptorInvalid",
                exception.Message));
        }
    }

    private static bool IsArtifactStorageException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException;
    }

    private sealed class ProjectReleasePublicationTransaction : IAsyncDisposable
    {
        private readonly IAutomationProjectWorkspaceService _workspaceService;
        private readonly IProjectReleaseArtifactStore _artifactStore;
        private readonly IProjectReleaseStationPackagePublisher _stationPackagePublisher;
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly ProjectReleaseArtifactDescriptor _release;
        private ProjectReleaseStationPackageSet? _stationPackages;
        private bool _aggregateMutationPossible;
        private bool _committed;

        public ProjectReleasePublicationTransaction(
            IAutomationProjectWorkspaceService workspaceService,
            IProjectReleaseArtifactStore artifactStore,
            IProjectReleaseStationPackagePublisher stationPackagePublisher,
            ProjectApplicationWorkspaceScope scope,
            ProjectReleaseArtifactDescriptor release)
        {
            _workspaceService = workspaceService;
            _artifactStore = artifactStore;
            _stationPackagePublisher = stationPackagePublisher;
            _scope = scope;
            _release = release;
        }

        public void SetStationPackages(ProjectReleaseStationPackageSet packages)
        {
            ArgumentNullException.ThrowIfNull(packages);
            if (_stationPackages is not null)
            {
                throw new InvalidOperationException("Station packages were already attached to the release transaction.");
            }

            _stationPackages = packages;
        }

        public void MarkAggregateMutationPossible() => _aggregateMutationPossible = true;

        public void Commit() => _committed = true;

        public async ValueTask DisposeAsync()
        {
            if (_committed)
            {
                return;
            }

            var rollbackErrors = new List<string>();
            if (_aggregateMutationPossible)
            {
                try
                {
                    var restore = await _workspaceService.OpenAsync(
                            new OpenAutomationProjectWorkspaceRequest(_scope.ProjectPath),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (restore.IsFailure)
                    {
                        rollbackErrors.Add($"Aggregate restore failed: {restore.Error.Message}");
                    }
                }
                catch (Exception exception) when (IsArtifactStorageException(exception))
                {
                    rollbackErrors.Add($"Aggregate restore failed: {exception.Message}");
                }
            }

            if (_stationPackages is not null)
            {
                try
                {
                    await _stationPackagePublisher.RollbackAsync(
                            _stationPackages,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsArtifactStorageException(exception))
                {
                    rollbackErrors.Add($"Station package rollback failed: {exception.Message}");
                }
            }

            try
            {
                await _artifactStore.RollbackPublicationAsync(
                        _scope,
                        _release.SnapshotId,
                        _release.ContentSha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsArtifactStorageException(exception))
            {
                rollbackErrors.Add($"Release rollback failed: {exception.Message}");
            }

            if (rollbackErrors.Count != 0)
            {
                throw new IOException(
                    $"Project release transaction rollback failed. {string.Join(' ', rollbackErrors)}");
            }
        }
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }
}
