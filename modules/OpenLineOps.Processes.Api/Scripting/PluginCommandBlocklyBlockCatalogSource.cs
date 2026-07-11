using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Processes.Application.Scripting;

namespace OpenLineOps.Processes.Api.Scripting;

internal sealed partial class PluginCommandBlocklyBlockCatalogSource : IProcessBlocklyBlockCatalogSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RuntimeActionContractCanonicalSerializer ContractSerializer = new();
    private static readonly DateTimeOffset GeneratedRecordedAtUtc = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly IServiceProvider _serviceProvider;

    public PluginCommandBlocklyBlockCatalogSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var blocks = new Dictionary<string, ProcessBlocklyBlockDefinitionDetails>(StringComparer.Ordinal);

        var deviceInventory = ResolveDeviceCommandInventory();
        if (deviceInventory is not null)
        {
            var commands = await deviceInventory
                .ListDeviceCommandsAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var command in commands)
            {
                var block = CreateDeviceCommandBlock(command);
                blocks.TryAdd(block.BlockType, block);
            }
        }

        var processInventory = ResolveProcessCommandInventory();
        if (processInventory is not null)
        {
            var commands = await processInventory
                .ListProcessCommandsAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var command in commands)
            {
                var block = CreateProcessCommandBlock(command);
                blocks.TryAdd(block.BlockType, block);
            }
        }

        return blocks.Values
            .OrderBy(block => block.Category, StringComparer.Ordinal)
            .ThenBy(block => block.DisplayName, StringComparer.Ordinal)
            .ThenBy(block => block.BlockType, StringComparer.Ordinal)
            .ToArray();
    }

    private IPluginDeviceCommandInventory? ResolveDeviceCommandInventory()
    {
        return _serviceProvider.GetService<IPluginDeviceCommandInventory>()
            ?? _serviceProvider.GetService<PluginDeviceCommandInventory>();
    }

    private IPluginProcessCommandInventory? ResolveProcessCommandInventory()
    {
        return _serviceProvider.GetService<IPluginProcessCommandInventory>()
            ?? _serviceProvider.GetService<PluginProcessCommandInventory>();
    }

    private static ProcessBlocklyBlockDefinitionDetails CreateDeviceCommandBlock(
        PluginDeviceCommandDescriptor command)
    {
        return CreateCommandBlock(new GeneratedPluginCommand(
            "device",
            "Plugin Device Commands",
            260,
            command.PluginId,
            command.PluginName,
            command.PluginKind.ToString(),
            command.CommandDefinitionId,
            command.Capability,
            command.CommandName,
            command.InputSchema,
            command.TimeoutMilliseconds));
    }

    private static ProcessBlocklyBlockDefinitionDetails CreateProcessCommandBlock(
        PluginProcessCommandDescriptor command)
    {
        return CreateCommandBlock(new GeneratedPluginCommand(
            "process",
            "Plugin Process Commands",
            300,
            command.PluginId,
            command.PluginName,
            command.PluginKind.ToString(),
            command.CommandDefinitionId,
            command.Capability,
            command.CommandName,
            command.InputSchema,
            command.TimeoutMilliseconds));
    }

    private static ProcessBlocklyBlockDefinitionDetails CreateCommandBlock(
        GeneratedPluginCommand command)
    {
        var blockType = CreateBlockType(command);
        var blocklyJson = JsonSerializer.Serialize(
            new
            {
                type = blockType,
                message0 = $"{command.KindLabel} {command.PluginName} {command.CommandName} target %1 %2 payload %3 timeout %4 ms",
                args0 = new object[]
                {
                    new
                    {
                        type = "field_dropdown",
                        name = "TARGET_KIND",
                        options = new[]
                        {
                            new[] { "System", RuntimeActionTargetKinds.System },
                            new[] { "Slot group", RuntimeActionTargetKinds.SlotGroup },
                            new[] { "Slot", RuntimeActionTargetKinds.Slot },
                            new[] { "Production Unit", RuntimeActionTargetKinds.ProductionUnit },
                            new[] { "Capability", RuntimeActionTargetKinds.Capability },
                            new[] { "Driver", RuntimeActionTargetKinds.Driver }
                        }
                    },
                    new
                    {
                        type = "field_input",
                        name = "TARGET_ID",
                        text = command.PluginId
                    },
                    new
                    {
                        type = "field_input",
                        name = "INPUT_PAYLOAD",
                        text = DefaultPayload(command.InputSchema)
                    },
                    new
                    {
                        type = "field_number",
                        name = "TIMEOUT_MS",
                        value = command.TimeoutMilliseconds,
                        min = 1,
                        precision = 1
                    }
                },
                previousStatement = (string?)null,
                nextStatement = (string?)null,
                colour = command.Colour,
                tooltip = CreateTooltip(command)
            },
            JsonOptions);

        var contractArtifact = ContractSerializer.Serialize(CreateContract(command));
        if (contractArtifact.IsFailure)
        {
            throw new InvalidOperationException(
                $"Generated Blockly block {blockType} has an invalid Runtime Action Contract: {contractArtifact.Error.Message}");
        }

        return new ProcessBlocklyBlockDefinitionDetails(
            blockType,
            command.Category,
            $"{command.PluginName} / {command.CommandName}",
            blocklyJson,
            IsBuiltIn: true,
            Version: 1,
            GeneratedRecordedAtUtc,
            GeneratedRecordedAtUtc,
            ExecutionMode: ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            RuntimeActionContractSchemaVersion: contractArtifact.Value.SchemaVersion,
            RuntimeActionContractJson: contractArtifact.Value.CanonicalJson,
            RuntimeActionContractSha256: contractArtifact.Value.Sha256);
    }

    private static string CreateBlockType(GeneratedPluginCommand command)
    {
        return $"openlineops_plugin_{command.KindLabel}_{NormalizeBlockSuffix(command.PluginId)}_{NormalizeBlockSuffix(command.CommandDefinitionId)}";
    }

    private static string NormalizeBlockSuffix(string value)
    {
        var normalized = InvalidBlockTypeCharacterRegex().Replace(value.Trim(), "_");
        normalized = RepeatedUnderscoreRegex().Replace(normalized, "_").Trim('_');

        return string.IsNullOrWhiteSpace(normalized)
            ? "command"
            : normalized.ToLowerInvariant();
    }

    private static string CreateTooltip(GeneratedPluginCommand command)
    {
        var inputSchema = string.IsNullOrWhiteSpace(command.InputSchema)
            ? "unspecified"
            : command.InputSchema.Trim();

        return $"Append plugin {command.KindLabel} command {command.Capability}/{command.CommandName}. Input schema: {inputSchema}.";
    }

    private static RuntimeActionContract CreateContract(GeneratedPluginCommand command)
    {
        return new RuntimeActionContract(
            RuntimeActionContractSchema.Current,
            $"plugin.{command.KindLabel}.command",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["TARGET_KIND"] = new(
                    RuntimeActionFieldType.Text,
                    Required: true,
                    RuntimeActionTargetKinds.All,
                    MaxLength: 32),
                ["TARGET_ID"] = new(
                    RuntimeActionFieldType.TargetReference,
                    Required: true,
                    MaxLength: 256),
                ["INPUT_PAYLOAD"] = new(
                    RuntimeActionFieldType.Text,
                    Required: true,
                    MaxLength: 4_096),
                ["TIMEOUT_MS"] = new(
                    RuntimeActionFieldType.WholeNumber,
                    Required: true,
                    Minimum: 1)
            },
            new RuntimeDeviceCommandEmit(
                new RuntimeActionFieldValue("TARGET_KIND"),
                new RuntimeActionFieldValue("TARGET_ID"),
                Literal(command.Capability),
                Literal(command.CommandName),
                new RuntimeActionObjectValue(new Dictionary<string, RuntimeActionValueExpression>(StringComparer.Ordinal)
                {
                    ["commandDefinitionId"] = Literal(command.CommandDefinitionId),
                    ["payload"] = new RuntimeActionFieldValue("INPUT_PAYLOAD"),
                    ["pluginId"] = Literal(command.PluginId),
                    ["pluginKind"] = Literal(command.PluginKind)
                }),
                new RuntimeActionFieldValue("TIMEOUT_MS")));
    }

    private static string DefaultPayload(string? inputSchema)
    {
        if (string.IsNullOrWhiteSpace(inputSchema))
        {
            return string.Empty;
        }

        return inputSchema.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? "{}"
            : "payload";
    }

    private static RuntimeActionLiteralValue Literal(string value)
    {
        return new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(value));
    }

    [GeneratedRegex("[^A-Za-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidBlockTypeCharacterRegex();

    [GeneratedRegex("_+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedUnderscoreRegex();

    private sealed record GeneratedPluginCommand(
        string KindLabel,
        string Category,
        int Colour,
        string PluginId,
        string PluginName,
        string PluginKind,
        string CommandDefinitionId,
        string Capability,
        string CommandName,
        string? InputSchema,
        int TimeoutMilliseconds);
}
