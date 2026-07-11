using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Engineering.Api.DependencyInjection;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Processes.Api.DependencyInjection;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.StationRuntime;

internal static class StationOperationExecutor
{
    public static async ValueTask<StationOperationResultDocument> ExecuteAsync(
        StationOperationRequestDocument request,
        string workDirectory,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var releaseReader = (IInstalledProjectReleaseReader)new FileSystemProjectReleaseArtifactStore();
        var release = await releaseReader.OpenAsync(
                request.PackageContentDirectory,
                request.ProjectId,
                request.ApplicationId,
                request.ProjectSnapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        var operation = ResolveOperation(release, request);
        var configuration = BuildConfiguration(workDirectory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IAutomationProjectRepository>(
            new ImmutableStationProjectRepositoryAdapter(release));
        services.AddSingleton<IProjectReleaseArtifactStore>(
            new ImmutableStationReleaseArtifactStoreAdapter(release));
        services.AddSingleton<IProjectApplicationWorkspaceScopeResolver>(
            new ImmutableStationWorkspaceScopeResolver(release));
        services.AddSingleton<IProjectReleasePluginCommandResolver, ProjectReleasePluginCommandResolver>();
        services.AddOpenLineOpsEngineeringModule();
        services.AddOpenLineOpsProcessesModule();
        services.AddOpenLineOpsRuntimeModule(configuration);
        services.Replace(ServiceDescriptor.Singleton<IResourceLeaseRepository>(
            new AgentResourceLeaseFenceRepository(request)));
        services.AddOpenLineOpsPluginsModule(configuration);
        services.AddOpenLineOpsDevicesModule(configuration);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        await using var scope = provider.CreateAsyncScope();
        await ValidateConfigurationAsync(
                scope.ServiceProvider,
                release,
                operation,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        var process = ResolveProcess(scope.ServiceProvider, operation);
        var sessionId = new RuntimeSessionId(request.RuntimeSessionId);
        var runner = scope.ServiceProvider.GetRequiredService<IRuntimeSessionRunner>();
        _ = await runner.RunAsync(
                new StartRuntimeSessionRequest(
                    sessionId,
                    new StationId(request.StationId),
                    new ConfigurationSnapshotId(request.ConfigurationSnapshotId),
                    new RecipeSnapshotId(request.RecipeSnapshotId),
                    process,
                    CreateTraceMetadata(request)),
                cancellationToken)
            .ConfigureAwait(false);
        var repository = scope.ServiceProvider.GetRequiredService<IRuntimeSessionRepository>();
        var session = await repository.GetByIdAsync(sessionId, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Runtime Session {request.RuntimeSessionId:D} did not persist its execution evidence.");
        return await StationOperationResultMapper.MapAsync(
                request,
                session,
                workDirectory,
                startedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ProjectReleaseOperation ResolveOperation(
        OpenedProjectReleaseArtifact release,
        StationOperationRequestDocument request)
    {
        var line = release.Metadata.ProductionLine;
        if (!string.Equals(line.LineDefinitionId, request.ProductionLineDefinitionId, StringComparison.Ordinal)
            || !string.Equals(line.TopologyId, request.TopologyId, StringComparison.Ordinal)
            || !string.Equals(
                line.ProductModel.ProductModelId,
                request.ProductModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                line.ProductModel.IdentityInputKey,
                request.ProductionUnitIdentityInputKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station job line, topology, or Product Model identity differs from the frozen release.");
        }

        var matches = line.Operations.Where(candidate =>
                string.Equals(candidate.OperationId, request.OperationId, StringComparison.Ordinal)
                && string.Equals(candidate.StationSystemId, request.StationSystemId, StringComparison.Ordinal)
                && string.Equals(candidate.FlowDefinitionId, request.FlowDefinitionId, StringComparison.Ordinal)
                && string.Equals(candidate.FlowVersionId, request.FlowVersionId, StringComparison.Ordinal)
                && string.Equals(
                    candidate.ConfigurationSnapshotId,
                    request.ConfigurationSnapshotId,
                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "Station job does not resolve to exactly one frozen Operation in its package.");
    }

    private static async ValueTask ValidateConfigurationAsync(
        IServiceProvider services,
        OpenedProjectReleaseArtifact release,
        ProjectReleaseOperation operation,
        StationOperationRequestDocument request,
        CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IProjectEngineeringConfigurationRepository>();
        var releaseScope = new ProjectApplicationWorkspaceScope(
            release.ProjectId,
            release.ApplicationId,
            release.SourceRootPath,
            release.ApplicationProjectRelativePath);
        var matches = (await repository.ListProjectsAsync(releaseScope, cancellationToken)
                .ConfigureAwait(false))
            .SelectMany(project => project.Snapshots)
            .Where(snapshot => string.Equals(
                snapshot.Id.Value,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1
            || !matches[0].IsPublished
            || !string.Equals(
                matches[0].ProcessDefinitionId.Value,
                operation.FlowDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                matches[0].ProcessVersionId.Value,
                operation.FlowVersionId,
                StringComparison.Ordinal)
            || !string.Equals(
                matches[0].RecipeVersionId.Value,
                request.RecipeSnapshotId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station job configuration, Flow, or Recipe identity differs from the frozen release.");
        }
    }

    private static OpenLineOps.Runtime.Application.Processes.ExecutableRuntimeProcess ResolveProcess(
        IServiceProvider services,
        ProjectReleaseOperation operation)
    {
        var serializer = services.GetRequiredService<IFlowIrCanonicalSerializer>();
        var documentResult = serializer.Deserialize(operation.FlowIrCanonicalJson);
        if (documentResult.IsFailure)
        {
            throw new InvalidDataException(
                $"Frozen Flow IR is invalid: {documentResult.Error.Message}");
        }

        var artifactResult = serializer.Serialize(documentResult.Value);
        if (artifactResult.IsFailure
            || !string.Equals(artifactResult.Value.SchemaVersion, operation.FlowIrSchema, StringComparison.Ordinal)
            || !string.Equals(artifactResult.Value.Sha256, operation.FlowIrSha256, StringComparison.Ordinal)
            || !string.Equals(
                artifactResult.Value.CanonicalJson,
                operation.FlowIrCanonicalJson,
                StringComparison.Ordinal)
            || !string.Equals(
                documentResult.Value.ProcessDefinitionId,
                operation.FlowDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                documentResult.Value.ProcessVersionId,
                operation.FlowVersionId,
                StringComparison.Ordinal)
            || !documentResult.Value.BlockDependencies
                .Select(dependency => dependency.LockId)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(operation.BlockVersionIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Frozen Flow IR identity, hash, or dependency inventory differs.");
        }

        var mapped = services.GetRequiredService<IFlowIrExecutableRuntimeProcessMapper>()
            .Map(documentResult.Value);
        return mapped.IsSuccess
            ? mapped.Value
            : throw new InvalidDataException(
                $"Frozen Flow IR cannot execute: {mapped.Error.Message}");
    }

    private static RuntimeSessionTraceMetadata CreateTraceMetadata(
        StationOperationRequestDocument request)
    {
        var fixtureId = SingleResource(request, ResourceKind.Fixture);
        var deviceId = SingleResource(request, ResourceKind.Device);
        return new RuntimeSessionTraceMetadata(
            new ProductionRunId(request.ProductionRunId),
            new ProductionUnitId(request.ProductionUnitId),
            request.ProductionLineDefinitionId,
            request.OperationId,
            request.OperationRunId,
            request.OperationAttempt,
            request.StationSystemId,
            new ProductionUnitIdentity(
                request.ProductModelId,
                request.ProductionUnitIdentityInputKey,
                request.ProductionUnitIdentityValue),
            request.LotId,
            request.CarrierId,
            fixtureId,
            deviceId,
            request.ActorId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ResourceFences.Select(fence => new ResourceLeaseFenceEvidence(
                new ResourceRequirement(
                    Enum.Parse<ResourceKind>(fence.ResourceKind, ignoreCase: false),
                    fence.ResourceId),
                fence.FencingToken,
                fence.ExpiresAtUtc)));
    }

    private static string? SingleResource(
        StationOperationRequestDocument request,
        ResourceKind kind)
    {
        var matches = request.ResourceFences
            .Where(fence => string.Equals(
                fence.ResourceKind,
                kind.ToString(),
                StringComparison.Ordinal))
            .Select(fence => fence.ResourceId)
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                $"Station operation Trace supports exactly zero or one {kind} resource.")
        };
    }

    private static IConfigurationRoot BuildConfiguration(string workDirectory)
    {
        var root = Path.GetFullPath(workDirectory);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] = "InMemory",
                ["OpenLineOps:Runtime:Coordination:Provider"] = "InMemory",
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                ["OpenLineOps:Devices:Persistence:Provider"] = "InMemory",
                ["OpenLineOps:Devices:ExternalProgramHost:WorkspaceRootPath"] = Path.Combine(
                    root,
                    "external-program-workspaces"),
                ["OpenLineOps:Devices:ExternalProgramHost:EvidenceRootPath"] = Path.Combine(
                    root,
                    "evidence"),
                ["OpenLineOps:Devices:ExternalProgramHost:RequireRestrictedHostIdentity"] = "false",
                ["OpenLineOps:Devices:ExternalProgramHost:RequireImmutableContentProtection"] = "true"
            })
            .AddEnvironmentVariables()
            .Build();
    }
}
