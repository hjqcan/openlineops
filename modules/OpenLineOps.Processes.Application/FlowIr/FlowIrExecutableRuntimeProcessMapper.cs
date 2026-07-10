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
            var runtimeNodes = document.Nodes
                .Where(node => node.Kind is FlowIrNodeKind.Command or FlowIrNodeKind.PythonScript)
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .Select(ToRuntimeNode)
                .ToArray();
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
                    new RuntimeNodeId(transition.FromNodeId),
                    new RuntimeNodeId(transition.ToNodeId),
                    transition.Label,
                    transition.LoopPolicy == FlowIrLoopPolicy.Counted
                        ? transition.MaxTraversals
                        : null))
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

    private static ExecutableRuntimeNode ToRuntimeNode(FlowIrNode node)
    {
        var action = node.Actions[0];
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
            new RuntimeNodeId(node.NodeId),
            node.DisplayName,
            new RuntimeCapabilityId(action.Target.Reference),
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
