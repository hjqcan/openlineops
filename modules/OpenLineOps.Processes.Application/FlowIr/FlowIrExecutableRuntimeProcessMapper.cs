using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Scripting;
using RuntimeCapabilityId = OpenLineOps.Runtime.Domain.Identifiers.RuntimeCapabilityId;
using RuntimeActionId = OpenLineOps.Runtime.Domain.Identifiers.RuntimeActionId;
using RuntimeNodeId = OpenLineOps.Runtime.Domain.Identifiers.RuntimeNodeId;
using RuntimeProcessDefinitionId = OpenLineOps.Runtime.Domain.Identifiers.ProcessDefinitionId;
using RuntimeProcessVersionId = OpenLineOps.Runtime.Domain.Identifiers.ProcessVersionId;

namespace OpenLineOps.Processes.Application.FlowIr;

public sealed class FlowIrExecutableRuntimeProcessMapper : IFlowIrExecutableRuntimeProcessMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFlowIrCanonicalSerializer _serializer;

    public FlowIrExecutableRuntimeProcessMapper(IFlowIrCanonicalSerializer serializer)
    {
        _serializer = serializer;
    }

    public Result<ExecutableRuntimeProcess> Map(FlowIrDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var validationResult = _serializer.Serialize(document);
        if (validationResult.IsFailure)
        {
            return Result.Failure<ExecutableRuntimeProcess>(validationResult.Error);
        }

        try
        {
            var executableNodes = document.Nodes
                .Where(node => node.Kind is FlowIrNodeKind.Command
                    or FlowIrNodeKind.PythonScript
                    or FlowIrNodeKind.Blockly)
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .ToArray();
            var runtimeNodes = executableNodes
                .SelectMany(ToRuntimeNodes)
                .ToArray();
            var finalNodeIds = executableNodes.ToDictionary(
                node => node.NodeId,
                node => node.Kind == FlowIrNodeKind.Blockly && node.Actions.Length > 1
                    ? CreateBlocklyRuntimeNodeId(node.NodeId, node.Actions.Length)
                    : node.NodeId,
                StringComparer.Ordinal);
            var routingNodes = document.Nodes
                .Where(node => node.Kind is FlowIrNodeKind.Start
                    or FlowIrNodeKind.Decision
                    or FlowIrNodeKind.Delay
                    or FlowIrNodeKind.End)
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .Select(ToRoutingNode)
                .ToArray();
            var transitions = document.Transitions
                .OrderBy(transition => transition.TransitionId, StringComparer.Ordinal)
                .Select(transition => new ExecutableRuntimeTransition(
                    new RuntimeNodeId(finalNodeIds.GetValueOrDefault(
                        transition.FromNodeId,
                        transition.FromNodeId)),
                    new RuntimeNodeId(transition.ToNodeId),
                    transition.Label,
                    transition.LoopPolicy == FlowIrLoopPolicy.Counted
                        ? transition.MaxTraversals
                        : null))
                .Concat(executableNodes
                    .Where(node => node.Kind == FlowIrNodeKind.Blockly && node.Actions.Length > 1)
                    .SelectMany(CreateBlocklyInternalTransitions))
                .ToArray();

            return Result.Success(new ExecutableRuntimeProcess(
                new RuntimeProcessDefinitionId(document.ProcessDefinitionId),
                new RuntimeProcessVersionId(document.ProcessVersionId),
                runtimeNodes)
            {
                StartNodeId = new RuntimeNodeId(document.StartNodeId),
                RoutingNodes = routingNodes,
                Transitions = transitions
            });
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or OverflowException)
        {
            return Result.Failure<ExecutableRuntimeProcess>(ApplicationError.Validation(
                "Processes.FlowIrRuntimeMappingInvalid",
                exception.Message));
        }
    }

    private static IEnumerable<ExecutableRuntimeNode> ToRuntimeNodes(FlowIrNode node)
    {
        if (node.Kind != FlowIrNodeKind.Blockly)
        {
            yield return ToRuntimeNode(node, node.Actions[0], node.NodeId, node.DisplayName);
            yield break;
        }

        for (var index = 0; index < node.Actions.Length; index += 1)
        {
            var action = node.Actions[index];
            yield return ToRuntimeNode(
                node,
                action,
                index == 0 ? node.NodeId : CreateBlocklyRuntimeNodeId(node.NodeId, index + 1),
                action.DisplayName);
        }
    }

    private static ExecutableRuntimeNode ToRuntimeNode(
        FlowIrNode node,
        FlowIrAction action,
        string runtimeNodeId,
        string displayName)
    {
        var timeout = TimeSpan.FromTicks(checked(
            action.Execution.TimeoutMilliseconds * TimeSpan.TicksPerMillisecond));
        var inputPayload = action.Kind == FlowIrActionKind.PythonScript
            ? JsonSerializer.Serialize(
                new RuntimeScriptCommandPayload(
                    action.PythonScript!.Language,
                    action.PythonScript.SourceCode,
                    action.PythonScript.Version,
                    action.InputPayload),
                JsonOptions)
            : action.InputPayload;

        return new ExecutableRuntimeNode(
            new RuntimeNodeId(runtimeNodeId),
            displayName,
            new RuntimeCapabilityId(action.RequiredCapability),
            action.CommandName,
            timeout,
            inputPayload,
            new RuntimeActionId(action.ActionId),
            action.DynamicChildren is null
                ? null
                : new ExecutableRuntimeDynamicActionSlot(
                    action.DynamicChildren.SlotId,
                    action.DynamicChildren.ChildActionIdPrefix,
                    action.DynamicChildren.SequenceBase,
                    action.DynamicChildren.SourceMappingMode.ToString()));
    }

    private static IEnumerable<ExecutableRuntimeTransition> CreateBlocklyInternalTransitions(
        FlowIrNode node)
    {
        for (var actionNumber = 1; actionNumber < node.Actions.Length; actionNumber += 1)
        {
            yield return new ExecutableRuntimeTransition(
                new RuntimeNodeId(actionNumber == 1
                    ? node.NodeId
                    : CreateBlocklyRuntimeNodeId(node.NodeId, actionNumber)),
                new RuntimeNodeId(CreateBlocklyRuntimeNodeId(node.NodeId, actionNumber + 1)),
                Label: null,
                MaxTraversals: null);
        }
    }

    private static string CreateBlocklyRuntimeNodeId(string blocklyNodeId, int actionNumber) =>
        $"{blocklyNodeId}:block-action:{actionNumber}";

    private static ExecutableRuntimeRoutingNode ToRoutingNode(FlowIrNode node)
    {
        var kind = node.Kind switch
        {
            FlowIrNodeKind.Start => ExecutableRuntimeRoutingNodeKind.Start,
            FlowIrNodeKind.Decision => ExecutableRuntimeRoutingNodeKind.Decision,
            FlowIrNodeKind.Delay => ExecutableRuntimeRoutingNodeKind.Delay,
            FlowIrNodeKind.End => ExecutableRuntimeRoutingNodeKind.End,
            _ => throw new InvalidOperationException(
                $"Flow IR node {node.NodeId} is executable, not a routing node.")
        };

        return new ExecutableRuntimeRoutingNode(
            new RuntimeNodeId(node.NodeId),
            node.DisplayName,
            kind);
    }
}
