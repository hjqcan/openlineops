using System.Text.Json;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
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

        var firstContract = CreateContract("test.fixture.open-clamp");
        var first = await repository.SaveNewVersionAsync(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """{"type":"user_open_clamp","message0":"open clamp","previousStatement":null,"nextStatement":null}""",
            ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            firstContract.SchemaVersion,
            firstContract.CanonicalJson,
            firstContract.Sha256,
            FirstRecordedAtUtc);
        var secondContract = CreateContract("test.fixture.open-clamp-wide");
        var second = await repository.SaveNewVersionAsync(
            "user_open_clamp",
            "Fixture",
            "Open Clamp Wide",
            """{"type":"user_open_clamp","message0":"open wide clamp","previousStatement":null,"nextStatement":null}""",
            ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            secondContract.SchemaVersion,
            secondContract.CanonicalJson,
            secondContract.Sha256,
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
        AssertCanonicalContract(restored, "test.fixture.open-clamp-wide");
        Assert.Single(latest);
        Assert.Equal("Open Clamp Wide", latest.Single().DisplayName);
        Assert.Collection(
            versions,
            block =>
            {
                Assert.Equal(2, block.Version);
                AssertCanonicalContract(block, "test.fixture.open-clamp-wide");
            },
            block =>
            {
                Assert.Equal(1, block.Version);
                AssertCanonicalContract(block, "test.fixture.open-clamp");
            });
    }

    [Fact]
    public async Task GetLatestAsyncReturnsNullForMissingBlock()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessBlocklyBlockDefinitionRepository(database.ConnectionString);

        var restored = await repository.GetLatestAsync("missing_block");

        Assert.Null(restored);
    }

    private static RuntimeActionContractCanonicalArtifact CreateContract(string actionType)
    {
        var result = new RuntimeActionContractCanonicalSerializer().Serialize(new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            actionType,
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal),
            new RuntimeDelayEmit(new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(1)))));

        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private static void AssertCanonicalContract(
        ProcessBlocklyBlockDefinitionRecord block,
        string expectedActionType)
    {
        Assert.Equal(ProcessBlocklyBlockExecutionModes.DeclarativeActionContract, block.ExecutionMode);
        Assert.Equal(RuntimeActionContractSchemaVersions.V1, block.RuntimeActionContractSchemaVersion);
        var serializer = new RuntimeActionContractCanonicalSerializer();
        var parsed = serializer.Deserialize(block.RuntimeActionContractJson);
        Assert.True(parsed.IsSuccess, parsed.Error.Message);
        Assert.Equal(expectedActionType, parsed.Value.ActionType);
        var canonical = serializer.Serialize(parsed.Value);
        Assert.True(canonical.IsSuccess, canonical.Error.Message);
        Assert.Equal(block.RuntimeActionContractJson, canonical.Value.CanonicalJson);
        Assert.Equal(block.RuntimeActionContractSha256, canonical.Value.Sha256);
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
