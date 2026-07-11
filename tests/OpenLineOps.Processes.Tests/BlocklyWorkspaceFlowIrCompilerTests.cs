using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;

namespace OpenLineOps.Processes.Tests;

public sealed class BlocklyWorkspaceFlowIrCompilerTests
{
    private static readonly DateTimeOffset RecordedAtUtc =
        new(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompileProducesStaticActionsExactBlockSourcesTargetsAndDependencyLocks()
    {
        var workspace = """
            {"blocks":{"languageVersion":0,"blocks":[{"type":"openlineops_move_axis","id":"move-1","x":0,"y":0,"fields":{"TARGET_KIND":"System","TARGET_ID":"system.motion","CAPABILITY":"motion.axis","COMMAND":"MoveAxis","AXIS":"X","POSITION":10,"SPEED":5,"UNIT":"mm"},"next":{"block":{"type":"openlineops_run_external_test","id":"external-1","fields":{"TARGET_KIND":"System","TARGET_ID":"system.tester","CAPABILITY":"production.external-test","COMMAND":"Run","ADAPTER_ID":"adapter.main","TIMEOUT_MS":1234}}}}]}}
            """;
        var definition = CreatePublishedDefinition(workspace);

        var result = new ProcessFlowIrCompiler().Compile(definition);

        Assert.True(result.IsSuccess, result.Error.Message);
        var document = result.Value.Document;
        var blocklyNode = Assert.Single(document.Nodes, node => node.Kind == FlowIrNodeKind.Blockly);
        Assert.Equal(2, blocklyNode.Actions.Length);
        Assert.Equal(FlowIrSourceElementKind.ProcessNode, blocklyNode.Source.ElementKind);
        Assert.Equal(Sha256(workspace), blocklyNode.Source.ContentHash);

        var move = blocklyNode.Actions[0];
        Assert.Equal("flow:action:1", move.ActionId);
        Assert.Equal("motion.axis", move.RequiredCapability);
        Assert.Equal("MoveAxis", move.CommandName);
        Assert.Equal(FlowIrTargetReferenceKind.System, move.Target.Kind);
        Assert.Equal("system.motion", move.Target.Reference);
        Assert.Equal(30_000, move.Execution.TimeoutMilliseconds);
        Assert.Equal(FlowIrSourceElementKind.BlocklyBlock, move.Source.ElementKind);
        Assert.Equal("move-1", move.Source.ElementId);
        Assert.Contains(document.BlockDependencies, dependency =>
            dependency.BlockType == "openlineops_move_axis"
            && dependency.Version == 1
            && dependency.ContractSchemaVersion == RuntimeActionContractSchema.Current
            && dependency.ContractSha256 == move.Source.ContentHash
            && dependency.LockId == $"openlineops_move_axis@1#{move.Source.ContentHash}");
        using (var payload = JsonDocument.Parse(move.InputPayload!))
        {
            Assert.Equal("System", payload.RootElement.GetProperty("targetKind").GetString());
            Assert.Equal("system.motion", payload.RootElement.GetProperty("targetId").GetString());
            Assert.Equal("X", payload.RootElement.GetProperty("axis").GetString());
            Assert.Equal(10m, payload.RootElement.GetProperty("position").GetDecimal());
        }

        var external = blocklyNode.Actions[1];
        Assert.Equal("flow:action:2", external.ActionId);
        Assert.Equal("production.external-test", external.RequiredCapability);
        Assert.Equal("Run", external.CommandName);
        Assert.Equal(FlowIrTargetReferenceKind.System, external.Target.Kind);
        Assert.Equal("system.tester", external.Target.Reference);
        Assert.Equal(1_234, external.Execution.TimeoutMilliseconds);
        Assert.Equal("external-1", external.Source.ElementId);
        using (var payload = JsonDocument.Parse(external.InputPayload!))
        {
            Assert.Equal("adapter.main", payload.RootElement
                .GetProperty("externalTestProgramAdapterId")
                .GetString());
        }

        Assert.Equal(
            ["openlineops_move_axis", "openlineops_run_external_test"],
            document.BlockDependencies.Select(dependency => dependency.BlockType));

        var artifact = new FlowIrCanonicalSerializer().Serialize(document);
        Assert.True(artifact.IsSuccess, artifact.Error.Message);
        Assert.Contains("\"blockDependencies\"", artifact.Value.CanonicalJson, StringComparison.Ordinal);
        Assert.True(new FlowIrCanonicalSerializer().Deserialize(artifact.Value.CanonicalJson).IsSuccess);

        var runtime = new FlowIrExecutableRuntimeProcessMapper(new FlowIrCanonicalSerializer()).Map(document);
        Assert.True(runtime.IsSuccess, runtime.Error.Message);
        var first = Assert.Single(runtime.Value.Nodes, node => node.NodeId.Value == "flow");
        var second = Assert.Single(runtime.Value.Nodes, node => node.NodeId.Value == "flow:block-action:2");
        Assert.Equal("motion.axis", first.TargetCapability.Value);
        Assert.Equal("flow:action:1", first.ActionId.Value);
        Assert.Equal("System", first.Target.Kind);
        Assert.Equal("system.motion", first.Target.TargetId);
        Assert.Equal("production.external-test", second.TargetCapability.Value);
        Assert.Equal("flow:action:2", second.ActionId.Value);
        Assert.Equal("System", second.Target.Kind);
        Assert.Equal("system.tester", second.Target.TargetId);
        Assert.Contains(runtime.Value.Transitions, transition =>
            transition.FromNodeId.Value == "flow"
            && transition.ToNodeId.Value == "flow:block-action:2");
        Assert.Contains(runtime.Value.Transitions, transition =>
            transition.FromNodeId.Value == "flow:block-action:2"
            && transition.ToNodeId.Value == "end");
    }

    [Theory]
    [InlineData("{\"blocks\":{\"languageVersion\":0}}", "Processes.FlowIrBlocklyWorkspaceInvalid")]
    [InlineData("{\"blocks\":{\"languageVersion\":1,\"blocks\":[]}}", "Processes.FlowIrBlocklyWorkspaceInvalid")]
    [InlineData("{\"blocks\":{\"languageVersion\":0,\"blocks\":[]},\"legacy\":true}", "Processes.FlowIrBlocklyWorkspaceInvalid")]
    [InlineData("{\"blocks\":{\"languageVersion\":0,\"blocks\":[{\"type\":\"unknown_block\",\"id\":\"x\",\"fields\":{}}]}}", "Processes.FlowIrBlocklyBlockUnknown")]
    public void CompileRejectsOldUnknownOrNonCurrentWorkspaceShapes(string workspace, string expectedCode)
    {
        var definition = CreatePublishedDefinition(workspace);

        var result = new ProcessFlowIrCompiler().Compile(definition);

        Assert.True(result.IsFailure);
        Assert.EndsWith(expectedCode, result.Error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileRejectsNonDeclarativeBlockDefinitions()
    {
        const string blockType = "legacy_python_template";
        var definition = CreatePublishedDefinition(
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"$TYPE","id":"legacy-1","fields":{}}]}}"""
                .Replace("$TYPE", blockType, StringComparison.Ordinal));
        var catalog = new[]
        {
            new ProcessBlocklyBlockDefinitionDetails(
                blockType,
                "Legacy",
                "Legacy",
                $$"""{"type":"{{blockType}}","message0":"legacy","previousStatement":null,"nextStatement":null}""",
                IsBuiltIn: false,
                Version: 1,
                CreatedAtUtc: RecordedAtUtc,
                UpdatedAtUtc: RecordedAtUtc,
                ExecutionMode: "PythonTemplate")
        };

        var result = new ProcessFlowIrCompiler().Compile(definition, catalog);

        Assert.True(result.IsFailure);
        Assert.EndsWith(
            "Processes.FlowIrBlocklyBlockNotDeclarative",
            result.Error.Code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompileAcceptsCurrentBlocklyCheckboxSerializationAndResolvesNodeContext()
    {
        var definition = CreatePublishedDefinition(
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"openlineops_result_from_input","id":"result-1","fields":{"INCLUDE_NODE_ID":"TRUE","INCLUDE_TIMESTAMP":"FALSE","INPUT_PAYLOAD":"scan-ok","OUTPUT_KEY":"normalized","STATUS":"ok"}}]}}""");

        var result = new ProcessFlowIrCompiler().Compile(definition);

        Assert.True(result.IsSuccess, result.Error.Message);
        var action = Assert.Single(
            Assert.Single(result.Value.Document.Nodes, node => node.Kind == FlowIrNodeKind.Blockly).Actions);
        Assert.Equal("runtime.flow", action.RequiredCapability);
        Assert.Equal("ResultPatch", action.CommandName);
        using var payload = JsonDocument.Parse(action.InputPayload!);
        var assignments = payload.RootElement.GetProperty("assignments").EnumerateArray().ToArray();
        Assert.Equal(3, assignments.Length);
        Assert.Contains(assignments, assignment =>
            assignment.GetProperty("key").GetString() == "node"
            && assignment.GetProperty("value").GetString() == "flow");
        Assert.DoesNotContain(assignments, assignment =>
            assignment.GetProperty("key").GetString() == "timestamp_utc");
    }

    [Fact]
    public void CanonicalRoundTripPreservesNumericActionOrderBeyondNineActions()
    {
        JsonObject? chain = null;
        for (var index = 12; index >= 1; index -= 1)
        {
            var block = new JsonObject
            {
                ["type"] = "openlineops_wait",
                ["id"] = $"wait-{index}",
                ["fields"] = new JsonObject { ["DURATION_MS"] = index }
            };
            if (chain is not null)
            {
                block["next"] = new JsonObject { ["block"] = chain };
            }

            chain = block;
        }

        var workspace = new JsonObject
        {
            ["blocks"] = new JsonObject
            {
                ["languageVersion"] = 0,
                ["blocks"] = new JsonArray(chain)
            }
        }.ToJsonString();
        var compilation = new ProcessFlowIrCompiler().Compile(CreatePublishedDefinition(workspace));
        Assert.True(compilation.IsSuccess, compilation.Error.Message);

        var serializer = new FlowIrCanonicalSerializer();
        var artifact = serializer.Serialize(compilation.Value.Document);
        Assert.True(artifact.IsSuccess, artifact.Error.Message);
        var roundTrip = serializer.Deserialize(artifact.Value.CanonicalJson);
        Assert.True(roundTrip.IsSuccess, roundTrip.Error.Message);
        Assert.Equal(
            Enumerable.Range(1, 12).Select(index => $"flow:action:{index}"),
            Assert.Single(roundTrip.Value.Nodes, node => node.Kind == FlowIrNodeKind.Blockly)
                .Actions
                .Select(action => action.ActionId));
    }

    [Fact]
    public void CompileFreezesExactCustomBlockVersionAndRejectsTamperedContractHash()
    {
        const string blockType = "custom_wait";
        var contract = new RuntimeActionContract(
            RuntimeActionContractSchema.Current,
            "custom.wait",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["DURATION_MS"] = new(
                    RuntimeActionFieldType.WholeNumber,
                    Required: true,
                    Minimum: 0)
            },
            new RuntimeDelayEmit(new RuntimeActionFieldValue("DURATION_MS")));
        var artifact = new RuntimeActionContractCanonicalSerializer().Serialize(contract);
        Assert.True(artifact.IsSuccess, artifact.Error.Message);
        var definition = CreatePublishedDefinition(
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"$TYPE","id":"wait-1","fields":{"DURATION_MS":42}}]}}"""
                .Replace("$TYPE", blockType, StringComparison.Ordinal));
        var definitionDetails = new ProcessBlocklyBlockDefinitionDetails(
            blockType,
            "Flow",
            "Custom Wait",
            """{"type":"$TYPE","message0":"wait %1","args0":[{"type":"field_number","name":"DURATION_MS","value":1}],"previousStatement":null,"nextStatement":null}"""
                .Replace("$TYPE", blockType, StringComparison.Ordinal),
            IsBuiltIn: false,
            Version: 7,
            CreatedAtUtc: RecordedAtUtc,
            UpdatedAtUtc: RecordedAtUtc,
            ExecutionMode: ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            RuntimeActionContractSchemaVersion: artifact.Value.SchemaVersion,
            RuntimeActionContractJson: artifact.Value.CanonicalJson,
            RuntimeActionContractSha256: artifact.Value.Sha256);

        var result = new ProcessFlowIrCompiler().Compile(definition, [definitionDetails]);

        Assert.True(result.IsSuccess, result.Error.Message);
        var dependency = Assert.Single(result.Value.Document.BlockDependencies);
        Assert.Equal(7, dependency.Version);
        Assert.Equal(artifact.Value.Sha256, dependency.ContractSha256);
        Assert.Equal($"{blockType}@7#{artifact.Value.Sha256}", dependency.LockId);

        var tampered = new ProcessFlowIrCompiler().Compile(
            definition,
            [definitionDetails with { RuntimeActionContractSha256 = new string('0', 64) }]);
        Assert.True(tampered.IsFailure);
        Assert.EndsWith(
            "Processes.FlowIrBlocklyContractHashMismatch",
            tampered.Error.Code,
            StringComparison.Ordinal);
    }

    private static ProcessDefinition CreatePublishedDefinition(string workspace)
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId("blockly-process"),
            new ProcessVersionId("blockly-process@1"),
            "Blockly Process",
            RecordedAtUtc);
        Assert.True(definition.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        Assert.True(definition.AddNode(ProcessNode.Blockly(
            new ProcessNodeId("flow"),
            "Flow",
            workspace,
            TimeSpan.FromMinutes(10))).Succeeded);
        Assert.True(definition.AddNode(ProcessNode.End(new ProcessNodeId("end"), "End")).Succeeded);
        Assert.True(definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("start-flow"),
            new ProcessNodeId("start"),
            new ProcessNodeId("flow"))).Succeeded);
        Assert.True(definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("flow-end"),
            new ProcessNodeId("flow"),
            new ProcessNodeId("end"))).Succeeded);
        var publish = definition.Publish(RecordedAtUtc.AddMinutes(1));
        Assert.True(publish.Succeeded, publish.Message);
        return definition;
    }

    private static string Sha256(string value) => Convert
        .ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
