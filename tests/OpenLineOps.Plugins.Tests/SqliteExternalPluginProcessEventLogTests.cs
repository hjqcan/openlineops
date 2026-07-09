using System.Globalization;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class SqliteExternalPluginProcessEventLogTests
{
    [Fact]
    public async Task RecordPersistsProcessEventsForNewLogInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        var firstEventTime = DateTimeOffset.Parse("2026-06-29T08:00:00Z", CultureInfo.InvariantCulture);
        var secondEventTime = DateTimeOffset.Parse("2026-06-29T08:00:01Z", CultureInfo.InvariantCulture);

        using (var log = new SqliteExternalPluginProcessEventLog(database.ConnectionString))
        {
            log.Record(new ExternalPluginProcessEvent(
                ExternalPluginProcessEventKind.TrustRejected,
                "plugin-alpha",
                "Plugin package trust policy rejected activation.",
                firstEventTime,
                "sha256-not-configured"));
            log.Record(new ExternalPluginProcessEvent(
                ExternalPluginProcessEventKind.CommandTimedOut,
                "plugin-beta",
                "External plugin process 'plugin-beta' command timed out after 50ms.",
                secondEventTime));
        }

        using var restartedLog = new SqliteExternalPluginProcessEventLog(database.ConnectionString);

        var events = await restartedLog.ListAsync();

        Assert.Collection(
            events,
            processEvent =>
            {
                Assert.Equal(ExternalPluginProcessEventKind.TrustRejected, processEvent.Kind);
                Assert.Equal("plugin-alpha", processEvent.PluginId);
                Assert.Equal("Plugin package trust policy rejected activation.", processEvent.Message);
                Assert.Equal(firstEventTime, processEvent.OccurredAtUtc);
                Assert.Equal("sha256-not-configured", processEvent.Detail);
            },
            processEvent =>
            {
                Assert.Equal(ExternalPluginProcessEventKind.CommandTimedOut, processEvent.Kind);
                Assert.Equal("plugin-beta", processEvent.PluginId);
                Assert.Null(processEvent.Detail);
            });
    }

    [Fact]
    public async Task ListAsyncFiltersByPluginKindTimeAndUsesStablePagination()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var log = new SqliteExternalPluginProcessEventLog(database.ConnectionString);
        var baseTime = DateTimeOffset.Parse("2026-06-29T09:00:00Z", CultureInfo.InvariantCulture);
        log.Record(new ExternalPluginProcessEvent(
            ExternalPluginProcessEventKind.Starting,
            "plugin-alpha",
            "starting",
            baseTime));
        log.Record(new ExternalPluginProcessEvent(
            ExternalPluginProcessEventKind.Started,
            "plugin-alpha",
            "started",
            baseTime.AddSeconds(1)));
        log.Record(new ExternalPluginProcessEvent(
            ExternalPluginProcessEventKind.Started,
            "plugin-beta",
            "started",
            baseTime.AddSeconds(2)));
        log.Record(new ExternalPluginProcessEvent(
            ExternalPluginProcessEventKind.ProcessKilled,
            "plugin-alpha",
            "killed",
            baseTime.AddSeconds(3)));

        var events = await log.ListAsync(new ExternalPluginProcessEventQuery(
            PluginId: "plugin-alpha",
            Kind: ExternalPluginProcessEventKind.Started,
            OccurredFromUtc: baseTime.AddMilliseconds(500),
            OccurredToUtc: baseTime.AddSeconds(2),
            Skip: 0,
            Take: 1));

        var processEvent = Assert.Single(events);
        Assert.Equal(ExternalPluginProcessEventKind.Started, processEvent.Kind);
        Assert.Equal("plugin-alpha", processEvent.PluginId);
        Assert.Equal("started", processEvent.Message);
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        private string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "plugins.db");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
