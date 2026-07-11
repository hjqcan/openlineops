using OpenLineOps.Agent.Contracts;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleaseProductionMaterialArrivalAuthorizer(
    IAutomationProjectService projectService,
    IProjectReleaseSnapshotReader releaseReader,
    IStationDeploymentResolver deploymentResolver) :
    IProductionMaterialArrivalAuthorizer
{
    public async ValueTask AuthorizeAsync(
        MaterialArrived message,
        ProductionMaterialArrivalOrigin origin,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        if (!Enum.IsDefined(origin))
        {
            throw Rejected("Material arrival origin is invalid.");
        }

        var projectResult = await projectService
            .GetByIdAsync(message.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            throw Rejected("Material arrival Project is not an opened Automation Project.");
        }

        var matchingSnapshots = projectResult.Value.Snapshots
            .Where(snapshot =>
                string.Equals(snapshot.ProjectId, message.ProjectId, StringComparison.Ordinal)
                && string.Equals(snapshot.ApplicationId, message.ApplicationId, StringComparison.Ordinal)
                && string.Equals(snapshot.SnapshotId, message.ProjectSnapshotId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingSnapshots.Length != 1)
        {
            throw Rejected(
                "Material arrival does not identify one exact published Project/Application snapshot.");
        }

        var releaseResult = await releaseReader
            .OpenAsync(matchingSnapshots[0], cancellationToken)
            .ConfigureAwait(false);
        if (releaseResult.IsFailure)
        {
            throw Rejected("Material arrival immutable Project release is unavailable or invalid.");
        }

        var release = releaseResult.Value.Artifact;
        var line = release.Metadata.ProductionLine;
        if (!string.Equals(release.ProjectId, message.ProjectId, StringComparison.Ordinal)
            || !string.Equals(release.ApplicationId, message.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(release.SnapshotId, message.ProjectSnapshotId, StringComparison.Ordinal)
            || !string.Equals(line.LineDefinitionId, message.LineId, StringComparison.Ordinal)
            || !ProjectReleaseStationDeploymentSet.Resolve(line).Contains(
                message.StationSystemId,
                StringComparer.Ordinal))
        {
            throw Rejected(
                "Material arrival Project release, Production Line, or Station identity is not frozen by the signed deployment.");
        }

        StationDeploymentRoute route;
        try
        {
            route = await deploymentResolver.ResolveAsync(
                    new StationDeploymentRequest(
                        message.ProjectId,
                        message.ApplicationId,
                        message.ProjectSnapshotId,
                        message.StationSystemId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or InvalidDataException
                                           or DirectoryNotFoundException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            throw Rejected("Material arrival Station deployment cannot be verified.", exception);
        }

        if (!string.Equals(
                route.ProductionLineDefinitionId,
                message.LineId,
                StringComparison.Ordinal)
            || !string.Equals(
                route.PackageContentSha256,
                message.PackageContentSha256,
                StringComparison.Ordinal)
            || !string.Equals(route.StationId, message.StationId, StringComparison.Ordinal))
        {
            throw Rejected(
                "Material arrival does not match the exact current Station package deployment.");
        }

        switch (origin)
        {
            case ProductionMaterialArrivalOrigin.CoordinatorApi
                when string.Equals(
                    message.Source,
                    StationMaterialArrivalSources.Api,
                    StringComparison.Ordinal)
                && string.Equals(
                    message.ProducerId,
                    StationMaterialArrivalProducers.CoordinatorApi,
                    StringComparison.Ordinal):
                return;
            case ProductionMaterialArrivalOrigin.StationAgent
                when message.Source is StationMaterialArrivalSources.Manual
                    or StationMaterialArrivalSources.Plc
                && !string.Equals(
                    route.AgentId,
                    StationMaterialArrivalProducers.CoordinatorApi,
                    StringComparison.Ordinal)
                && string.Equals(message.ProducerId, route.AgentId, StringComparison.Ordinal):
                return;
            default:
                throw Rejected(
                    "Material arrival source and Producer do not match the authenticated ingress origin.");
        }
    }

    private static InvalidDataException Rejected(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);
}
