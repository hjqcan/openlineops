using System.Text.Json;

namespace OpenLineOps.Processes.Application.Scripting;

internal static class BuiltInRuntimeActionContracts
{
    public static RuntimeActionContract Get(string blockType)
    {
        return blockType switch
        {
            "openlineops_move_axis" => MoveAxis(),
            "openlineops_set_light" => SetLight(),
            "openlineops_rotate_motor" => RotateMotor(),
            "openlineops_wait" => Wait(),
            "openlineops_result_from_input" => ResultFromInput(),
            "openlineops_run_external_test" => RunExternalTest(),
            _ => throw new ArgumentException(
                $"Built-in Blockly block {blockType} does not have a Runtime Action Contract.",
                nameof(blockType))
        };
    }

    private static RuntimeActionContract MoveAxis()
    {
        return Contract(
            "motion.axis.move",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["TARGET_KIND"] = TargetKindField(),
                ["TARGET_ID"] = TargetIdField(),
                ["CAPABILITY"] = TextField(maxLength: 256),
                ["COMMAND"] = TextField(maxLength: 256),
                ["AXIS"] = TextField(["X", "Y", "Z"], maxLength: 16),
                ["POSITION"] = NumberField(),
                ["SPEED"] = NumberField(minimum: 0),
                ["UNIT"] = TextField(["mm", "deg"], maxLength: 16)
            },
            new RuntimeDeviceCommandEmit(
                Field("TARGET_KIND"),
                Field("TARGET_ID"),
                Field("CAPABILITY"),
                Field("COMMAND"),
                Object(
                    ("axis", Field("AXIS")),
                    ("position", Field("POSITION")),
                    ("speed", Field("SPEED")),
                    ("unit", Field("UNIT"))),
                Literal(30_000)));
    }

    private static RuntimeActionContract SetLight()
    {
        return Contract(
            "io.light.set",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["TARGET_KIND"] = TargetKindField(),
                ["TARGET_ID"] = TargetIdField(),
                ["CAPABILITY"] = TextField(maxLength: 256),
                ["COMMAND"] = TextField(maxLength: 256),
                ["CHANNEL"] = TextField(maxLength: 256),
                ["STATE"] = TextField(["On", "Off"], maxLength: 16)
            },
            new RuntimeDeviceCommandEmit(
                Field("TARGET_KIND"),
                Field("TARGET_ID"),
                Field("CAPABILITY"),
                Field("COMMAND"),
                Object(
                    ("channel", Field("CHANNEL")),
                    ("state", Field("STATE"))),
                Literal(30_000)));
    }

    private static RuntimeActionContract RotateMotor()
    {
        return Contract(
            "motion.motor.rotate",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["TARGET_KIND"] = TargetKindField(),
                ["TARGET_ID"] = TargetIdField(),
                ["CAPABILITY"] = TextField(maxLength: 256),
                ["COMMAND"] = TextField(maxLength: 256),
                ["DURATION_MS"] = IntegerField(minimum: 0),
                ["MOTOR"] = TextField(maxLength: 256),
                ["RPM"] = NumberField()
            },
            new RuntimeDeviceCommandEmit(
                Field("TARGET_KIND"),
                Field("TARGET_ID"),
                Field("CAPABILITY"),
                Field("COMMAND"),
                Object(
                    ("duration_ms", Field("DURATION_MS")),
                    ("motor", Field("MOTOR")),
                    ("rpm", Field("RPM"))),
                Literal(30_000)));
    }

    private static RuntimeActionContract Wait()
    {
        return Contract(
            "flow.wait",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["DURATION_MS"] = IntegerField(minimum: 0)
            },
            new RuntimeDelayEmit(Field("DURATION_MS")));
    }

    private static RuntimeActionContract ResultFromInput()
    {
        return Contract(
            "result.from-input",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["INCLUDE_NODE_ID"] = BooleanField(),
                ["INCLUDE_TIMESTAMP"] = BooleanField(),
                ["INPUT_PAYLOAD"] = TextField(maxLength: 4096),
                ["OUTPUT_KEY"] = TextField(maxLength: 128),
                ["STATUS"] = TextField(maxLength: 128)
            },
            new RuntimeResultPatchEmit(
            [
                new RuntimeResultPatchAssignment(
                    Field("OUTPUT_KEY"),
                    Field("INPUT_PAYLOAD")),
                new RuntimeResultPatchAssignment(
                    Literal("status"),
                    Field("STATUS")),
                new RuntimeResultPatchAssignment(
                    Literal("node"),
                    new RuntimeActionContextValue(RuntimeActionContextValueKind.NodeId),
                    new RuntimeActionFieldEqualsCondition("INCLUDE_NODE_ID", ExpectedValue: true)),
                new RuntimeResultPatchAssignment(
                    Literal("timestamp_utc"),
                    new RuntimeActionContextValue(RuntimeActionContextValueKind.TimestampUtc),
                    new RuntimeActionFieldEqualsCondition("INCLUDE_TIMESTAMP", ExpectedValue: true))
            ]));
    }

    private static RuntimeActionContract RunExternalTest()
    {
        return Contract(
            "production.external-test.run",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["TARGET_KIND"] = TargetKindField(),
                ["TARGET_ID"] = TargetIdField(),
                ["CAPABILITY"] = TextField(maxLength: 256),
                ["COMMAND"] = TextField(maxLength: 256),
                ["ADAPTER_ID"] = TextField(maxLength: 256),
                ["TIMEOUT_MS"] = IntegerField(minimum: 1)
            },
            new RuntimeDeviceCommandEmit(
                Field("TARGET_KIND"),
                Field("TARGET_ID"),
                Field("CAPABILITY"),
                Field("COMMAND"),
                Object(("externalTestProgramAdapterId", Field("ADAPTER_ID"))),
                Field("TIMEOUT_MS")));
    }

    private static RuntimeActionContract Contract(
        string actionType,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields,
        RuntimeActionEmit emit)
    {
        return new RuntimeActionContract(
            RuntimeActionContractSchema.Current,
            actionType,
            fields,
            emit);
    }

    private static RuntimeActionFieldDefinition TextField(
        IReadOnlyCollection<string>? allowedValues = null,
        int? maxLength = null)
    {
        return new RuntimeActionFieldDefinition(
            RuntimeActionFieldType.Text,
            Required: true,
            allowedValues,
            MaxLength: maxLength);
    }

    private static RuntimeActionFieldDefinition NumberField(decimal? minimum = null)
    {
        return new RuntimeActionFieldDefinition(
            RuntimeActionFieldType.Number,
            Required: true,
            Minimum: minimum);
    }

    private static RuntimeActionFieldDefinition IntegerField(decimal? minimum = null)
    {
        return new RuntimeActionFieldDefinition(
            RuntimeActionFieldType.WholeNumber,
            Required: true,
            Minimum: minimum);
    }

    private static RuntimeActionFieldDefinition BooleanField()
    {
        return new RuntimeActionFieldDefinition(
            RuntimeActionFieldType.Boolean,
            Required: true);
    }

    private static RuntimeActionFieldDefinition TargetKindField()
    {
        return TextField(RuntimeActionTargetKinds.All, maxLength: 32);
    }

    private static RuntimeActionFieldDefinition TargetIdField()
    {
        return new RuntimeActionFieldDefinition(
            RuntimeActionFieldType.TargetReference,
            Required: true,
            MaxLength: 256);
    }

    private static RuntimeActionFieldValue Field(string name) => new(name);

    private static RuntimeActionLiteralValue Literal<T>(T value)
    {
        return new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(value));
    }

    private static RuntimeActionObjectValue Object(
        params (string Name, RuntimeActionValueExpression Value)[] properties)
    {
        return new RuntimeActionObjectValue(properties.ToDictionary(
            property => property.Name,
            property => property.Value,
            StringComparer.Ordinal));
    }
}
