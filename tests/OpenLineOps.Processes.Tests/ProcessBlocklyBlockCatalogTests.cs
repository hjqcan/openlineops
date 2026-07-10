using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class ProcessBlocklyBlockCatalogTests
{
    private static readonly DateTimeOffset RegisteredAtUtc = new(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);

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
            && block.PythonCodeTemplate.Contains("automation_plan.append", StringComparison.Ordinal)
            && block.ExecutionMode == ProcessBlocklyBlockExecutionModes.DeclarativeActionContract
            && block.RuntimeActionContractSchemaVersion == RuntimeActionContractSchemaVersions.V1
            && block.RuntimeActionContractSha256 is { Length: 64 });
        Assert.Contains(result.Value, block =>
            block.IsBuiltIn
            && block.BlockType == "openlineops_result_from_input");
        var builtIns = result.Value.Where(block => block.IsBuiltIn).ToArray();
        Assert.Equal(5, builtIns.Length);
        var serializer = new RuntimeActionContractCanonicalSerializer();
        Assert.All(
            builtIns,
            block =>
            {
                Assert.Equal(
                    ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
                    block.ExecutionMode);
                Assert.Equal(RuntimeActionContractSchemaVersions.V1, block.RuntimeActionContractSchemaVersion);
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

        var result = await catalog.RegisterAsync(new RegisterProcessBlocklyBlockDefinitionRequest(
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
            "automation_plan.append({'type': 'fixture.clamp', 'clamp': {{CLAMP}}, 'state': 'open'})"));

        var list = await catalog.ListAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsBuiltIn);
        Assert.Equal("user_open_clamp", result.Value.BlockType);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(RegisteredAtUtc, result.Value.CreatedAtUtc);
        Assert.Equal(RegisteredAtUtc, result.Value.UpdatedAtUtc);
        Assert.Equal(ProcessBlocklyBlockExecutionModes.LegacyPythonTemplate, result.Value.ExecutionMode);
        Assert.Null(result.Value.RuntimeActionContractSchemaVersion);
        Assert.Null(result.Value.RuntimeActionContractJson);
        Assert.Null(result.Value.RuntimeActionContractSha256);
        Assert.Contains(list.Value, block =>
            block.BlockType == "user_open_clamp"
            && block.Category == "Fixture");
    }

    [Fact]
    public async Task RegisterAsyncRejectsBlocklyJsonTypeMismatch()
    {
        var catalog = CreateCatalog();

        var result = await catalog.RegisterAsync(new RegisterProcessBlocklyBlockDefinitionRequest(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """{"type":"different_block"}""",
            "automation_plan.append({'type': 'fixture.clamp'})"));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.BlocklyBlockJsonTypeMismatch", result.Error.Code);
    }

    [Fact]
    public async Task RegisterAsyncCreatesNewVersionForExistingUserBlockType()
    {
        var repository = new InMemoryProcessBlocklyBlockDefinitionRepository();
        var catalog = new ProcessBlocklyBlockCatalog(repository, new FixedClock(RegisteredAtUtc));
        var firstRequest = new RegisterProcessBlocklyBlockDefinitionRequest(
            "user_open_clamp",
            "Fixture",
            "Open Clamp",
            """{"type":"user_open_clamp","message0":"open clamp","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'fixture.clamp'})");
        var secondRequest = new RegisterProcessBlocklyBlockDefinitionRequest(
            "user_open_clamp",
            "Fixture",
            "Open Clamp Wide",
            """{"type":"user_open_clamp","message0":"open wide clamp","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'fixture.clamp', 'mode': 'wide'})");

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
        Assert.Collection(
            versions.Value,
            block => Assert.Equal(2, block.Version),
            block => Assert.Equal(1, block.Version));
        Assert.Contains(list.Value, block =>
            block.BlockType == "user_open_clamp"
            && block.DisplayName == "Open Clamp Wide"
            && block.Version == 2);
    }

    [Fact]
    public async Task RegisterAsyncRejectsBuiltInBlockOverride()
    {
        var catalog = CreateCatalog();

        var result = await catalog.RegisterAsync(new RegisterProcessBlocklyBlockDefinitionRequest(
            "openlineops_move_axis",
            "Motion",
            "Move Axis Override",
            """{"type":"openlineops_move_axis","message0":"move axis","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'axis.override'})"));

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
            && block.ExecutionMode == ProcessBlocklyBlockExecutionModes.LegacyPythonTemplate
            && block.RuntimeActionContractJson is null);
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

        var result = await catalog.RegisterAsync(new RegisterProcessBlocklyBlockDefinitionRequest(
            generatedBlock.BlockType,
            "Plugin Device Commands",
            "Override",
            """{"type":"generated_plugin_block","message0":"override","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'override'})"));

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
            new InMemoryProcessBlocklyBlockDefinitionRepository(),
            new FixedClock(RegisteredAtUtc),
            sources);
    }

    private static ProcessBlocklyBlockDefinitionDetails CreateGeneratedBlock()
    {
        return new ProcessBlocklyBlockDefinitionDetails(
            "generated_plugin_block",
            "Plugin Device Commands",
            "Generated Plugin Block",
            """{"type":"generated_plugin_block","message0":"plugin command","previousStatement":null,"nextStatement":null}""",
            "automation_plan.append({'type': 'command.execute'})",
            IsBuiltIn: true,
            Version: 1,
            RegisteredAtUtc,
            RegisteredAtUtc);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StaticBlocklyBlockCatalogSource(
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> blocks) : IProcessBlocklyBlockCatalogSource
    {
        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(blocks);
        }
    }
}
