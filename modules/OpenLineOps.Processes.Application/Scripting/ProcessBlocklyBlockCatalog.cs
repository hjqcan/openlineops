using System.Text.Json;
using System.Text.RegularExpressions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Persistence;

namespace OpenLineOps.Processes.Application.Scripting;

public sealed partial class ProcessBlocklyBlockCatalog : IProcessBlocklyBlockCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RuntimeActionContractCanonicalSerializer ActionContractSerializer = new();
    private static readonly DateTimeOffset BuiltInRecordedAtUtc = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly IProcessBlocklyBlockDefinitionRepository _repository;
    private readonly IClock _clock;
    private readonly IProcessBlocklyBlockCatalogSource[] _sources;

    public ProcessBlocklyBlockCatalog(
        IProcessBlocklyBlockDefinitionRepository repository,
        IClock clock,
        IEnumerable<IProcessBlocklyBlockCatalogSource>? sources = null)
    {
        _repository = repository;
        _clock = clock;
        _sources = sources?.ToArray() ?? [];
    }

    public async Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var generatedBlocks = await ListGeneratedBlocksAsync(cancellationToken)
            .ConfigureAwait(false);
        var customBlocks = await _repository
            .ListLatestAsync(cancellationToken)
            .ConfigureAwait(false);
        var blocksByType = new Dictionary<string, ProcessBlocklyBlockDefinitionDetails>(StringComparer.Ordinal);

        foreach (var block in BuiltInBlocks())
        {
            blocksByType.TryAdd(block.BlockType, block);
        }

        foreach (var block in generatedBlocks)
        {
            blocksByType.TryAdd(block.BlockType, block);
        }

        foreach (var block in customBlocks.Select(ToDetails))
        {
            blocksByType.TryAdd(block.BlockType, block);
        }

        var blocks = blocksByType.Values
            .OrderBy(block => block.Category, StringComparer.Ordinal)
            .ThenBy(block => block.DisplayName, StringComparer.Ordinal)
            .ThenBy(block => block.BlockType, StringComparer.Ordinal)
            .ToArray();

        return Result.Success<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>(blocks);
    }

    public async Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListVersionsAsync(
        string blockType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blockType))
        {
            return Result.Failure<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>(
                ApplicationError.Validation(
                    "Processes.BlocklyBlockTypeRequired",
                    "BlockType is required."));
        }

        var normalizedBlockType = blockType.Trim();
        var builtIn = BuiltInBlocks().FirstOrDefault(block => block.BlockType == normalizedBlockType);
        if (builtIn is not null)
        {
            return Result.Success<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>([builtIn]);
        }

        var generatedBlock = await FindGeneratedBlockAsync(normalizedBlockType, cancellationToken)
            .ConfigureAwait(false);
        if (generatedBlock is not null)
        {
            return Result.Success<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>([generatedBlock]);
        }

        var versions = await _repository
            .ListVersionsAsync(normalizedBlockType, cancellationToken)
            .ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return Result.Failure<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>(
                ApplicationError.NotFound(
                    "Processes.BlocklyBlockNotFound",
                    $"Blockly block {normalizedBlockType} was not found."));
        }

        return Result.Success<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>(
            versions.Select(ToDetails).ToArray());
    }

    public async Task<Result<ProcessBlocklyBlockDefinitionDetails>> RegisterAsync(
        RegisterProcessBlocklyBlockDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return Result.Failure<ProcessBlocklyBlockDefinitionDetails>(validationError);
        }

        var blockType = request.BlockType.Trim();
        if (BuiltInBlocks().Any(block => block.BlockType == blockType))
        {
            return Result.Failure<ProcessBlocklyBlockDefinitionDetails>(ApplicationError.Conflict(
                "Processes.BlocklyBlockBuiltInCannotBeOverwritten",
                $"Built-in Blockly block {blockType} cannot be overwritten."));
        }

        var generatedBlock = await FindGeneratedBlockAsync(blockType, cancellationToken)
            .ConfigureAwait(false);
        if (generatedBlock is not null)
        {
            return Result.Failure<ProcessBlocklyBlockDefinitionDetails>(ApplicationError.Conflict(
                "Processes.BlocklyBlockGeneratedCannotBeOverwritten",
                $"Generated Blockly block {blockType} cannot be overwritten."));
        }

        var contractResult = ActionContractSerializer.Parse(request.RuntimeActionContractJson);
        if (contractResult.IsFailure)
        {
            return Result.Failure<ProcessBlocklyBlockDefinitionDetails>(contractResult.Error);
        }

        var contractArtifactResult = ActionContractSerializer.Serialize(contractResult.Value);
        if (contractArtifactResult.IsFailure)
        {
            return Result.Failure<ProcessBlocklyBlockDefinitionDetails>(contractArtifactResult.Error);
        }

        var contractArtifact = contractArtifactResult.Value;

        var record = await _repository
            .SaveNewVersionAsync(
                blockType,
                request.Category.Trim(),
                request.DisplayName.Trim(),
                NormalizeJson(request.BlocklyJson),
                ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
                contractArtifact.SchemaVersion,
                contractArtifact.CanonicalJson,
                contractArtifact.Sha256,
                _clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(ToDetails(record));
    }

    private async ValueTask<ProcessBlocklyBlockDefinitionDetails?> FindGeneratedBlockAsync(
        string blockType,
        CancellationToken cancellationToken)
    {
        var generatedBlocks = await ListGeneratedBlocksAsync(cancellationToken)
            .ConfigureAwait(false);

        return generatedBlocks.FirstOrDefault(block =>
            string.Equals(block.BlockType, blockType, StringComparison.Ordinal));
    }

    private async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>> ListGeneratedBlocksAsync(
        CancellationToken cancellationToken)
    {
        if (_sources.Length == 0)
        {
            return [];
        }

        var blocksByType = new Dictionary<string, ProcessBlocklyBlockDefinitionDetails>(StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceBlocks = await source.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var block in sourceBlocks)
            {
                if (!string.IsNullOrWhiteSpace(block.BlockType))
                {
                    blocksByType.TryAdd(block.BlockType, block);
                }
            }
        }

        return blocksByType.Values.ToArray();
    }

    private static ProcessBlocklyBlockDefinitionDetails ToDetails(
        ProcessBlocklyBlockDefinitionRecord record)
    {
        return new ProcessBlocklyBlockDefinitionDetails(
            record.BlockType,
            record.Category,
            record.DisplayName,
            record.BlocklyJson,
            IsBuiltIn: false,
            record.Version,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            ExecutionMode: record.ExecutionMode,
            RuntimeActionContractSchemaVersion: record.RuntimeActionContractSchemaVersion,
            RuntimeActionContractJson: record.RuntimeActionContractJson,
            RuntimeActionContractSha256: record.RuntimeActionContractSha256);
    }

    private static ApplicationError? Validate(RegisterProcessBlocklyBlockDefinitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BlockType))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockTypeRequired",
                "BlockType is required.");
        }

        if (!BlockTypeRegex().IsMatch(request.BlockType))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockTypeInvalid",
                "BlockType must start with a letter and contain only letters, numbers, underscore, hyphen, colon, or dot.");
        }

        if (string.IsNullOrWhiteSpace(request.Category))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockCategoryRequired",
                "Category is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockDisplayNameRequired",
                "DisplayName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.BlocklyJson))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockJsonRequired",
                "BlocklyJson is required.");
        }

        var blockType = TryReadBlockType(request.BlocklyJson, out var jsonError);
        if (jsonError is not null)
        {
            return jsonError;
        }

        if (!string.Equals(blockType, request.BlockType.Trim(), StringComparison.Ordinal))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockJsonTypeMismatch",
                $"BlocklyJson type '{blockType}' must match BlockType '{request.BlockType.Trim()}'.");
        }

        if (string.IsNullOrWhiteSpace(request.RuntimeActionContractSchemaVersion)
            || string.IsNullOrWhiteSpace(request.RuntimeActionContractJson))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockRuntimeActionContractRequired",
                "Runtime Action Contract schema and JSON are required.");
        }

        var contractResult = ActionContractSerializer.Parse(request.RuntimeActionContractJson);
        if (contractResult.IsFailure)
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockRuntimeActionContractInvalid",
                contractResult.Error.Message);
        }

        var artifactResult = ActionContractSerializer.Serialize(contractResult.Value);
        if (artifactResult.IsFailure
            || !string.Equals(
                request.RuntimeActionContractSchemaVersion,
                artifactResult.Value.SchemaVersion,
                StringComparison.Ordinal))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockRuntimeActionContractHashMismatch",
                "Runtime Action Contract schema does not match its JSON contract.");
        }

        HashSet<string> blockFields;
        try
        {
            blockFields = ReadBlocklyFieldNames(request.BlocklyJson);
        }
        catch (InvalidDataException exception)
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockJsonInvalid",
                exception.Message);
        }

        var contractFields = contractResult.Value.Fields.Keys.ToHashSet(StringComparer.Ordinal);
        if (!blockFields.SetEquals(contractFields))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockContractFieldsMismatch",
                "Blockly field names must exactly match the Runtime Action Contract field names.");
        }

        return null;
    }

    private static HashSet<string> ReadBlocklyFieldNames(string blocklyJson)
    {
        using var document = JsonDocument.Parse(blocklyJson);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("args0", out var args)
            || args.ValueKind != JsonValueKind.Array)
        {
            return fields;
        }

        foreach (var argument in args.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.Object
                || !argument.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.GetString()!.StartsWith("field_", StringComparison.Ordinal)
                || !argument.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                continue;
            }

            if (!fields.Add(name.GetString()!))
            {
                throw new InvalidDataException(
                    $"BlocklyJson contains duplicate field name '{name.GetString()}'.");
            }
        }

        return fields;
    }

    private static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, JsonOptions);
    }

    private static string? TryReadBlockType(string json, out ApplicationError? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = ApplicationError.Validation(
                    "Processes.BlocklyBlockJsonInvalid",
                    "BlocklyJson must be a JSON object.");
                return null;
            }

            if (!document.RootElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                error = ApplicationError.Validation(
                    "Processes.BlocklyBlockJsonTypeRequired",
                    "BlocklyJson must declare a non-empty type.");
                return null;
            }

            return typeElement.GetString();
        }
        catch (JsonException exception)
        {
            error = ApplicationError.Validation(
                "Processes.BlocklyBlockJsonInvalid",
                $"BlocklyJson is invalid JSON: {exception.Message}");
            return null;
        }
    }

    internal static IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> BuiltInBlocks()
    {
        return
        [
            BuiltIn(
                "openlineops_move_axis",
                "Motion",
                "Move Axis",
                """
                {
                  "type": "openlineops_move_axis",
                  "message0": "move target %1 %2 axis %3 to %4 %5 speed %6",
                  "args0": [
                    {
                      "type": "field_dropdown",
                      "name": "TARGET_KIND",
                      "options": [["Module", "AutomationModule"], ["Equipment", "EquipmentNode"], ["Slot group", "SlotGroup"], ["Slot", "Slot"], ["DUT", "Dut"], ["System", "System"], ["Capability", "Capability"], ["Driver", "Driver"]]
                    },
                    {
                      "type": "field_input",
                      "name": "TARGET_ID",
                      "text": "module.motion"
                    },
                    {
                      "type": "field_dropdown",
                      "name": "AXIS",
                      "options": [["X", "X"], ["Y", "Y"], ["Z", "Z"]]
                    },
                    {
                      "type": "field_number",
                      "name": "POSITION",
                      "value": 10,
                      "precision": 0.001
                    },
                    {
                      "type": "field_dropdown",
                      "name": "UNIT",
                      "options": [["mm", "mm"], ["deg", "deg"]]
                    },
                    {
                      "type": "field_number",
                      "name": "SPEED",
                      "value": 5,
                      "min": 0,
                      "precision": 0.001
                    }
                  ],
                  "previousStatement": null,
                  "nextStatement": null,
                  "colour": 176,
                  "tooltip": "Append an axis movement command to the automation plan."
                }
                """),
            BuiltIn(
                "openlineops_set_light",
                "I/O",
                "Set Light",
                """
                {
                  "type": "openlineops_set_light",
                  "message0": "set light target %1 %2 channel %3 state %4",
                  "args0": [
                    {
                      "type": "field_dropdown",
                      "name": "TARGET_KIND",
                      "options": [["Module", "AutomationModule"], ["Equipment", "EquipmentNode"], ["Slot group", "SlotGroup"], ["Slot", "Slot"], ["DUT", "Dut"], ["System", "System"], ["Capability", "Capability"], ["Driver", "Driver"]]
                    },
                    {
                      "type": "field_input",
                      "name": "TARGET_ID",
                      "text": "module.io"
                    },
                    {
                      "type": "field_input",
                      "name": "CHANNEL",
                      "text": "tower.green"
                    },
                    {
                      "type": "field_dropdown",
                      "name": "STATE",
                      "options": [["on", "On"], ["off", "Off"]]
                    }
                  ],
                  "previousStatement": null,
                  "nextStatement": null,
                  "colour": 205,
                  "tooltip": "Append a digital output command for a light channel."
                }
                """),
            BuiltIn(
                "openlineops_rotate_motor",
                "Motion",
                "Rotate Motor",
                """
                {
                  "type": "openlineops_rotate_motor",
                  "message0": "rotate motor target %1 %2 motor %3 at %4 rpm for %5 ms",
                  "args0": [
                    {
                      "type": "field_dropdown",
                      "name": "TARGET_KIND",
                      "options": [["Module", "AutomationModule"], ["Equipment", "EquipmentNode"], ["Slot group", "SlotGroup"], ["Slot", "Slot"], ["DUT", "Dut"], ["System", "System"], ["Capability", "Capability"], ["Driver", "Driver"]]
                    },
                    {
                      "type": "field_input",
                      "name": "TARGET_ID",
                      "text": "module.motor"
                    },
                    {
                      "type": "field_input",
                      "name": "MOTOR",
                      "text": "motor.main"
                    },
                    {
                      "type": "field_number",
                      "name": "RPM",
                      "value": 1200,
                      "precision": 1
                    },
                    {
                      "type": "field_number",
                      "name": "DURATION_MS",
                      "value": 500,
                      "min": 0,
                      "precision": 1
                    }
                  ],
                  "previousStatement": null,
                  "nextStatement": null,
                  "colour": 176,
                  "tooltip": "Append a timed motor rotation command."
                }
                """),
            BuiltIn(
                "openlineops_wait",
                "Flow",
                "Wait",
                """
                {
                  "type": "openlineops_wait",
                  "message0": "wait %1 ms",
                  "args0": [
                    {
                      "type": "field_number",
                      "name": "DURATION_MS",
                      "value": 250,
                      "min": 0,
                      "precision": 1
                    }
                  ],
                  "previousStatement": null,
                  "nextStatement": null,
                  "colour": 48,
                  "tooltip": "Append a deterministic wait step."
                }
                """),
            BuiltIn(
                "openlineops_run_external_test",
                "Production",
                "Run External Test",
                """
                {
                  "type": "openlineops_run_external_test",
                  "message0": "run external test target %1 %2 capability %3 command %4 adapter %5 timeout %6 ms",
                  "args0": [
                    {
                      "type": "field_dropdown",
                      "name": "TARGET_KIND",
                      "options": [["Module", "AutomationModule"], ["Equipment", "EquipmentNode"], ["Slot group", "SlotGroup"], ["Slot", "Slot"], ["DUT", "Dut"], ["System", "System"], ["Capability", "Capability"], ["Driver", "Driver"]]
                    },
                    {
                      "type": "field_input",
                      "name": "TARGET_ID",
                      "text": "workstation.main"
                    },
                    {
                      "type": "field_input",
                      "name": "CAPABILITY",
                      "text": "production.external-test"
                    },
                    {
                      "type": "field_input",
                      "name": "COMMAND",
                      "text": "Run"
                    },
                    {
                      "type": "field_input",
                      "name": "ADAPTER_ID",
                      "text": "adapter.main"
                    },
                    {
                      "type": "field_number",
                      "name": "TIMEOUT_MS",
                      "value": 300000,
                      "min": 1,
                      "precision": 1
                    }
                  ],
                  "previousStatement": null,
                  "nextStatement": null,
                  "colour": 12,
                  "tooltip": "Run an external production test through an explicitly bound adapter."
                }
                """),
            BuiltIn(
                "openlineops_result_from_input",
                "Result",
                "Result From Input",
                """
                {
                  "type": "openlineops_result_from_input",
                  "message0": "result key %1 from input %2 status %3 include node %4 include timestamp %5",
                  "args0": [
                    {
                      "type": "field_input",
                      "name": "OUTPUT_KEY",
                      "text": "normalized"
                    },
                    {
                      "type": "field_input",
                      "name": "INPUT_PAYLOAD",
                      "text": "scan-ok"
                    },
                    {
                      "type": "field_input",
                      "name": "STATUS",
                      "text": "ok"
                    },
                    {
                      "type": "field_checkbox",
                      "name": "INCLUDE_NODE_ID",
                      "checked": true
                    },
                    {
                      "type": "field_checkbox",
                      "name": "INCLUDE_TIMESTAMP",
                      "checked": false
                    }
                  ],
                  "previousStatement": null,
                  "nextStatement": null,
                  "colour": 176,
                  "tooltip": "Write the PythonScript result payload used by runtime traceability."
                }
                """)
        ];
    }

    private static ProcessBlocklyBlockDefinitionDetails BuiltIn(
        string blockType,
        string category,
        string displayName,
        string blocklyJson)
    {
        var contractResult = ActionContractSerializer.Serialize(
            BuiltInRuntimeActionContracts.Get(blockType));
        if (contractResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Built-in Blockly block {blockType} has an invalid Runtime Action Contract: {contractResult.Error.Message}");
        }

        var contract = contractResult.Value;
        return new ProcessBlocklyBlockDefinitionDetails(
            blockType,
            category,
            displayName,
            NormalizeJson(blocklyJson),
            IsBuiltIn: true,
            Version: 1,
            CreatedAtUtc: BuiltInRecordedAtUtc,
            UpdatedAtUtc: BuiltInRecordedAtUtc,
            ExecutionMode: ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            RuntimeActionContractSchemaVersion: contract.SchemaVersion,
            RuntimeActionContractJson: contract.CanonicalJson,
            RuntimeActionContractSha256: contract.Sha256);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_:\\.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockTypeRegex();
}
