using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class ProcessBlocklyBlockCatalogTests
{
    private static readonly DateTimeOffset RegisteredAtUtc = new(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task ListAsyncReturnsBuiltInAutomationBlocks()
    {
        var catalog = CreateCatalog();

        var result = await catalog.ListAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, block =>
            block.IsBuiltIn
            && block.BlockType == "openlineops_move_axis"
            && block.Version == 1
            && block.ExecutionMode == ProcessBlocklyBlockExecutionModes.DeclarativeActionContract
            && block.RuntimeActionContractSchemaVersion == RuntimeActionContractSchema.Current
            && block.RuntimeActionContractSha256 is { Length: 64 });
        Assert.Contains(result.Value, block =>
            block.IsBuiltIn
            && block.BlockType == "openlineops_result_from_input");
        Assert.Contains(result.Value, block =>
            block.IsBuiltIn
            && block.BlockType == "openlineops_run_external_program");
        var builtIns = result.Value.Where(block => block.IsBuiltIn).ToArray();
        Assert.Equal(7, builtIns.Length);
        var serializer = new RuntimeActionContractCanonicalSerializer();
        Assert.All(
            builtIns,
            block =>
            {
                Assert.Equal(
                    ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
                    block.ExecutionMode);
                Assert.Equal(RuntimeActionContractSchema.Current, block.RuntimeActionContractSchemaVersion);
                Assert.NotNull(block.RuntimeActionContractJson);
                Assert.NotNull(block.RuntimeActionContractSha256);
                var contract = serializer.Deserialize(block.RuntimeActionContractJson);
                Assert.True(contract.IsSuccess, contract.Error.Message);
                Assert.Equal(
                    block.RuntimeActionContractSha256,
                    serializer.Serialize(contract.Value).Value.Sha256);
            });
    }

    [Fact]
    public async Task RegisterAsyncAddsUserDefinedBlocklyBlock()
    {
        var catalog = CreateCatalog();

        var result = await catalog.RegisterAsync(Registration(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """
            {
              "type": "user_open_clamp",
              "message0": "open clamp %1",
              "args0": [
                {
                  "type": "field_input",
                  "name": "CLAMP",
                  "text": "left"
                }
              ],
              "previousStatement": null,
              "nextStatement": null,
              "colour": 130
            }
            """,
            "test.fixture.open-clamp"));

        var list = await catalog.ListAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsBuiltIn);
        Assert.Equal("user_open_clamp", result.Value.BlockType);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(RegisteredAtUtc, result.Value.CreatedAtUtc);
        Assert.Equal(RegisteredAtUtc, result.Value.UpdatedAtUtc);
        AssertCanonicalContract(result.Value, "test.fixture.open-clamp");
        Assert.Contains(list.Value, block =>
            block.BlockType == "user_open_clamp"
            && block.Category == "Fixture");
    }

    [Fact]
    public async Task RegisterAsyncAcceptsNonCanonicalContractAndReturnsCanonicalHash()
    {
        var catalog = CreateCatalog();
        var expected = CreateContract("test.fixture.noncanonical");
        using var contractDocument = JsonDocument.Parse(expected.CanonicalJson);
        var nonCanonicalJson = JsonSerializer.Serialize(
            contractDocument.RootElement,
            IndentedJsonOptions);

        var result = await catalog.RegisterAsync(new RegisterProcessBlocklyBlockDefinitionRequest(
            "user_noncanonical_contract",
            "Fixture",
            "Noncanonical Contract",
            """{"type":"user_noncanonical_contract","message0":"noncanonical","previousStatement":null,"nextStatement":null}""",
            expected.SchemaVersion,
            nonCanonicalJson));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(expected.SchemaVersion, result.Value.RuntimeActionContractSchemaVersion);
        Assert.Equal(expected.CanonicalJson, result.Value.RuntimeActionContractJson);
        Assert.Equal(expected.Sha256, result.Value.RuntimeActionContractSha256);
        AssertCanonicalContract(result.Value, "test.fixture.noncanonical");
    }

    [Fact]
    public async Task RegisterAsyncRejectsBlocklyJsonTypeMismatch()
    {
        var catalog = CreateCatalog();

        var result = await catalog.RegisterAsync(Registration(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """{"type":"different_block"}""",
            "test.fixture.open-clamp"));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.BlocklyBlockJsonTypeMismatch", result.Error.Code);
    }

    [Fact]
    public async Task RegisterAsyncCreatesNewVersionForExistingUserBlockType()
    {
        var repository = new TestBlockRepository();
        var catalog = new ProcessBlocklyBlockCatalog(repository, new FixedClock(RegisteredAtUtc));
        var firstRequest = Registration(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """{"type":"user_open_clamp","message0":"open clamp","previousStatement":null,"nextStatement":null}""",
            "test.fixture.open-clamp");
        var secondRequest = Registration(
            "user_open_clamp",
            "Fixture",
            "Open Clamp Wide",
            """{"type":"user_open_clamp","message0":"open wide clamp","previousStatement":null,"nextStatement":null}""",
            "test.fixture.open-clamp-wide");

        var first = await catalog.RegisterAsync(firstRequest);
        var second = await catalog.RegisterAsync(secondRequest);
        var latest = await repository.GetLatestAsync("user_open_clamp");
        var list = await catalog.ListAsync();
        var versions = await catalog.ListVersionsAsync("user_open_clamp");

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(versions.IsSuccess);
        Assert.Equal(1, first.Value.Version);
        Assert.Equal(2, second.Value.Version);
        Assert.NotNull(latest);
        Assert.Equal(2, latest.Version);
        Assert.Equal("Open Clamp Wide", latest.DisplayName);
        AssertCanonicalContract(latest, "test.fixture.open-clamp-wide");
        Assert.Collection(
            versions.Value,
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
        Assert.Contains(list.Value, block =>
            block.BlockType == "user_open_clamp"
            && block.DisplayName == "Open Clamp Wide"
            && block.Version == 2);
    }

    [Fact]
    public async Task RegisterAsyncRejectsBuiltInBlockOverride()
    {
        var catalog = CreateCatalog();

        var result = await catalog.RegisterAsync(Registration(
            "openlineops_move_axis",
            "Motion",
            "Move Axis Override",
            """{"type":"openlineops_move_axis","message0":"move axis","previousStatement":null,"nextStatement":null}""",
            "test.motion.axis-override"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Processes.BlocklyBlockBuiltInCannotBeOverwritten", result.Error.Code);
    }

    [Fact]
    public async Task ListAsyncReturnsGeneratedCatalogSourceBlocks()
    {
        var generatedBlock = CreateGeneratedBlock();
        var catalog = CreateCatalog([new StaticBlocklyBlockCatalogSource([generatedBlock])]);

        var result = await catalog.ListAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, block =>
            block.BlockType == generatedBlock.BlockType
            && block.DisplayName == generatedBlock.DisplayName
            && block.IsBuiltIn
            && block.ExecutionMode == ProcessBlocklyBlockExecutionModes.DeclarativeActionContract
            && block.RuntimeActionContractSchemaVersion == RuntimeActionContractSchema.Current
            && block.RuntimeActionContractJson is not null
            && block.RuntimeActionContractSha256 is { Length: 64 });
    }

    [Fact]
    public async Task ListVersionsAsyncReturnsGeneratedCatalogSourceBlock()
    {
        var generatedBlock = CreateGeneratedBlock();
        var catalog = CreateCatalog([new StaticBlocklyBlockCatalogSource([generatedBlock])]);

        var result = await catalog.ListVersionsAsync(generatedBlock.BlockType);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value,
            block =>
            {
                Assert.Equal(generatedBlock.BlockType, block.BlockType);
                Assert.Equal(1, block.Version);
            });
    }

    [Fact]
    public async Task RegisterAsyncRejectsGeneratedCatalogSourceBlockOverride()
    {
        var generatedBlock = CreateGeneratedBlock();
        var catalog = CreateCatalog([new StaticBlocklyBlockCatalogSource([generatedBlock])]);

        var result = await catalog.RegisterAsync(Registration(
            generatedBlock.BlockType,
            "Plugin Device Commands",
            "Override",
            """{"type":"generated_plugin_block","message0":"override","previousStatement":null,"nextStatement":null}""",
            "test.plugin.override"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Processes.BlocklyBlockGeneratedCannotBeOverwritten", result.Error.Code);
    }

    [Fact]
    public async Task ListVersionsAsyncReturnsNotFoundForMissingCustomBlock()
    {
        var catalog = CreateCatalog();

        var result = await catalog.ListVersionsAsync("missing_custom_block");

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound.Processes.BlocklyBlockNotFound", result.Error.Code);
    }

    private static ProcessBlocklyBlockCatalog CreateCatalog(
        IEnumerable<IProcessBlocklyBlockCatalogSource>? sources = null)
    {
        return new ProcessBlocklyBlockCatalog(
            new TestBlockRepository(),
            new FixedClock(RegisteredAtUtc),
            sources,
            new ProjectApplicationWorkspaceScope(
                "project.test",
                "application.test",
                Path.Combine(Path.GetTempPath(), "openlineops-blockly-tests"),
                "applications/test/test.oloapp"));
    }

    private static RegisterProcessBlocklyBlockDefinitionRequest Registration(
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string actionType)
    {
        var contract = CreateContract(actionType, blocklyJson);
        return new RegisterProcessBlocklyBlockDefinitionRequest(
            blockType,
            category,
            displayName,
            blocklyJson,
            contract.SchemaVersion,
            contract.CanonicalJson);
    }

    private static RuntimeActionContractCanonicalArtifact CreateContract(
        string actionType,
        string? blocklyJson = null)
    {
        var fields = new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal);
        if (blocklyJson is not null)
        {
            using var definition = JsonDocument.Parse(blocklyJson);
            if (definition.RootElement.TryGetProperty("args0", out var args))
            {
                foreach (var argument in args.EnumerateArray())
                {
                    if (argument.TryGetProperty("name", out var name))
                    {
                        fields[name.GetString()!] = new RuntimeActionFieldDefinition(
                            RuntimeActionFieldType.Text,
                            Required: true,
                            MaxLength: 256);
                    }
                }
            }
        }

        var result = new RuntimeActionContractCanonicalSerializer().Serialize(new RuntimeActionContract(
            RuntimeActionContractSchema.Current,
            actionType,
            fields,
            new RuntimeDelayEmit(new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(1)))));

        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private static void AssertCanonicalContract(
        ProcessBlocklyBlockDefinitionDetails block,
        string expectedActionType)
    {
        Assert.Equal(ProcessBlocklyBlockExecutionModes.DeclarativeActionContract, block.ExecutionMode);
        Assert.Equal(RuntimeActionContractSchema.Current, block.RuntimeActionContractSchemaVersion);
        Assert.NotNull(block.RuntimeActionContractJson);
        Assert.NotNull(block.RuntimeActionContractSha256);
        var serializer = new RuntimeActionContractCanonicalSerializer();
        var parsed = serializer.Deserialize(block.RuntimeActionContractJson);
        Assert.True(parsed.IsSuccess, parsed.Error.Message);
        Assert.Equal(expectedActionType, parsed.Value.ActionType);
        var canonical = serializer.Serialize(parsed.Value);
        Assert.True(canonical.IsSuccess, canonical.Error.Message);
        Assert.Equal(block.RuntimeActionContractJson, canonical.Value.CanonicalJson);
        Assert.Equal(block.RuntimeActionContractSha256, canonical.Value.Sha256);
    }

    private static void AssertCanonicalContract(
        OpenLineOps.Processes.Application.Persistence.ProcessBlocklyBlockDefinitionRecord block,
        string expectedActionType)
    {
        Assert.Equal(ProcessBlocklyBlockExecutionModes.DeclarativeActionContract, block.ExecutionMode);
        Assert.Equal(RuntimeActionContractSchema.Current, block.RuntimeActionContractSchemaVersion);
        var serializer = new RuntimeActionContractCanonicalSerializer();
        var parsed = serializer.Deserialize(block.RuntimeActionContractJson);
        Assert.True(parsed.IsSuccess, parsed.Error.Message);
        Assert.Equal(expectedActionType, parsed.Value.ActionType);
        var canonical = serializer.Serialize(parsed.Value);
        Assert.True(canonical.IsSuccess, canonical.Error.Message);
        Assert.Equal(block.RuntimeActionContractJson, canonical.Value.CanonicalJson);
        Assert.Equal(block.RuntimeActionContractSha256, canonical.Value.Sha256);
    }

    private static ProcessBlocklyBlockDefinitionDetails CreateGeneratedBlock()
    {
        var contract = CreateContract("test.plugin.generated-command");
        return new ProcessBlocklyBlockDefinitionDetails(
            "generated_plugin_block",
            "Plugin Device Commands",
            "Generated Plugin Block",
            """{"type":"generated_plugin_block","message0":"plugin command","previousStatement":null,"nextStatement":null}""",
            IsBuiltIn: true,
            Version: 1,
            RegisteredAtUtc,
            RegisteredAtUtc,
            ExecutionMode: ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            RuntimeActionContractSchemaVersion: contract.SchemaVersion,
            RuntimeActionContractJson: contract.CanonicalJson,
            RuntimeActionContractSha256: contract.Sha256);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StaticBlocklyBlockCatalogSource(
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> blocks) : IProcessBlocklyBlockCatalogSource
    {
        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>> ListAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(blocks);
        }
    }

    private sealed class TestBlockRepository : IProcessBlocklyBlockDefinitionRepository
    {
        private readonly Dictionary<string, List<ProcessBlocklyBlockDefinitionRecord>> _blocks =
            new(StringComparer.Ordinal);

        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>>(
                _blocks.Values
                    .Select(versions => versions.MaxBy(block => block.Version)!)
                    .OrderBy(block => block.BlockType, StringComparer.Ordinal)
                    .ToArray());
        }

        public ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
            string blockType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_blocks.TryGetValue(blockType, out var versions)
                ? versions.MaxBy(block => block.Version)
                : null);
        }

        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
            string blockType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>>(
                _blocks.TryGetValue(blockType, out var versions)
                    ? versions.OrderByDescending(block => block.Version).ToArray()
                    : []);
        }

        public ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
            string blockType,
            string category,
            string displayName,
            string blocklyJson,
            string executionMode,
            string runtimeActionContractSchemaVersion,
            string runtimeActionContractJson,
            string runtimeActionContractSha256,
            DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_blocks.TryGetValue(blockType, out var versions))
            {
                versions = [];
                _blocks.Add(blockType, versions);
            }

            var latest = versions.MaxBy(block => block.Version);
            var record = new ProcessBlocklyBlockDefinitionRecord(
                blockType,
                category,
                displayName,
                blocklyJson,
                executionMode,
                runtimeActionContractSchemaVersion,
                runtimeActionContractJson,
                runtimeActionContractSha256,
                latest?.Version + 1 ?? 1,
                latest?.CreatedAtUtc ?? recordedAtUtc,
                recordedAtUtc);
            versions.Add(record);
            return ValueTask.FromResult(record);
        }
    }
}
