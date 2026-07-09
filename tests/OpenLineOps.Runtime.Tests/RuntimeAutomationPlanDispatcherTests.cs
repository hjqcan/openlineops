using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeAutomationPlanDispatcherTests
{
    [Fact]
    public async Task DispatchAsyncReturnsOriginalScriptResultWhenPayloadHasNoAutomationPlan()
    {
        var dispatcher = new RuntimeAutomationPlanDispatcher();
        var request = CreateRequest();
        var scriptResult = RuntimeCommandExecutionResult.Completed("""{"normalized":true}""");
        var invoked = false;

        var result = await dispatcher.DispatchAsync(
            request,
            scriptResult,
            (context, cancellationToken) =>
            {
                invoked = true;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed());
            });

        Assert.Same(scriptResult, result);
        Assert.False(invoked);
    }

    [Fact]
    public async Task DispatchAsyncMapsAutomationActionsToRuntimeCommands()
    {
        var dispatcher = new RuntimeAutomationPlanDispatcher();
        var request = CreateRequest();
        var scriptResult = RuntimeCommandExecutionResult.Completed(
            """
            {
              "automation_plan": [
                {"type":"axis.move","axis":"x","distance":15,"unit":"mm"},
                {"type":"io.light","channel":"lamp-main","state":true},
                {"type":"motor.rotate","motor":"m1","degrees":90}
              ]
            }
            """);
        var commands = new List<RuntimeCommandExecutionContext>();

        var result = await dispatcher.DispatchAsync(
            request,
            scriptResult,
            (context, cancellationToken) =>
            {
                commands.Add(context);
                return ValueTask.FromResult(
                    RuntimeCommandExecutionResult.Completed(
                        $$"""{"executed":"{{context.CommandName}}"}"""));
            });

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(3, commands.Count);
        Assert.Equal("motion.axis", commands[0].TargetCapability.Value);
        Assert.Equal("MoveAxis", commands[0].CommandName);
        Assert.Equal("io.light", commands[1].TargetCapability.Value);
        Assert.Equal("SetLight", commands[1].CommandName);
        Assert.Equal("motion.motor", commands[2].TargetCapability.Value);
        Assert.Equal("RotateMotor", commands[2].CommandName);
        Assert.All(commands, command =>
        {
            Assert.Equal(request.CommandContext.SessionId, command.SessionId);
            Assert.Equal(request.CommandContext.StationId, command.StationId);
            Assert.Equal(request.CommandContext.ConfigurationSnapshotId, command.ConfigurationSnapshotId);
            Assert.Equal(request.CommandContext.StepId, command.StepId);
            Assert.StartsWith("node-script.automation.", command.NodeId.Value, StringComparison.Ordinal);
        });

        using var document = JsonDocument.Parse(result.Payload!);
        var dispatch = document.RootElement.GetProperty("automation_dispatch");
        Assert.Equal(3, dispatch.GetArrayLength());
        Assert.Equal("Completed", dispatch[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task DispatchAsyncMapsExplicitCommandExecuteActionsToRuntimeCommands()
    {
        var dispatcher = new RuntimeAutomationPlanDispatcher();
        var request = CreateRequest();
        var scriptResult = RuntimeCommandExecutionResult.Completed(
            """
            {
              "automation_plan": [
                {
                  "type": "command.execute",
                  "capability": "device.loopback",
                  "command": "Echo",
                  "payload": {"message": "hello"},
                  "timeout_ms": 2500
                }
              ]
            }
            """);
        var commands = new List<RuntimeCommandExecutionContext>();

        var result = await dispatcher.DispatchAsync(
            request,
            scriptResult,
            (context, cancellationToken) =>
            {
                commands.Add(context);
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed());
            });

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        var command = Assert.Single(commands);
        Assert.Equal("device.loopback", command.TargetCapability.Value);
        Assert.Equal("Echo", command.CommandName);
        Assert.Equal("""{"message":"hello"}""", command.InputPayload);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), command.Timeout);
    }

    [Fact]
    public async Task DispatchAsyncExecutesWaitActionsLocally()
    {
        var dispatcher = new RuntimeAutomationPlanDispatcher();
        var request = CreateRequest();
        var scriptResult = RuntimeCommandExecutionResult.Completed(
            """{"automation_plan":[{"type":"flow.wait","duration_ms":0}]}""");
        var invoked = false;

        var result = await dispatcher.DispatchAsync(
            request,
            scriptResult,
            (context, cancellationToken) =>
            {
                invoked = true;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed());
            });

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.False(invoked);

        using var document = JsonDocument.Parse(result.Payload!);
        var dispatch = document.RootElement.GetProperty("automation_dispatch");
        Assert.Single(dispatch.EnumerateArray());
        Assert.Equal("flow.wait", dispatch[0].GetProperty("type").GetString());
        Assert.Equal("Completed", dispatch[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task DispatchAsyncRejectsUnsupportedAutomationActionType()
    {
        var dispatcher = new RuntimeAutomationPlanDispatcher();
        var request = CreateRequest();
        var scriptResult = RuntimeCommandExecutionResult.Completed(
            """{"automation_plan":[{"type":"camera.capture"}]}""");

        var result = await dispatcher.DispatchAsync(
            request,
            scriptResult,
            (context, cancellationToken) =>
                ValueTask.FromResult(RuntimeCommandExecutionResult.Completed()));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("camera.capture", result.Reason, StringComparison.Ordinal);
        Assert.NotNull(result.Payload);
        Assert.Contains("automation_dispatch", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsyncStopsWhenActionCommandIsRejected()
    {
        var dispatcher = new RuntimeAutomationPlanDispatcher();
        var request = CreateRequest();
        var scriptResult = RuntimeCommandExecutionResult.Completed(
            """
            {
              "automation_plan": [
                {"type":"axis.move","axis":"x","distance":15},
                {"type":"motor.rotate","motor":"m1","degrees":90}
              ]
            }
            """);
        var commands = new List<RuntimeCommandExecutionContext>();

        var result = await dispatcher.DispatchAsync(
            request,
            scriptResult,
            (context, cancellationToken) =>
            {
                commands.Add(context);
                return ValueTask.FromResult(
                    RuntimeCommandExecutionResult.Rejected("Axis x is not homed."));
            });

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Single(commands);
        Assert.Contains("Automation action 1 (axis.move) Rejected", result.Reason, StringComparison.Ordinal);
        Assert.Contains("Axis x is not homed", result.Reason, StringComparison.Ordinal);
    }

    private static RuntimeScriptExecutionRequest CreateRequest()
    {
        return new RuntimeScriptExecutionRequest(
            CreateContext(),
            "Python",
            "result = {'automation_plan': automation_plan}",
            "1",
            null);
    }

    private static RuntimeCommandExecutionContext CreateContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260630-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-script"),
            new RuntimeCapabilityId(RuntimeScriptCommand.PythonCapability),
            RuntimeScriptCommand.PythonCommandName,
            null,
            TimeSpan.FromSeconds(15));
    }
}
