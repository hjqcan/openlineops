namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed record ExternalPluginProcessEventQuery(
    string ProjectId,
    string ApplicationId,
    string PackageContentSha256,
    ExternalPluginProcessEventKind? Kind = null,
    DateTimeOffset? OccurredFromUtc = null,
    DateTimeOffset? OccurredToUtc = null,
    int Skip = 0,
    int Take = 100);
