using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeAutomationPlanDispatcherTests
{
    private readonly RuntimeAutomationPlanExpander _expander = new();

    [Fact]
    public void ExpandCreatesStableIrDerivedChildIdentitiesAndCommands()
    {
        var result = _expander.Expand(
            CreateParent(),
            """
            {
              "automation_plan": [
                {"type":"axis.move","axis":"X","position":12},
                {"type":"command.execute","capability":"vision.camera","command":"Capture","payload":{"mode":"fast"},"timeout_ms":1250}
              ]
            }
            """);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.True(result.Value.HasAutomationPlan);
        Assert.Collection(
            result.Value.Actions,
            first =>
            {
                Assert.Equal(1, first.Sequence);
                Assert.Equal("script-node:action:1:child:1", first.Node.EffectiveActionId.Value);
                Assert.Equal("script-node:action:1:automation-plan:node:1", first.Node.NodeId.Value);
                Assert.Equal("motion.axis", first.Node.TargetCapability.Value);
                Assert.Equal("MoveAxis", first.Node.CommandName);
            },
            second =>
            {
                Assert.Equal(2, second.Sequence);
                Assert.Equal("script-node:action:1:child:2", second.Node.EffectiveActionId.Value);
                Assert.Equal("vision.camera", second.Node.TargetCapability.Value);
                Assert.Equal("Capture", second.Node.CommandName);
                Assert.Equal(TimeSpan.FromMilliseconds(1250), second.Node.Timeout);
                Assert.Equal("{\"mode\":\"fast\"}", second.Node.InputPayload);
            });
    }

    [Fact]
    public void ExpandPreflightsEntirePlanBeforeReturningAnyActions()
    {
        var result = _expander.Expand(
            CreateParent(),
            """
            {
              "automation_plan": [
                {"type":"axis.move","axis":"X","position":12},
                {"type":"unsupported.custom"}
              ]
            }
            """);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Runtime.AutomationPlanInvalid", result.Error.Code);
        Assert.Contains("action 2", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandMapsWaitToInternalRuntimeCommand()
    {
        var result = _expander.Expand(
            CreateParent(),
            """{"automation_plan":[{"type":"flow.wait","duration_ms":25}]}""");

        Assert.True(result.IsSuccess, result.Error.Message);
        var action = Assert.Single(result.Value.Actions);
        Assert.Equal(RuntimeFlowCommand.Capability, action.Node.TargetCapability.Value);
        Assert.Equal(RuntimeFlowCommand.WaitCommandName, action.Node.CommandName);
        using var payload = JsonDocument.Parse(action.Node.InputPayload!);
        Assert.Equal(25, payload.RootElement.GetProperty("durationMilliseconds").GetDouble());
    }

    [Fact]
    public void ExpandRejectsInvalidLaterWaitBeforeExecution()
    {
        var result = _expander.Expand(
            CreateParent(),
            """
            {"automation_plan":[
              {"type":"flow.wait","duration_ms":0},
              {"type":"flow.wait","duration_ms":-1}
            ]}
            """);

        Assert.True(result.IsFailure);
        Assert.Contains("action 2", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandRejectsNestedPythonExecution()
    {
        var result = _expander.Expand(
            CreateParent(),
            $$"""
            {"automation_plan":[{
              "type":"command.execute",
              "capability":"{{RuntimeScriptCommand.PythonCapability}}",
              "command":"{{RuntimeScriptCommand.PythonCommandName}}"
            }]}
            """);

        Assert.True(result.IsFailure);
        Assert.Contains("recursively", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletionPayloadIncludesStableChildIdentityAndOutcome()
    {
        var expansionResult = _expander.Expand(
            CreateParent(),
            """{"automation_plan":[{"type":"flow.wait","duration_ms":0}]}""");
        Assert.True(expansionResult.IsSuccess, expansionResult.Error.Message);

        var payload = expansionResult.Value.CreateCompletionPayload(
        [
            new RuntimeAutomationPlanActionResult(
                1,
                "script-node:action:1:child:1",
                "script-node:action:1:automation-plan:node:1",
                "flow.wait",
                RuntimeCommandExecutionOutcome.Completed,
                "waited",
                null)
        ]);

        using var document = JsonDocument.Parse(payload!);
        var dispatch = Assert.Single(document.RootElement.GetProperty("automation_dispatch").EnumerateArray());
        Assert.Equal("script-node:action:1:child:1", dispatch.GetProperty("actionId").GetString());
        Assert.Equal("Completed", dispatch.GetProperty("outcome").GetString());
    }

    private static ExecutableRuntimeNode CreateParent()
    {
        return new ExecutableRuntimeNode(
            new RuntimeNodeId("script-node"),
            "Script node",
            new RuntimeCapabilityId(RuntimeScriptCommand.PythonCapability),
            RuntimeScriptCommand.PythonCommandName,
            TimeSpan.FromSeconds(10),
            InputPayload: null,
            ActionId: new RuntimeActionId("script-node:action:1"),
            DynamicChildren: new ExecutableRuntimeDynamicActionSlot(
                "script-node:action:1:automation-plan",
                "script-node:action:1:child:",
                SequenceBase: 1,
                SourceMappingMode: "ContainerOnly"));
    }
}
