using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Processes.Application.Scripting;

namespace OpenLineOps.Processes.Tests;

public sealed class RuntimeActionContractCanonicalSerializerTests
{
    private readonly RuntimeActionContractCanonicalSerializer _serializer = new();

    [Fact]
    public void SerializeProducesStableCanonicalJsonAndHashAcrossMapInsertionOrder()
    {
        var first = CreateCommandContract(reverseInsertionOrder: false);
        var second = CreateCommandContract(reverseInsertionOrder: true);

        var firstResult = _serializer.Serialize(first);
        var secondResult = _serializer.Serialize(second);

        Assert.True(firstResult.IsSuccess, firstResult.Error.Message);
        Assert.True(secondResult.IsSuccess, secondResult.Error.Message);
        Assert.Equal(firstResult.Value.CanonicalJson, secondResult.Value.CanonicalJson);
        Assert.Equal(firstResult.Value.Sha256, secondResult.Value.Sha256);
        Assert.Equal(RuntimeActionContractSchemaVersions.V1, firstResult.Value.SchemaVersion);
        Assert.Equal(64, firstResult.Value.Sha256.Length);
        Assert.Equal(firstResult.Value.Sha256, firstResult.Value.Sha256.ToLowerInvariant());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(firstResult.Value.CanonicalJson)))
                .ToLowerInvariant(),
            firstResult.Value.Sha256);
        Assert.StartsWith(
            "{\"schemaVersion\":\"openlineops.runtime-action-contract/v1\",\"actionType\":\"motion.axis.move\",\"fields\":{\"AXIS\"",
            firstResult.Value.CanonicalJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"enum\":[\"X\",\"Y\",\"Z\"]",
            firstResult.Value.CanonicalJson,
            StringComparison.Ordinal);

        var roundTrip = _serializer.Deserialize(firstResult.Value.CanonicalJson);
        Assert.True(roundTrip.IsSuccess, roundTrip.Error.Message);
        Assert.Equal(
            firstResult.Value.Sha256,
            _serializer.Serialize(roundTrip.Value).Value.Sha256);
    }

    [Theory]
    [MemberData(nameof(MaliciousOrInvalidContracts))]
    public void DeserializeRejectsMaliciousOrInvalidContractJson(string json)
    {
        var result = _serializer.Deserialize(json);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.RuntimeActionContractInvalid", result.Error.Code);
    }

    [Fact]
    public void DeserializeRejectsValidButNonCanonicalJson()
    {
        var artifact = _serializer.Serialize(CreateCommandContract(reverseInsertionOrder: false));
        Assert.True(artifact.IsSuccess, artifact.Error.Message);
        var nonCanonical = artifact.Value.CanonicalJson.Replace(
            "{\"schemaVersion\"",
            "{ \"schemaVersion\"",
            StringComparison.Ordinal);

        var result = _serializer.Deserialize(nonCanonical);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.RuntimeActionContractInvalid", result.Error.Code);
        Assert.Contains("not in canonical form", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeRejectsHostileNullsInvalidRangesAndNestedLiteralObjects()
    {
        var missingFields = new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "test.action",
            null!,
            new RuntimeDelayEmit(Field("DURATION_MS")));
        var invalidRange = new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "test.action",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["DURATION_MS"] = new(
                    RuntimeActionFieldType.WholeNumber,
                    Required: true,
                    Minimum: 10,
                    Maximum: 1)
            },
            new RuntimeDelayEmit(Field("DURATION_MS")));
        var objectLiteral = new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "test.action",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal),
            new RuntimeDeviceCommandEmit(
                "test.capability",
                "Execute",
                new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(new
                {
                    script = "import os"
                })),
                TimeoutMilliseconds: 1000));

        var missingFieldsResult = _serializer.Serialize(missingFields);
        var invalidRangeResult = _serializer.Serialize(invalidRange);
        var objectLiteralResult = _serializer.Serialize(objectLiteral);

        Assert.True(missingFieldsResult.IsFailure);
        Assert.True(invalidRangeResult.IsFailure);
        Assert.True(objectLiteralResult.IsFailure);
        Assert.All(
            new[]
            {
                missingFieldsResult.Error,
                invalidRangeResult.Error,
                objectLiteralResult.Error
            },
            error => Assert.Equal("Validation.Processes.RuntimeActionContractInvalid", error.Code));
    }

    [Fact]
    public void SerializeSupportsSafeDelayResultPatchContextObjectAndArrayVocabulary()
    {
        var delay = new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "flow.wait",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["DURATION_MS"] = new(
                    RuntimeActionFieldType.WholeNumber,
                    Required: true,
                    Minimum: 0)
            },
            new RuntimeDelayEmit(Field("DURATION_MS")));
        var resultPatch = new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "result.patch",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["ENABLED"] = new(RuntimeActionFieldType.Boolean, Required: true),
                ["KEY"] = new(RuntimeActionFieldType.Text, Required: true, MaxLength: 64)
            },
            new RuntimeResultPatchEmit(
            [
                new RuntimeResultPatchAssignment(
                    Field("KEY"),
                    new RuntimeActionObjectValue(
                        new Dictionary<string, RuntimeActionValueExpression>(StringComparer.Ordinal)
                        {
                            ["node"] = new RuntimeActionContextValue(RuntimeActionContextValueKind.NodeId),
                            ["values"] = new RuntimeActionArrayValue(
                            [
                                Literal(true),
                                Literal(12.5m),
                                Literal("ok")
                            ])
                        }),
                    new RuntimeActionFieldEqualsCondition("ENABLED", ExpectedValue: true))
            ]));

        var delayArtifact = _serializer.Serialize(delay);
        var resultPatchArtifact = _serializer.Serialize(resultPatch);

        Assert.True(delayArtifact.IsSuccess, delayArtifact.Error.Message);
        Assert.True(resultPatchArtifact.IsSuccess, resultPatchArtifact.Error.Message);
        Assert.True(_serializer.Deserialize(delayArtifact.Value.CanonicalJson).IsSuccess);
        Assert.True(_serializer.Deserialize(resultPatchArtifact.Value.CanonicalJson).IsSuccess);
    }

    public static TheoryData<string> MaliciousOrInvalidContracts()
    {
        const string validFields = "\"fields\":{\"VALUE\":{\"type\":\"string\",\"required\":true}}";
        const string validInput = "\"input\":{\"source\":\"field\",\"name\":\"VALUE\"}";
        var prefix = "{\"schemaVersion\":\"openlineops.runtime-action-contract/v1\",\"actionType\":\"test.action\",";

        return new TheoryData<string>
        {
            prefix + validFields + ",\"emit\":null}",
            prefix + "\"fields\":null,\"emit\":{\"kind\":\"delay\",\"durationMilliseconds\":{\"source\":\"literal\",\"value\":1}}}",
            prefix + validFields + ",\"script\":\"import os\",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":\"test.capability\",\"commandName\":\"Execute\"," + validInput + ",\"timeoutMilliseconds\":1000,\"retryLimit\":0}}",
            prefix + validFields + ",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":{\"source\":\"field\",\"name\":\"VALUE\"},\"commandName\":\"Execute\"," + validInput + ",\"timeoutMilliseconds\":1000,\"retryLimit\":0}}",
            prefix + validFields + ",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":\"test.capability\",\"commandName\":{\"source\":\"field\",\"name\":\"VALUE\"}," + validInput + ",\"timeoutMilliseconds\":1000,\"retryLimit\":0}}",
            prefix + validFields + ",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":\"test.capability\",\"commandName\":\"Execute\",\"input\":{\"source\":\"raw\",\"code\":\"__import__('os')\"},\"timeoutMilliseconds\":1000,\"retryLimit\":0}}",
            prefix + validFields + ",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":\"test.capability\",\"commandName\":\"Execute\",\"input\":{\"source\":\"field\",\"name\":\"VALUE\",\"template\":\"{{VALUE}}\"},\"timeoutMilliseconds\":1000,\"retryLimit\":0}}",
            prefix + validFields + ",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":\"test.capability\",\"commandName\":\"Execute\",\"input\":{\"source\":\"literal\",\"value\":NaN},\"timeoutMilliseconds\":1000,\"retryLimit\":0}}",
            prefix + validFields + ",\"emit\":{\"kind\":\"deviceCommand\",\"capability\":\"test.capability\",\"commandName\":\"Execute\"," + validInput + ",\"timeoutMilliseconds\":1000,\"retryLimit\":1}}",
            "{\"schemaVersion\":\"openlineops.runtime-action-contract/v1\",\"actionType\":\"test.action\",\"actionType\":\"duplicate.action\",\"fields\":{},\"emit\":{\"kind\":\"delay\",\"durationMilliseconds\":{\"source\":\"literal\",\"value\":1}}}"
        };
    }

    private static RuntimeActionContract CreateCommandContract(bool reverseInsertionOrder)
    {
        var fields = reverseInsertionOrder
            ? new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["POSITION"] = new(RuntimeActionFieldType.Number, Required: true),
                ["AXIS"] = new(
                    RuntimeActionFieldType.Text,
                    Required: true,
                    AllowedValues: ["Z", "X", "Y"])
            }
            : new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["AXIS"] = new(
                    RuntimeActionFieldType.Text,
                    Required: true,
                    AllowedValues: ["X", "Y", "Z"]),
                ["POSITION"] = new(RuntimeActionFieldType.Number, Required: true)
            };
        var input = reverseInsertionOrder
            ? new Dictionary<string, RuntimeActionValueExpression>(StringComparer.Ordinal)
            {
                ["position"] = Field("POSITION"),
                ["axis"] = Field("AXIS")
            }
            : new Dictionary<string, RuntimeActionValueExpression>(StringComparer.Ordinal)
            {
                ["axis"] = Field("AXIS"),
                ["position"] = Field("POSITION")
            };

        return new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "motion.axis.move",
            fields,
            new RuntimeDeviceCommandEmit(
                "motion.axis",
                "MoveAxis",
                new RuntimeActionObjectValue(input),
                TimeoutMilliseconds: 30_000));
    }

    private static RuntimeActionFieldValue Field(string name) => new(name);

    private static RuntimeActionLiteralValue Literal<T>(T value)
    {
        return new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(value));
    }
}
