namespace OpenLineOps.Engineering.Infrastructure.Persistence;

internal static class ProjectEngineeringConfigurationDocumentSchema
{
    public const string CurrentSchema = "openlineops.engineering-configuration-resource";

    public const int CurrentSchemaVersion = 1;
}

internal static class ProjectEngineeringResourceKinds
{
    public const string Workspace = "workspace";
    public const string Project = "project";
    public const string Recipe = "recipe";
    public const string StationProfile = "station-profile";
}

internal sealed record ProjectEngineeringConfigurationDocument<TSnapshot>(
    string Schema,
    int SchemaVersion,
    string ProjectId,
    string ApplicationId,
    string ResourceKind,
    string ResourceId,
    TSnapshot Snapshot);
