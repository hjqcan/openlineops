using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Events;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Operations;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Domain.Validation;

namespace OpenLineOps.Processes.Tests;

public sealed class ProcessDefinitionTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(5);

    [Fact]
    public void PublishValidGraphMarksDefinitionPublished()
    {
        var definition = CreateValidDefinition();

        var result = definition.Publish(PublishedAtUtc);

        Assert.True(result.Succeeded);
        Assert.Equal(ProcessDefinitionStatus.Published, definition.Status);
        Assert.True(definition.IsPublished);
        Assert.Equal(PublishedAtUtc, definition.PublishedAtUtc);

        var domainEvent = Assert.IsType<ProcessDefinitionPublishedDomainEvent>(Assert.Single(definition.DomainEvents));
        Assert.Equal(definition.Id, domainEvent.ProcessDefinitionId);
        Assert.Equal(definition.VersionId, domainEvent.VersionId);
        Assert.Equal(PublishedAtUtc, domainEvent.PublishedAtUtc);
    }

    [Fact]
    public void PublishValidGraphWithBlocklyNodeMarksDefinitionPublished()
    {
        var definition = CreateDefinition();

        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Blockly(
            NodeId("normalize"),
            "Normalize Measurement",
            """{"blocks":{"languageVersion":0,"blocks":[]}}""",
            executionTimeout: TimeSpan.FromSeconds(10)));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-normalize"), NodeId("start"), NodeId("normalize")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("normalize-to-end"), NodeId("normalize"), NodeId("end")));

        var result = definition.Publish(PublishedAtUtc);

        Assert.True(result.Succeeded);
        Assert.Equal(ProcessDefinitionStatus.Published, definition.Status);
    }

    [Fact]
    public void PublishInvalidGraphDoesNotPublish()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Command(NodeId("inspect"), "Inspect", CapabilityId("vision-camera")));

        var result = definition.Publish(PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Processes.PublishValidationFailed", result.Code);
        Assert.Equal(ProcessDefinitionStatus.Draft, definition.Status);
        Assert.Null(definition.PublishedAtUtc);
        Assert.Empty(definition.DomainEvents);
    }

    [Fact]
    public void ValidatorRejectsGraphWithoutStartNode()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Command(NodeId("inspect"), "Inspect", CapabilityId("vision-camera")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.GraphStartNodeCountInvalid");
    }

    [Fact]
    public void ValidatorRejectsUnreachableNode()
    {
        var definition = CreateValidDefinition();
        AddNode(definition, ProcessNode.Command(NodeId("orphan"), "Orphan", CapabilityId("unused-capability")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.NodeUnreachable");
    }

    [Fact]
    public void ValidatorRejectsUnknownTransitionTarget()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddTransition(
            definition,
            ProcessTransition.Create(TransitionId("start-to-missing"), NodeId("start"), NodeId("missing")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.TransitionTargetMissing");
    }

    [Fact]
    public void ValidatorRejectsCycles()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(NodeId("inspect"), "Inspect", CapabilityId("vision-camera")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("inspect-to-start"), NodeId("inspect"), NodeId("start")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.GraphCycleDetected");
    }

    [Fact]
    public void PublishGraphWithExplicitCountedLoopPolicyMarksDefinitionPublished()
    {
        var definition = CreateLoopingDefinition();

        var result = definition.Publish(PublishedAtUtc);

        Assert.True(result.Succeeded);
        Assert.Equal(ProcessDefinitionStatus.Published, definition.Status);
        var loopTransition = Assert.Single(
            definition.Transitions,
            transition => transition.Id == TransitionId("route-to-inspect-retry"));
        Assert.Equal(ProcessTransitionLoopPolicy.Counted, loopTransition.LoopPolicy);
        Assert.Equal(3, loopTransition.MaxTraversals);
    }

    [Fact]
    public void ValidatorRejectsCountedLoopPolicyWithoutMaxTraversals()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30)));
        AddNode(definition, ProcessNode.Decision(NodeId("route"), "Route Result"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("inspect-to-route"), NodeId("inspect"), NodeId("route")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("route-to-inspect-retry"),
            NodeId("route"),
            NodeId("inspect"),
            label: "retry",
            loopPolicy: ProcessTransitionLoopPolicy.Counted));
        AddTransition(definition, ProcessTransition.Create(TransitionId("route-to-end-ok"), NodeId("route"), NodeId("end"), label: "ok"));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.LoopPolicyMaxTraversalsInvalid");
    }

    [Fact]
    public void ValidatorRejectsCountedLoopPolicyFromNonDecisionNode()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30)));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("inspect-to-start-retry"),
            NodeId("inspect"),
            NodeId("start"),
            label: "retry",
            loopPolicy: ProcessTransitionLoopPolicy.Counted,
            maxTraversals: 2));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.LoopPolicySourceMustBeDecision");
    }

    [Fact]
    public void PublishedDefinitionCannotBeModified()
    {
        var definition = CreateValidDefinition();
        var publishResult = definition.Publish(PublishedAtUtc);

        var addNodeResult = definition.AddNode(ProcessNode.End(NodeId("extra-end"), "Extra End"));
        var addTransitionResult = definition.AddTransition(
            ProcessTransition.Create(TransitionId("extra-transition"), NodeId("end"), NodeId("extra-end")));

        Assert.True(publishResult.Succeeded);
        Assert.False(addNodeResult.Succeeded);
        Assert.False(addTransitionResult.Succeeded);
        Assert.Equal("Processes.DefinitionImmutable", addNodeResult.Code);
        Assert.Equal("Processes.DefinitionImmutable", addTransitionResult.Code);
    }

    [Fact]
    public void AddDuplicateNodeIsRejected()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));

        var result = definition.AddNode(ProcessNode.Start(NodeId("start"), "Duplicate Start"));

        Assert.False(result.Succeeded);
        Assert.Equal("Processes.NodeAlreadyExists", result.Code);
        Assert.Single(definition.Nodes);
    }

    [Fact]
    public void CommandNodeWithoutCapabilityIsInvalid()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            requiredCapability: null,
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30)));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("inspect-to-end"), NodeId("inspect"), NodeId("end")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.CommandCapabilityMissing");
    }

    [Fact]
    public void CommandNodeWithoutExecutionMetadataIsInvalid()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(NodeId("inspect"), "Inspect", CapabilityId("vision-camera")));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("inspect-to-end"), NodeId("inspect"), NodeId("end")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.CommandNameMissing");
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.CommandTimeoutInvalid");
    }

    [Fact]
    public void PythonScriptNodeComputesSourceHash()
    {
        const string sourceCode = "result = {'ok': True}";

        var node = ProcessNode.PythonScript(
            NodeId("script"),
            "Script",
            sourceCode,
            scriptTimeout: TimeSpan.FromSeconds(5));
        var expectedHash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceCode)))
            .ToLowerInvariant();

        Assert.Equal(ProcessNodeKind.PythonScript, node.Kind);
        Assert.Equal("Python", node.ScriptLanguage);
        Assert.Null(node.BlocklyWorkspaceJson);
        Assert.Equal(sourceCode, node.ScriptSourceCode);
        Assert.Equal(expectedHash, node.ScriptSourceHash);
        Assert.Equal("1", node.ScriptVersion);
        Assert.Equal(TimeSpan.FromSeconds(5), node.ScriptTimeout);
    }

    [Fact]
    public void PythonScriptNodeWithoutRequiredMetadataIsInvalid()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.PythonScript(
            NodeId("script"),
            "Script",
            sourceCode: null,
            scriptVersion: "",
            scriptTimeout: null));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-script"), NodeId("start"), NodeId("script")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("script-to-end"), NodeId("script"), NodeId("end")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.PythonScriptSourceMissing");
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.PythonScriptSourceHashMissing");
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.PythonScriptVersionMissing");
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.PythonScriptTimeoutInvalid");
    }

    [Fact]
    public void BlocklyNodeWithoutWorkspaceIsInvalid()
    {
        var definition = CreateDefinition();
        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Blockly(
            NodeId("script"),
            "Script",
            workspaceJson: null,
            executionTimeout: TimeSpan.FromSeconds(5)));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-script"), NodeId("start"), NodeId("script")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("script-to-end"), NodeId("script"), NodeId("end")));

        var report = ProcessGraphValidator.Validate(definition);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Processes.BlocklyWorkspaceMissing");
    }

    private static ProcessDefinition CreateValidDefinition()
    {
        var definition = CreateDefinition();

        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30),
            inputPayload: "scan-ok"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("inspect-to-end"), NodeId("inspect"), NodeId("end")));

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
            commandTimeout: TimeSpan.FromSeconds(30),
            inputPayload: "scan-ok"));
        AddNode(definition, ProcessNode.Decision(NodeId("route"), "Route Result"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(TransitionId("start-to-inspect"), NodeId("start"), NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(TransitionId("inspect-to-route"), NodeId("inspect"), NodeId("route")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("route-to-inspect-retry"),
            NodeId("route"),
            NodeId("inspect"),
            label: "retry",
            loopPolicy: ProcessTransitionLoopPolicy.Counted,
            maxTraversals: 3));
        AddTransition(definition, ProcessTransition.Create(TransitionId("route-to-end-ok"), NodeId("route"), NodeId("end"), label: "ok"));

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

    private static ProcessNodeId NodeId(string value)
    {
        return new ProcessNodeId(value);
    }

    private static ProcessTransitionId TransitionId(string value)
    {
        return new ProcessTransitionId(value);
    }

    private static ProcessCapabilityId CapabilityId(string value)
    {
        return new ProcessCapabilityId(value);
    }
}
