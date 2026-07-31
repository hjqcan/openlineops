using Microsoft.Data.Sqlite;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class SqliteExternalPluginProcessEventLogTests
{
    private const string ProjectId = "project.alpha";
    private const string ApplicationId = "application.alpha";
    private static readonly string PackageHash = new('a', 64);

    [Fact]
    public async Task ListAsyncPersistsAndReturnsOnlyExactScopedPackageEvents()
    {
        using var database = TestDatabase.Create();
        using var log = new SqliteExternalPluginProcessEventLog(database.ConnectionString);
        var occurred = new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero);
        log.Record(Event(ProjectId, ApplicationId, PackageHash, "plugin.alpha", occurred));
        log.Record(Event("project.other", ApplicationId, PackageHash, "plugin.alpha", occurred));
        log.Record(Event(ProjectId, "application.other", PackageHash, "plugin.alpha", occurred));
        log.Record(Event(ProjectId, ApplicationId, new string('b', 64), "plugin.alpha", occurred));

        var events = await log.ListAsync(Query());

        var processEvent = Assert.Single(events);
        Assert.Equal(ProjectId, processEvent.ProjectId);
        Assert.Equal(ApplicationId, processEvent.ApplicationId);
        Assert.Equal(PackageHash, processEvent.PackageContentSha256);
        Assert.Equal("plugin.alpha", processEvent.PluginId);
        Assert.Equal(occurred, processEvent.OccurredAtUtc);
    }

    [Fact]
    public async Task ListAsyncFiltersKindAndUsesStableTimeOrdering()
    {
        using var database = TestDatabase.Create();
        using var log = new SqliteExternalPluginProcessEventLog(database.ConnectionString);
        var first = new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);
        log.Record(Event(ProjectId, ApplicationId, PackageHash, "plugin.alpha", first,
            ExternalPluginProcessEventKind.Starting));
        log.Record(Event(ProjectId, ApplicationId, PackageHash, "plugin.alpha", first.AddSeconds(1),
            ExternalPluginProcessEventKind.Started));

        var events = await log.ListAsync(Query(ExternalPluginProcessEventKind.Started));

        var processEvent = Assert.Single(events);
        Assert.Equal(ExternalPluginProcessEventKind.Started, processEvent.Kind);
    }

    [Theory]
    [InlineData(" project.alpha ", "application.alpha")]
    [InlineData("project.alpha", " application.alpha ")]
    public async Task ListAsyncRejectsNonCanonicalScope(string projectId, string applicationId)
    {
        using var database = TestDatabase.Create();
        using var log = new SqliteExternalPluginProcessEventLog(database.ConnectionString);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await log.ListAsync(new ExternalPluginProcessEventQuery(
                projectId,
                applicationId,
                PackageHash)));
    }

    [Fact]
    public void RecordRejectsInvalidPackageHash()
    {
        using var database = TestDatabase.Create();
        using var log = new SqliteExternalPluginProcessEventLog(database.ConnectionString);

        Assert.Throws<ArgumentException>(() => log.Record(Event(
            ProjectId,
            ApplicationId,
            "invalid",
            "plugin.alpha",
            DateTimeOffset.UtcNow)));
    }

    private static ExternalPluginProcessEvent Event(
        string projectId,
        string applicationId,
        string hash,
        string pluginId,
        DateTimeOffset occurredAtUtc,
        ExternalPluginProcessEventKind kind = ExternalPluginProcessEventKind.Started) => new(
        kind,
        projectId,
        applicationId,
        pluginId,
        hash,
        $"{kind} {pluginId}",
        occurredAtUtc,
        "detail");

    private static ExternalPluginProcessEventQuery Query(
        ExternalPluginProcessEventKind? kind = null) => new(
        ProjectId,
        ApplicationId,
        PackageHash,
        kind);

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string path)
        {
            Path = path;
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
        }

        public string Path { get; }

        public string ConnectionString { get; }

        public static TestDatabase Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"openlineops-plugin-events-{Guid.NewGuid():N}.sqlite");
            return new TestDatabase(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
