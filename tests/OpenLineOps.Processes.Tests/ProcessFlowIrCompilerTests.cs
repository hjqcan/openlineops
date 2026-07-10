using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Operations;
using OpenLineOps.Processes.Domain.Transitions;

namespace OpenLineOps.Processes.Tests;

public sealed class ProcessFlowIrCompilerTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(1);

    private readonly ProcessFlowIrCompiler _compiler = new();

    [Fact]
    public void CompileProducesDeterministicDeviceCommandFlowIr()
    {
        var firstDefinition = CreateCommandDefinition(reverseInsertionOrder: false);
        var secondDefinition = CreateCommandDefinition(reverseInsertionOrder: true);
        Publish(firstDefinition);
        Publish(secondDefinition);

        var firstResult = _compiler.Compile(firstDefinition);
        var secondResult = _compiler.Compile(secondDefinition);

        Assert.True(firstResult.IsSuccess, firstResult.Error.Message);
        Assert.True(secondResult.IsSuccess, secondResult.Error.Message);
        Assert.Equal(
            JsonSerializer.Serialize(firstResult.Value),
            JsonSerializer.Serialize(secondResult.Value));

        var compilation = firstResult.Value;
        var document = compilation.Document;
        Assert.Equal(FlowIrSchemaVersions.V1, document.SchemaVersion);
        Assert.Equal("packaging-line-eol", document.ProcessDefinitionId);
        Assert.Equal("packaging-line-eol@1.0.0", document.ProcessVersionId);
        Assert.Equal("start", document.StartNodeId);
        Assert.Equal(["end", "inspect", "start"], document.Nodes.Select(node => node.NodeId));
        Assert.Equal(["inspect-to-end", "start-to-inspect"], document.Transitions.Select(transition => transition.TransitionId));
        Assert.Empty(compilation.Diagnostics);

        var commandNode = Assert.Single(document.Nodes, node => node.NodeId == "inspect");
        Assert.Equal(FlowIrNodeKind.Command, commandNode.Kind);
        var action = Assert.Single(commandNode.Actions);
        Assert.Equal("inspect:action:1", action.ActionId);
        Assert.Equal(FlowIrActionKind.DeviceCommand, action.Kind);
        Assert.Equal("vision-camera", action.RequiredCapability);
        Assert.Equal("Inspect", action.CommandName);
        Assert.Equal(FlowIrTargetReferenceKind.Capability, action.Target.Kind);
        Assert.Equal("vision-camera", action.Target.Reference);
        Assert.Equal("scan-ok", action.InputPayload);
        Assert.Equal(30_000, action.Execution.TimeoutMilliseconds);
        Assert.Equal(0, action.Execution.RetryLimit);
        Assert.Equal(FlowIrCancellationMode.Cooperative, action.Execution.CancellationMode);
        Assert.Null(action.PythonScript);
        Assert.Null(action.DynamicChildren);
        Assert.Equal(FlowIrSourceElementKind.ProcessNode, action.Source.ElementKind);
        Assert.Equal("inspect", action.Source.ElementId);
        Assert.Null(action.Source.ContentHash);
    }

    [Fact]
    public void CompileModelsPythonScriptAsRuntimeExpandedActionContainer()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.PythonScript(
            NodeId("normalize"),
            "Normalize",
            ProcessScriptEditorMode.Blockly,
            """{"blocks":{"languageVersion":0}}""",
            "result = {'automation_plan': []}",
            scriptVersion: "3",
            scriptTimeout: TimeSpan.FromSeconds(12),
            inputPayload: "raw-reading"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, Transition("start-to-normalize", "start", "normalize"));
        AddTransition(definition, Transition("normalize-to-end", "normalize", "end"));
        Publish(definition);

        var result = _compiler.Compile(definition);

        Assert.True(result.IsSuccess, result.Error.Message);
        var scriptNode = Assert.Single(result.Value.Document.Nodes, node => node.NodeId == "normalize");
        var action = Assert.Single(scriptNode.Actions);
        Assert.Equal(FlowIrActionKind.PythonScript, action.Kind);
        Assert.Equal("process.python-script", action.RequiredCapability);
        Assert.Equal("PythonScript.Execute", action.CommandName);
        Assert.Equal("process.python-script", action.Target.Reference);
        Assert.Equal(12_000, action.Execution.TimeoutMilliseconds);

        var script = Assert.IsType<FlowIrPythonScript>(action.PythonScript);
        Assert.Equal("Python", script.Language);
        Assert.Equal("Blockly", script.EditorMode);
        Assert.Equal("result = {'automation_plan': []}", script.SourceCode);
        Assert.Equal("3", script.Version);
        Assert.Equal(action.Source.ContentHash, script.SourceHash);
        Assert.Equal("""{"blocks":{"languageVersion":0}}""", script.BlocklyWorkspaceJson);

        var dynamicChildren = Assert.IsType<FlowIrDynamicActionSlot>(action.DynamicChildren);
        Assert.Equal("normalize:action:1:automation-plan", dynamicChildren.SlotId);
        Assert.Equal(FlowIrDynamicActionExpansionKind.RuntimeAutomationPlan, dynamicChildren.ExpansionKind);
        Assert.Equal("normalize:action:1:child:", dynamicChildren.ChildActionIdPrefix);
        Assert.Equal(1, dynamicChildren.SequenceBase);
        Assert.False(dynamicChildren.IsCompileTimeResolved);
        Assert.Equal(FlowIrChildSourceMappingMode.ContainerOnly, dynamicChildren.SourceMappingMode);
        Assert.Equal("normalize", dynamicChildren.Source.ElementId);

        var runtimeResult = new FlowIrExecutableRuntimeProcessMapper(new FlowIrCanonicalSerializer())
            .Map(result.Value.Document);
        Assert.True(runtimeResult.IsSuccess, runtimeResult.Error.Message);
        var runtimeNode = Assert.Single(runtimeResult.Value.Nodes);
        Assert.Equal("normalize:action:1", runtimeNode.EffectiveActionId.Value);
        var runtimeSlot = Assert.IsType<OpenLineOps.Runtime.Application.Processes.ExecutableRuntimeDynamicActionSlot>(
            runtimeNode.DynamicChildren);
        Assert.Equal("normalize:action:1:automation-plan", runtimeSlot.SlotId);
        Assert.Equal("normalize:action:1:child:", runtimeSlot.ChildActionIdPrefix);
        Assert.Equal(1, runtimeSlot.SequenceBase);

        var diagnostic = Assert.Single(result.Value.Diagnostics);
        Assert.Equal(FlowIrDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Processes.FlowIrPythonChildActionsRuntimeResolved", diagnostic.Code);
        Assert.Equal("normalize", diagnostic.Source.ElementId);
    }

    [Fact]
    public void CompilePreservesCountedLoopTransitionMetadata()
    {
        var definition = CreateLoopingDefinition();
        Publish(definition);

        var result = _compiler.Compile(definition);

        Assert.True(result.IsSuccess, result.Error.Message);
        var transition = Assert.Single(
            result.Value.Document.Transitions,
            candidate => candidate.TransitionId == "route-to-inspect-retry");
        Assert.Equal("route", transition.FromNodeId);
        Assert.Equal("inspect", transition.ToNodeId);
        Assert.Equal("retry", transition.Label);
        Assert.Equal(FlowIrLoopPolicy.Counted, transition.LoopPolicy);
        Assert.Equal(3, transition.MaxTraversals);
        Assert.Equal(FlowIrSourceElementKind.ProcessTransition, transition.Source.ElementKind);
        Assert.Equal("route-to-inspect-retry", transition.Source.ElementId);
    }

    [Fact]
    public void CompileRejectsDraftDefinition()
    {
        var definition = CreateCommandDefinition(reverseInsertionOrder: false);

        var result = _compiler.Compile(definition);

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Processes.FlowIrDefinitionNotPublished", result.Error.Code);
    }

    [Fact]
    public void CompileRejectsBranchingOutsideDecisionNode()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30)));
        AddNode(definition, ProcessNode.End(NodeId("accepted"), "Accepted"));
        AddNode(definition, ProcessNode.End(NodeId("rejected"), "Rejected"));
        AddTransition(definition, Transition("start-to-inspect", "start", "inspect"));
        AddTransition(definition, Transition("inspect-to-accepted", "inspect", "accepted", "accepted"));
        AddTransition(definition, Transition("inspect-to-rejected", "inspect", "rejected", "rejected"));
        Publish(definition);

        var result = _compiler.Compile(definition);

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Processes.FlowIrBranchingRequiresDecision", result.Error.Code);
    }

    [Fact]
    public void CompileRejectsPublishedGraphWithoutExecutableActions()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, Transition("start-to-end", "start", "end"));
        Publish(definition);

        var result = _compiler.Compile(definition);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.FlowIrNoExecutableActions", result.Error.Code);
    }

    [Fact]
    public void CanonicalSerializerUsesStableCamelCaseStringEnumsAndHash()
    {
        var firstDefinition = CreateCommandDefinition(reverseInsertionOrder: false);
        var secondDefinition = CreateCommandDefinition(reverseInsertionOrder: true);
        Publish(firstDefinition);
        Publish(secondDefinition);
        var firstCompilation = _compiler.Compile(firstDefinition);
        var secondCompilation = _compiler.Compile(secondDefinition);
        var serializer = new FlowIrCanonicalSerializer();

        var first = serializer.Serialize(firstCompilation.Value.Document);
        var second = serializer.Serialize(secondCompilation.Value.Document);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(second.IsSuccess, second.Error.Message);
        Assert.Equal(first.Value.CanonicalJson, second.Value.CanonicalJson);
        Assert.Equal(first.Value.Sha256, second.Value.Sha256);
        Assert.StartsWith("{\"schemaVersion\":\"openlineops.flow-ir/v1\"", first.Value.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"deviceCommand\"", first.Value.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":30000", first.Value.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"cancellationMode\":\"cooperative\"", first.Value.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutTicks", first.Value.CanonicalJson, StringComparison.Ordinal);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.Value.CanonicalJson)))
                .ToLowerInvariant(),
            first.Value.Sha256);

        var roundTrip = serializer.Deserialize(first.Value.CanonicalJson);
        Assert.True(roundTrip.IsSuccess, roundTrip.Error.Message);
        Assert.Equal(first.Value.Sha256, serializer.Serialize(roundTrip.Value).Value.Sha256);
    }

    [Fact]
    public void CanonicalSerializerRejectsPythonSourceHashMismatch()
    {
        var definition = CreatePythonDefinition();
        Publish(definition);
        var compilation = _compiler.Compile(definition);
        var document = compilation.Value.Document;
        var tampered = document with
        {
            Nodes = document.Nodes
                .Select(node => node.NodeId != "normalize"
                    ? node
                    : node with
                    {
                        Actions = node.Actions
                            .Select(action => action with
                            {
                                PythonScript = action.PythonScript! with
                                {
                                    SourceCode = "result = {'tampered': True}"
                                }
                            })
                            .ToImmutableArray()
                    })
                .ToImmutableArray()
        };

        var result = new FlowIrCanonicalSerializer().Serialize(tampered);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.FlowIrDocumentInvalid", result.Error.Code);
        Assert.Contains("source hash", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalDeserializerRejectsHostileNullStructureDeterministically()
    {
        const string hostileJson =
            "{\"schemaVersion\":\"openlineops.flow-ir/v1\",\"processDefinitionId\":\"process.main\",\"processVersionId\":\"process.main@1\",\"displayName\":\"Main\",\"startNodeId\":\"start\",\"nodes\":[null],\"transitions\":[]}";

        var result = new FlowIrCanonicalSerializer().Deserialize(hostileJson);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.FlowIrDocumentInvalid", result.Error.Code);
    }

    [Fact]
    public void RuntimeMapperMapsFrozenFlowIrWithoutReadingProcessDefinition()
    {
        var definition = CreateLoopingDefinition();
        Publish(definition);
        var compilation = _compiler.Compile(definition);
        var serializer = new FlowIrCanonicalSerializer();
        var mapper = new FlowIrExecutableRuntimeProcessMapper(serializer);

        var result = mapper.Map(compilation.Value.Document);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal("packaging-line-eol", result.Value.ProcessDefinitionId.Value);
        Assert.Equal("packaging-line-eol@1.0.0", result.Value.ProcessVersionId.Value);
        Assert.Equal("start", result.Value.StartNodeId!.Value);
        var command = Assert.Single(result.Value.Nodes);
        Assert.Equal("inspect", command.NodeId.Value);
        Assert.Equal("vision-camera", command.TargetCapability.Value);
        Assert.Equal("Inspect", command.CommandName);
        Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
        var loop = Assert.Single(result.Value.Transitions, transition => transition.Label == "retry");
        Assert.Equal(3, loop.MaxTraversals);
    }

    [Fact]
    public void CompileRejectsTimeoutThatIsNotAWholeMillisecond()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1)));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, Transition("start-to-inspect", "start", "inspect"));
        AddTransition(definition, Transition("inspect-to-end", "inspect", "end"));
        Publish(definition);

        var result = _compiler.Compile(definition);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.FlowIrTimeoutPrecisionUnsupported", result.Error.Code);
    }

    private static ProcessDefinition CreateCommandDefinition(bool reverseInsertionOrder)
    {
        var definition = CreateDefinition();
        var nodes = new[]
        {
            ProcessNode.Start(NodeId("start"), "Start"),
            ProcessNode.Command(
                NodeId("inspect"),
                "Inspect",
                CapabilityId("vision-camera"),
                commandName: "Inspect",
                commandTimeout: TimeSpan.FromSeconds(30),
                inputPayload: "scan-ok"),
            ProcessNode.End(NodeId("end"), "End")
        };
        var transitions = new[]
        {
            Transition("start-to-inspect", "start", "inspect"),
            Transition("inspect-to-end", "inspect", "end")
        };

        foreach (var node in reverseInsertionOrder ? nodes.Reverse() : nodes)
        {
            AddNode(definition, node);
        }

        foreach (var transition in reverseInsertionOrder ? transitions.Reverse() : transitions)
        {
            AddTransition(definition, transition);
        }

        return definition;
    }

    private static ProcessDefinition CreateLoopingDefinition()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30)));
        AddNode(definition, ProcessNode.Decision(NodeId("route"), "Route"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, Transition("start-to-inspect", "start", "inspect"));
        AddTransition(definition, Transition("inspect-to-route", "inspect", "route"));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("route-to-inspect-retry"),
            NodeId("route"),
            NodeId("inspect"),
            label: "retry",
            loopPolicy: ProcessTransitionLoopPolicy.Counted,
            maxTraversals: 3));
        AddTransition(definition, Transition("route-to-end-ok", "route", "end", "ok"));
        return definition;
    }

    private static ProcessDefinition CreatePythonDefinition()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.PythonScript(
            NodeId("normalize"),
            "Normalize",
            ProcessScriptEditorMode.Blockly,
            """{"blocks":{"languageVersion":0}}""",
            "result = {'automation_plan': []}",
            scriptVersion: "3",
            scriptTimeout: TimeSpan.FromSeconds(12)));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, Transition("start-to-normalize", "start", "normalize"));
        AddTransition(definition, Transition("normalize-to-end", "normalize", "end"));
        return definition;
    }

    private static ProcessDefinition CreateDefinition()
    {
        return ProcessDefinition.Create(
            new ProcessDefinitionId("packaging-line-eol"),
            new ProcessVersionId("packaging-line-eol@1.0.0"),
            "Packaging Line End Of Line Test",
            CreatedAtUtc);
    }

    private static ProcessTransition Transition(
        string transitionId,
        string fromNodeId,
        string toNodeId,
        string? label = null)
    {
        return ProcessTransition.Create(
            TransitionId(transitionId),
            NodeId(fromNodeId),
            NodeId(toNodeId),
            label);
    }

    private static void Publish(ProcessDefinition definition)
    {
        AssertAccepted(definition.Publish(PublishedAtUtc));
    }

    private static void AddNode(ProcessDefinition definition, ProcessNode node)
    {
        AssertAccepted(definition.AddNode(node));
    }

    private static void AddTransition(ProcessDefinition definition, ProcessTransition transition)
    {
        AssertAccepted(definition.AddTransition(transition));
    }

    private static void AssertAccepted(ProcessOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }

    private static ProcessNodeId NodeId(string value) => new(value);

    private static ProcessTransitionId TransitionId(string value) => new(value);

    private static ProcessCapabilityId CapabilityId(string value) => new(value);
}
