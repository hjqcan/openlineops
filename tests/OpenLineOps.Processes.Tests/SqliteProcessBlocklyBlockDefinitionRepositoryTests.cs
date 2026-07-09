using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class SqliteProcessBlocklyBlockDefinitionRepositoryTests
{
    private static readonly DateTimeOffset FirstRecordedAtUtc = new(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondRecordedAtUtc = FirstRecordedAtUtc.AddMinutes(5);

    [Fact]
    public async Task SaveNewVersionAsyncPersistsLatestBlockForNewRepositoryInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessBlocklyBlockDefinitionRepository(database.ConnectionString);

        var first = await repository.SaveNewVersionAsync(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """{"type":"user_open_clamp","message0":"open clamp","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'fixture.clamp'})",
            FirstRecordedAtUtc);
        var second = await repository.SaveNewVersionAsync(
            "user_open_clamp",
            "Fixture",
            "Open Clamp Wide",
            """{"type":"user_open_clamp","message0":"open wide clamp","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'fixture.clamp', 'mode': 'wide'})",
            SecondRecordedAtUtc);

        using var restartedRepository = new SqliteProcessBlocklyBlockDefinitionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetLatestAsync("user_open_clamp");
        var latest = await restartedRepository.ListLatestAsync();
        var versions = await restartedRepository.ListVersionsAsync("user_open_clamp");

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(FirstRecordedAtUtc, second.CreatedAtUtc);
        Assert.Equal(SecondRecordedAtUtc, second.UpdatedAtUtc);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.Version);
        Assert.Equal("Open Clamp Wide", restored.DisplayName);
        Assert.Equal(FirstRecordedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(SecondRecordedAtUtc, restored.UpdatedAtUtc);
        Assert.Single(latest);
        Assert.Equal("Open Clamp Wide", latest.Single().DisplayName);
        Assert.Collection(
            versions,
            block => Assert.Equal(2, block.Version),
            block => Assert.Equal(1, block.Version));
    }

    [Fact]
    public async Task GetLatestAsyncReturnsNullForMissingBlock()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessBlocklyBlockDefinitionRepository(database.ConnectionString);

        var restored = await repository.GetLatestAsync("missing_block");

        Assert.Null(restored);
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        public string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "process-blockly-blocks.sqlite");

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
