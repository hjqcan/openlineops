using System.Collections.Immutable;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Domain.Validation;
using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Processes.Application.FlowIr;

public sealed class ProcessFlowIrCompiler : IProcessFlowIrCompiler
{
    public Result<FlowIrCompilation> Compile(
        ProcessDefinition definition,
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>? blockCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!definition.IsPublished)
        {
            return Result.Failure<FlowIrCompilation>(ApplicationError.Conflict(
                "Processes.FlowIrDefinitionNotPublished",
                $"Process definition {definition.Id} must be published before it can be compiled to Flow IR."));
        }

        var graphReport = ProcessGraphValidator.Validate(definition);
        if (!graphReport.IsValid)
        {
            var issueSummary = string.Join(
                "; ",
                graphReport.Issues
                    .OrderBy(issue => issue.Code, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                    .Select(issue => $"{issue.Code}: {issue.Message}"));

            return Result.Failure<FlowIrCompilation>(ApplicationError.Validation(
                "Processes.FlowIrGraphInvalid",
                $"Published process definition {definition.Id} cannot be compiled because graph validation failed: {issueSummary}"));
        }

        var branchingError = ValidateBranching(definition);
        if (branchingError is not null)
        {
            return Result.Failure<FlowIrCompilation>(branchingError);
        }

        var timeoutError = ValidateTimeouts(definition);
        if (timeoutError is not null)
        {
            return Result.Failure<FlowIrCompilation>(timeoutError);
        }

        var executableNodeCount = definition.Nodes.Count(node =>
            node.Kind is ProcessNodeKind.Command or ProcessNodeKind.PythonScript or ProcessNodeKind.Blockly);
        if (executableNodeCount == 0)
        {
            return Result.Failure<FlowIrCompilation>(ApplicationError.Validation(
                "Processes.FlowIrNoExecutableActions",
                $"Process definition {definition.Id} does not contain an executable action."));
        }

        var startNode = definition.Nodes.Single(node => node.Kind == ProcessNodeKind.Start);
        var diagnostics = ImmutableArray.CreateBuilder<FlowIrCompilationDiagnostic>();
        var nodes = ImmutableArray.CreateBuilder<FlowIrNode>(definition.Nodes.Count);
        var blockDependencies = new Dictionary<string, FlowIrBlockDependency>(StringComparer.Ordinal);
        var effectiveCatalog = blockCatalog?.ToArray()
            ?? ProcessBlocklyBlockCatalog.BuiltInBlocks().ToArray();
        foreach (var node in definition.Nodes.OrderBy(node => node.Id.Value, StringComparer.Ordinal))
        {
            if (node.Kind == ProcessNodeKind.Blockly)
            {
                var workspaceResult = BlocklyWorkspaceActionCompiler.Compile(
                    definition,
                    node,
                    effectiveCatalog);
                if (workspaceResult.IsFailure)
                {
                    return Result.Failure<FlowIrCompilation>(workspaceResult.Error);
                }

                nodes.Add(ToBlocklyNode(definition, node, workspaceResult.Value));
                foreach (var dependency in workspaceResult.Value.Dependencies)
                {
                    if (blockDependencies.TryGetValue(dependency.BlockType, out var existing)
                        && existing != dependency)
                    {
                        return Result.Failure<FlowIrCompilation>(ApplicationError.Conflict(
                            "Processes.FlowIrBlocklyDependencyConflict",
                            $"Blockly block {dependency.BlockType} resolved to more than one version or contract hash."));
                    }

                    blockDependencies[dependency.BlockType] = dependency;
                }

                continue;
            }

            nodes.Add(ToNode(definition, node));
        }

        var transitions = definition.Transitions
            .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
            .Select(transition => ToTransition(definition, transition))
            .ToImmutableArray();

        var document = new FlowIrDocument(
            FlowIrSchemaVersions.V1,
            definition.Id.Value,
            definition.VersionId.Value,
            definition.DisplayName,
            startNode.Id.Value,
            nodes.ToImmutable(),
            transitions,
            blockDependencies.Values
                .OrderBy(dependency => dependency.BlockType, StringComparer.Ordinal)
                .ToImmutableArray());

        return Result.Success(new FlowIrCompilation(document, diagnostics.ToImmutable()));
    }

    private static FlowIrNode ToBlocklyNode(
        ProcessDefinition definition,
        ProcessNode node,
        CompiledBlocklyWorkspace workspace)
    {
        return new FlowIrNode(
            node.Id.Value,
            FlowIrNodeKind.Blockly,
            node.DisplayName,
            workspace.Actions,
            new FlowIrSourceTrace(
                definition.Id.Value,
                definition.VersionId.Value,
                FlowIrSourceElementKind.ProcessNode,
                node.Id.Value,
                workspace.WorkspaceSha256));
    }

    private static FlowIrNode ToNode(
        ProcessDefinition definition,
        ProcessNode node)
    {
        var source = NodeSource(definition, node);
        var actions = node.Kind switch
        {
            ProcessNodeKind.Command => ImmutableArray.Create(ToDeviceCommandAction(node, source)),
            ProcessNodeKind.PythonScript => ImmutableArray.Create(ToPythonScriptAction(node, source)),
            _ => ImmutableArray<FlowIrAction>.Empty
        };

        return new FlowIrNode(
            node.Id.Value,
            ToNodeKind(node),
            node.DisplayName,
            actions,
            source);
    }

    private static FlowIrAction ToDeviceCommandAction(
        ProcessNode node,
        FlowIrSourceTrace source)
    {
        var capability = node.RequiredCapability!.Value;

        return new FlowIrAction(
            CreateActionId(node),
            FlowIrActionKind.DeviceCommand,
            node.DisplayName,
            capability,
            node.CommandName!,
            new FlowIrTargetReference(
                ToFlowIrTargetKind(node.TargetKind!.Value),
                node.TargetId!),
            node.InputPayload,
            CreateExecutionPolicy(node.CommandTimeout!.Value),
            PythonScript: null,
            source);
    }

    private static FlowIrAction ToPythonScriptAction(
        ProcessNode node,
        FlowIrSourceTrace source)
    {
        return new FlowIrAction(
            CreateActionId(node),
            FlowIrActionKind.PythonScript,
            node.DisplayName,
            RuntimeScriptCommand.PythonCapability,
            RuntimeScriptCommand.PythonCommandName,
            new FlowIrTargetReference(
                FlowIrTargetReferenceKind.Capability,
                RuntimeScriptCommand.PythonCapability),
            node.InputPayload,
            CreateExecutionPolicy(node.ScriptTimeout!.Value),
            new FlowIrPythonScript(
                node.ScriptLanguage!,
                node.ScriptSourceCode!,
                node.ScriptSourceHash!,
                node.ScriptVersion!),
            source);
    }

    private static FlowIrExecutionPolicy CreateExecutionPolicy(TimeSpan timeout)
    {
        return new FlowIrExecutionPolicy(
            checked(timeout.Ticks / TimeSpan.TicksPerMillisecond),
            RetryLimit: 0,
            FlowIrCancellationMode.Cooperative);
    }

    private static ApplicationError? ValidateTimeouts(ProcessDefinition definition)
    {
        foreach (var node in definition.Nodes
                     .Where(node => node.Kind is ProcessNodeKind.Command
                         or ProcessNodeKind.PythonScript
                         or ProcessNodeKind.Blockly)
                     .OrderBy(node => node.Id.Value, StringComparer.Ordinal))
        {
            var timeout = node.Kind is ProcessNodeKind.Command or ProcessNodeKind.Blockly
                ? node.CommandTimeout!.Value
                : node.ScriptTimeout!.Value;
            if (timeout <= TimeSpan.Zero)
            {
                return ApplicationError.Validation(
                    "Processes.FlowIrTimeoutInvalid",
                    $"Process node {node.Id} must declare a positive timeout before Flow IR compilation.");
            }

            if (timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
            {
                return ApplicationError.Validation(
                    "Processes.FlowIrTimeoutPrecisionUnsupported",
                    $"Process node {node.Id} timeout must be representable as a whole number of milliseconds in Flow IR v1.");
            }

            var timeoutMilliseconds = checked(timeout.Ticks / TimeSpan.TicksPerMillisecond);
            if (timeoutMilliseconds <= 0)
            {
                return ApplicationError.Validation(
                    "Processes.FlowIrTimeoutInvalid",
                    $"Process node {node.Id} timeout must be at least one millisecond in Flow IR v1.");
            }
        }

        return null;
    }

    private static FlowIrTransition ToTransition(
        ProcessDefinition definition,
        ProcessTransition transition)
    {
        return new FlowIrTransition(
            transition.Id.Value,
            transition.FromNodeId.Value,
            transition.ToNodeId.Value,
            transition.Label,
            transition.LoopPolicy == ProcessTransitionLoopPolicy.Counted
                ? FlowIrLoopPolicy.Counted
                : FlowIrLoopPolicy.None,
            transition.MaxTraversals,
            new FlowIrSourceTrace(
                definition.Id.Value,
                definition.VersionId.Value,
                FlowIrSourceElementKind.ProcessTransition,
                transition.Id.Value,
                ContentHash: null));
    }

    private static FlowIrSourceTrace NodeSource(
        ProcessDefinition definition,
        ProcessNode node)
    {
        return new FlowIrSourceTrace(
            definition.Id.Value,
            definition.VersionId.Value,
            FlowIrSourceElementKind.ProcessNode,
            node.Id.Value,
            node.ScriptSourceHash);
    }

    private static string CreateActionId(ProcessNode node)
    {
        return $"{node.Id.Value}:action:1";
    }

    private static FlowIrNodeKind ToNodeKind(ProcessNode node)
    {
        return node.Kind switch
        {
            ProcessNodeKind.Start => FlowIrNodeKind.Start,
            ProcessNodeKind.Command => FlowIrNodeKind.Command,
            ProcessNodeKind.Decision => FlowIrNodeKind.Decision,
            ProcessNodeKind.Delay => FlowIrNodeKind.Delay,
            ProcessNodeKind.End => FlowIrNodeKind.End,
            ProcessNodeKind.PythonScript => FlowIrNodeKind.PythonScript,
            ProcessNodeKind.Blockly => FlowIrNodeKind.Blockly,
            _ => throw new InvalidOperationException($"Unsupported process node kind {node.Kind}.")
        };
    }

    private static FlowIrTargetReferenceKind ToFlowIrTargetKind(ProcessActionTargetKind kind)
    {
        return kind switch
        {
            ProcessActionTargetKind.System => FlowIrTargetReferenceKind.System,
            ProcessActionTargetKind.SlotGroup => FlowIrTargetReferenceKind.SlotGroup,
            ProcessActionTargetKind.Slot => FlowIrTargetReferenceKind.Slot,
            ProcessActionTargetKind.Dut => FlowIrTargetReferenceKind.Dut,
            ProcessActionTargetKind.Capability => FlowIrTargetReferenceKind.Capability,
            ProcessActionTargetKind.Driver => FlowIrTargetReferenceKind.Driver,
            _ => throw new InvalidOperationException($"Unsupported command target kind {kind}.")
        };
    }

    private static ApplicationError? ValidateBranching(ProcessDefinition definition)
    {
        var nodesById = definition.Nodes.ToDictionary(node => node.Id);
        foreach (var transitionGroup in definition.Transitions
                     .GroupBy(transition => transition.FromNodeId)
                     .OrderBy(group => group.Key.Value, StringComparer.Ordinal))
        {
            var outgoingTransitions = transitionGroup
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .ToArray();
            if (outgoingTransitions.Length <= 1)
            {
                continue;
            }

            if (!nodesById.TryGetValue(transitionGroup.Key, out var sourceNode)
                || sourceNode.Kind != ProcessNodeKind.Decision)
            {
                return ApplicationError.Conflict(
                    "Processes.FlowIrBranchingRequiresDecision",
                    $"Process node {transitionGroup.Key} has multiple outgoing transitions; Flow IR branching must originate from a Decision node.");
            }

            var duplicateLabel = outgoingTransitions
                .Select(transition => string.IsNullOrWhiteSpace(transition.Label)
                    ? "default"
                    : transition.Label.Trim())
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)
                ?.Key;
            if (duplicateLabel is not null)
            {
                return ApplicationError.Conflict(
                    "Processes.FlowIrDecisionLabelDuplicate",
                    $"Decision node {transitionGroup.Key} has duplicate outgoing transition label '{duplicateLabel}'.");
            }
        }

        return null;
    }
}
