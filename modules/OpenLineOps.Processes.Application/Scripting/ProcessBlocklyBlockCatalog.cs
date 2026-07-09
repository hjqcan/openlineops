using System.Text.Json;
using System.Text.RegularExpressions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Persistence;

namespace OpenLineOps.Processes.Application.Scripting;

public sealed partial class ProcessBlocklyBlockCatalog : IProcessBlocklyBlockCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

        var record = await _repository
            .SaveNewVersionAsync(
                blockType,
                request.Category.Trim(),
                request.DisplayName.Trim(),
                NormalizeJson(request.BlocklyJson),
                request.PythonCodeTemplate.Trim(),
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
            record.PythonCodeTemplate,
            IsBuiltIn: false,
            record.Version,
            record.CreatedAtUtc,
            record.UpdatedAtUtc);
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

        if (string.IsNullOrWhiteSpace(request.PythonCodeTemplate))
        {
            return ApplicationError.Validation(
                "Processes.BlocklyBlockPythonTemplateRequired",
                "PythonCodeTemplate is required.");
        }

        return null;
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

    private static IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> BuiltInBlocks()
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
                  "message0": "move axis %1 to %2 %3 speed %4",
                  "args0": [
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
                """,
                "automation_plan.append({'type': 'axis.move', 'axis': {{AXIS}}, 'position': {{number:POSITION}}, 'unit': {{UNIT}}, 'speed': {{number:SPEED}}})"),
            BuiltIn(
                "openlineops_set_light",
                "I/O",
                "Set Light",
                """
                {
                  "type": "openlineops_set_light",
                  "message0": "set light %1 %2",
                  "args0": [
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
                """,
                "automation_plan.append({'type': 'io.light', 'channel': {{CHANNEL}}, 'state': {{STATE}}})"),
            BuiltIn(
                "openlineops_rotate_motor",
                "Motion",
                "Rotate Motor",
                """
                {
                  "type": "openlineops_rotate_motor",
                  "message0": "rotate motor %1 at %2 rpm for %3 ms",
                  "args0": [
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
                """,
                "automation_plan.append({'type': 'motor.rotate', 'motor': {{MOTOR}}, 'rpm': {{number:RPM}}, 'duration_ms': {{number:DURATION_MS}}})"),
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
                """,
                "automation_plan.append({'type': 'flow.wait', 'duration_ms': {{number:DURATION_MS}}})"),
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
                """,
                """
                input_payload = {{INPUT_PAYLOAD}}
                result[{{OUTPUT_KEY}}] = input_payload
                result['status'] = {{STATUS}}
                if {{INCLUDE_TIMESTAMP}} == 'TRUE':
                    result['timestamp_utc'] = 'runtime-provided'
                if {{INCLUDE_NODE_ID}} == 'TRUE':
                    result['node'] = node_id
                """)
        ];
    }

    private static ProcessBlocklyBlockDefinitionDetails BuiltIn(
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string pythonCodeTemplate)
    {
        return new ProcessBlocklyBlockDefinitionDetails(
            blockType,
            category,
            displayName,
            NormalizeJson(blocklyJson),
            pythonCodeTemplate,
            IsBuiltIn: true,
            Version: 1,
            CreatedAtUtc: BuiltInRecordedAtUtc,
            UpdatedAtUtc: BuiltInRecordedAtUtc);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_:\\.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockTypeRegex();
}
