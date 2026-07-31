using OpenLineOps.Processes.Application.Validation;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Validation;

namespace OpenLineOps.Processes.Application.Definitions;

public static class ProcessDefinitionMapper
{
    public static ProcessDefinitionDetails ToDetails(ProcessDefinition definition)
    {
        return new ProcessDefinitionDetails(
            definition.Id.Value,
            definition.VersionId.Value,
            definition.DisplayName,
            definition.Status.ToString(),
            definition.CreatedAtUtc,
            definition.PublishedAtUtc,
            definition.Nodes
                .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
                .Select(ToNodeDetails)
                .ToArray(),
            definition.Transitions
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .Select(transition => new ProcessTransitionDetails(
                    transition.Id.Value,
                    transition.FromNodeId.Value,
                    transition.ToNodeId.Value,
                    transition.Label,
                    transition.LoopPolicy.ToString(),
                    transition.MaxTraversals))
                .ToArray());
    }

    public static ProcessDefinitionSummary ToSummary(ProcessDefinition definition)
    {
        return new ProcessDefinitionSummary(
            definition.Id.Value,
            definition.VersionId.Value,
            definition.DisplayName,
            definition.Status.ToString(),
            definition.CreatedAtUtc,
            definition.PublishedAtUtc);
    }

    public static ProcessGraphValidationReportDetails ToValidationReport(
        ProcessGraphValidationReport report)
    {
        return new ProcessGraphValidationReportDetails(
            report.IsValid,
            report.Issues
                .Select(issue => new ProcessGraphValidationIssueDetails(
                    issue.Severity.ToString(),
                    issue.Code,
                    issue.Message,
                    issue.TargetKind.ToString(),
                    issue.TargetId))
                .ToArray());
    }

    private static ProcessNodeDetails ToNodeDetails(ProcessNode node)
    {
        return new ProcessNodeDetails(
            node.Id.Value,
            node.Kind.ToString(),
            node.DisplayName,
            node.RequiredCapability?.Value,
            node.CommandName,
            node.TargetKind?.ToString(),
            node.TargetId,
            node.CommandTimeout is null
                ? node.ScriptTimeout is null
                    ? null
                    : Convert.ToInt32(node.ScriptTimeout.Value.TotalSeconds)
                : Convert.ToInt32(node.CommandTimeout.Value.TotalSeconds),
            node.InputPayload,
            node.ScriptLanguage,
            node.BlocklyWorkspaceJson,
            node.ScriptSourceCode,
            node.ScriptSourceHash,
            node.ScriptVersion);
    }
}
