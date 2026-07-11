namespace OpenLineOps.Projects.Application.Releases;

public static class ProjectReleaseStationDeploymentSet
{
    public static IReadOnlyCollection<string> Resolve(ProjectReleaseProductionLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Operations
            .Select(operation => operation.StationSystemId)
            .Concat(line.LineControllerAuthorizations.Select(authorization =>
                authorization.TargetStationSystemId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
