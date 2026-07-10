using OpenLineOps.Processes.Api.Models;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Validation;
using CreateApiDefinitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessDefinitionRequest;
using CreateApiNodeRequest = OpenLineOps.Processes.Api.Models.CreateProcessNodeRequest;
using CreateApiTransitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessTransitionRequest;
using CreateApplicationDefinitionRequest = OpenLineOps.Processes.Application.Definitions.CreateProcessDefinitionRequest;
using CreateApplicationNodeRequest = OpenLineOps.Processes.Application.Definitions.CreateProcessNodeRequest;
using CreateApplicationTransitionRequest = OpenLineOps.Processes.Application.Definitions.CreateProcessTransitionRequest;

namespace OpenLineOps.Processes.Api.Controllers;

internal static class ProcessDefinitionApiContractMapper
{
    public static CreateApplicationDefinitionRequest ToApplicationRequest(
        CreateApiDefinitionRequest request)
    {
        return new CreateApplicationDefinitionRequest(
            request.ProcessDefinitionId!,
            request.VersionId!,
            request.DisplayName!,
            request.Nodes!
                .Select(node => new CreateApplicationNodeRequest(
                    node.NodeId!,
                    node.Kind!,
                    node.DisplayName!,
                    node.RequiredCapability,
                    node.CommandName,
                    node.TimeoutSeconds,
                    node.InputPayload,
                    node.BlocklyWorkspaceJson,
                    node.ScriptSourceCode,
                    node.ScriptVersion))
                .ToArray(),
            request.Transitions!
                .Select(transition => new CreateApplicationTransitionRequest(
                    transition.TransitionId!,
                    transition.FromNodeId!,
                    transition.ToNodeId!,
                    transition.Label,
                    transition.LoopPolicy,
                    transition.MaxTraversals))
                .ToArray());
    }

    public static ProcessDefinitionResponse ToResponse(ProcessDefinitionDetails definition)
    {
        return new ProcessDefinitionResponse(
            definition.ProcessDefinitionId,
            definition.VersionId,
            definition.DisplayName,
            definition.Status,
            definition.CreatedAtUtc,
            definition.PublishedAtUtc,
            definition.Nodes.Select(ToNodeResponse).ToArray(),
            definition.Transitions.Select(ToTransitionResponse).ToArray());
    }

    public static ProcessDefinitionSummaryResponse ToSummaryResponse(ProcessDefinitionSummary summary)
    {
        return new ProcessDefinitionSummaryResponse(
            summary.ProcessDefinitionId,
            summary.VersionId,
            summary.DisplayName,
            summary.Status,
            summary.CreatedAtUtc,
            summary.PublishedAtUtc);
    }

    public static ProcessGraphValidationReportResponse ToResponse(
        ProcessGraphValidationReportDetails report)
    {
        return new ProcessGraphValidationReportResponse(
            report.IsValid,
            report.Issues
                .Select(issue => new ProcessGraphValidationIssueResponse(
                    issue.Severity,
                    issue.Code,
                    issue.Message))
                .ToArray());
    }

    public static Dictionary<string, string[]> Validate(CreateApiDefinitionRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.VersionId), request.VersionId);
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);

        if (request.Nodes is null)
        {
            errors[nameof(request.Nodes)] = ["Nodes collection is required."];
        }
        else
        {
            ValidateNodes(errors, request.Nodes);
        }

        if (request.Transitions is null)
        {
            errors[nameof(request.Transitions)] = ["Transitions collection is required."];
        }
        else
        {
            ValidateTransitions(errors, request.Transitions);
        }

        return errors;
    }

    private static ProcessNodeResponse ToNodeResponse(ProcessNodeDetails node)
    {
        return new ProcessNodeResponse(
            node.NodeId,
            node.Kind,
            node.DisplayName,
            node.RequiredCapability,
            node.CommandName,
            node.TimeoutSeconds,
            node.InputPayload,
            node.ScriptLanguage,
            node.BlocklyWorkspaceJson,
            node.ScriptSourceCode,
            node.ScriptSourceHash,
            node.ScriptVersion);
    }

    private static ProcessTransitionResponse ToTransitionResponse(ProcessTransitionDetails transition)
    {
        return new ProcessTransitionResponse(
            transition.TransitionId,
            transition.FromNodeId,
            transition.ToNodeId,
            transition.Label,
            transition.LoopPolicy,
            transition.MaxTraversals);
    }

    private static void ValidateNodes(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<CreateApiNodeRequest> nodes)
    {
        var index = 0;
        foreach (var node in nodes)
        {
            var prefix = $"Nodes[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(node.NodeId)}", node.NodeId);
            AddRequired(errors, $"{prefix}.{nameof(node.Kind)}", node.Kind);
            AddRequired(errors, $"{prefix}.{nameof(node.DisplayName)}", node.DisplayName);
            if (node.UnknownProperties is { Count: > 0 })
            {
                errors[$"{prefix}.UnknownProperties"] =
                [
                    $"Unknown properties are not allowed: {string.Join(", ", node.UnknownProperties.Keys.Order(StringComparer.Ordinal))}."
                ];
            }

            index++;
        }
    }

    private static void ValidateTransitions(
        Dictionary<string, string[]> errors,
        IReadOnlyCollection<CreateApiTransitionRequest> transitions)
    {
        var index = 0;
        foreach (var transition in transitions)
        {
            var prefix = $"Transitions[{index}]";
            AddRequired(errors, $"{prefix}.{nameof(transition.TransitionId)}", transition.TransitionId);
            AddRequired(errors, $"{prefix}.{nameof(transition.FromNodeId)}", transition.FromNodeId);
            AddRequired(errors, $"{prefix}.{nameof(transition.ToNodeId)}", transition.ToNodeId);
            index++;
        }
    }

    private static void AddRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }
}
