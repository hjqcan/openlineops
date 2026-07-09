using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Application.Validation;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Domain.Validation;

namespace OpenLineOps.Processes.Application.Definitions;

public sealed class ProcessDefinitionService : IProcessDefinitionService
{
    private readonly IProcessDefinitionRepository _repository;
    private readonly IClock _clock;
    private readonly IProcessScriptDefinitionValidator _scriptValidator;

    public ProcessDefinitionService(
        IProcessDefinitionRepository repository,
        IClock clock,
        IProcessScriptDefinitionValidator scriptValidator)
    {
        _repository = repository;
        _clock = clock;
        _scriptValidator = scriptValidator;
    }

    public async Task<Result<ProcessDefinitionDetails>> CreateAsync(
        CreateProcessDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestValidation = ValidateCreateRequest(request);
        if (requestValidation is not null)
        {
            return Result.Failure<ProcessDefinitionDetails>(requestValidation);
        }

        try
        {
            var definitionId = new ProcessDefinitionId(request.ProcessDefinitionId);

            var existing = await _repository
                .GetByIdAsync(definitionId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Result.Failure<ProcessDefinitionDetails>(ApplicationError.Conflict(
                    "Processes.DefinitionAlreadyExists",
                    $"Process definition {definitionId} already exists."));
            }

            var definition = ProcessDefinition.Create(
                definitionId,
                new ProcessVersionId(request.VersionId),
                request.DisplayName,
                _clock.UtcNow);

            foreach (var nodeRequest in request.Nodes!)
            {
                var nodeResult = AddNode(definition, nodeRequest);
                if (nodeResult is not null)
                {
                    return Result.Failure<ProcessDefinitionDetails>(nodeResult);
                }
            }

            foreach (var transitionRequest in request.Transitions!)
            {
                var transitionResult = AddTransition(definition, transitionRequest);
                if (transitionResult is not null)
                {
                    return Result.Failure<ProcessDefinitionDetails>(transitionResult);
                }
            }

            await _repository
                .SaveAsync(definition, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success(ProcessDefinitionMapper.ToDetails(definition));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProcessDefinitionDetails>(ApplicationError.Validation(
                "Processes.InvalidDefinitionInput",
                exception.Message));
        }
    }

    public async Task<Result<ProcessDefinitionDetails>> GetByIdAsync(
        string processDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await FindByIdAsync(processDefinitionId, cancellationToken).ConfigureAwait(false);

        return definition is null
            ? Result.Failure<ProcessDefinitionDetails>(NotFound(processDefinitionId))
            : Result.Success(ProcessDefinitionMapper.ToDetails(definition));
    }

    public async Task<Result<IReadOnlyCollection<ProcessDefinitionSummary>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await _repository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var summaries = definitions
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .Select(ProcessDefinitionMapper.ToSummary)
            .ToArray();

        return Result.Success<IReadOnlyCollection<ProcessDefinitionSummary>>(summaries);
    }

    public async Task<Result<ProcessGraphValidationReportDetails>> ValidateAsync(
        string processDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await FindByIdAsync(processDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return Result.Failure<ProcessGraphValidationReportDetails>(NotFound(processDefinitionId));
        }

        var report = ProcessGraphValidator.Validate(definition);

        return Result.Success(ProcessDefinitionMapper.ToValidationReport(report));
    }

    public async Task<Result<ProcessDefinitionDetails>> PublishAsync(
        string processDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await FindByIdAsync(processDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return Result.Failure<ProcessDefinitionDetails>(NotFound(processDefinitionId));
        }

        if (!definition.IsPublished)
        {
            var graphReport = ProcessGraphValidator.Validate(definition);
            if (!graphReport.IsValid)
            {
                return Result.Failure<ProcessDefinitionDetails>(ApplicationError.Validation(
                    "Processes.PublishValidationFailed",
                    $"Process definition {definition.Id} cannot be published because graph validation failed."));
            }

            var scriptValidationError = await ValidatePythonScriptsAsync(definition, cancellationToken)
                .ConfigureAwait(false);
            if (scriptValidationError is not null)
            {
                return Result.Failure<ProcessDefinitionDetails>(scriptValidationError);
            }
        }

        var publishResult = definition.Publish(_clock.UtcNow);
        if (!publishResult.Succeeded)
        {
            return Result.Failure<ProcessDefinitionDetails>(ApplicationError.Validation(
                publishResult.Code,
                publishResult.Message));
        }

        await _repository
            .SaveAsync(definition, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(ProcessDefinitionMapper.ToDetails(definition));
    }

    private async ValueTask<ApplicationError?> ValidatePythonScriptsAsync(
        ProcessDefinition definition,
        CancellationToken cancellationToken)
    {
        foreach (var node in definition.Nodes.Where(node => node.IsPythonScript))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var report = await _scriptValidator
                .ValidateAsync(node, cancellationToken)
                .ConfigureAwait(false);
            if (report.IsValid)
            {
                continue;
            }

            var issueSummary = string.Join(
                "; ",
                report.Issues.Select(issue =>
                    issue.Line > 0
                        ? $"{issue.Code} at {issue.Line}:{issue.Column}: {issue.Message}"
                        : $"{issue.Code}: {issue.Message}"));

            return ApplicationError.Validation(
                "Processes.PythonScriptValidationFailed",
                $"Python script node {node.Id} failed validation: {issueSummary}");
        }

        return null;
    }

    private async Task<ProcessDefinition?> FindByIdAsync(
        string processDefinitionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return null;
        }

        return await _repository
            .GetByIdAsync(new ProcessDefinitionId(processDefinitionId), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ApplicationError? AddNode(
        ProcessDefinition definition,
        CreateProcessNodeRequest request)
    {
        if (!Enum.TryParse<ProcessNodeKind>(request.Kind, ignoreCase: true, out var nodeKind))
        {
            return ApplicationError.Validation(
                "Processes.InvalidNodeKind",
                $"Process node {request.NodeId} has unsupported kind {request.Kind}.");
        }

        var nodeId = new ProcessNodeId(request.NodeId);
        var scriptEditorMode = ParseScriptEditorMode(request);
        if (scriptEditorMode.IsFailure)
        {
            return scriptEditorMode.Error;
        }

        ProcessNode node = nodeKind switch
        {
            ProcessNodeKind.Start => ProcessNode.Start(nodeId, request.DisplayName),
            ProcessNodeKind.Command => ProcessNode.Command(
                nodeId,
                request.DisplayName,
                string.IsNullOrWhiteSpace(request.RequiredCapability)
                    ? null
                    : new ProcessCapabilityId(request.RequiredCapability),
                request.CommandName,
                request.TimeoutSeconds is null
                    ? null
                    : TimeSpan.FromSeconds(request.TimeoutSeconds.Value),
                request.InputPayload),
            ProcessNodeKind.PythonScript => ProcessNode.PythonScript(
                nodeId,
                request.DisplayName,
                scriptEditorMode.Value,
                request.BlocklyWorkspaceJson,
                request.ScriptSourceCode,
                request.ScriptVersion,
                request.TimeoutSeconds is null
                    ? null
                    : TimeSpan.FromSeconds(request.TimeoutSeconds.Value),
                request.InputPayload),
            ProcessNodeKind.Decision => ProcessNode.Decision(nodeId, request.DisplayName),
            ProcessNodeKind.Delay => ProcessNode.Delay(nodeId, request.DisplayName),
            ProcessNodeKind.End => ProcessNode.End(nodeId, request.DisplayName),
            _ => throw new InvalidOperationException($"Unsupported process node kind {nodeKind}.")
        };

        var addResult = definition.AddNode(node);

        return addResult.Succeeded
            ? null
            : ApplicationError.Validation(addResult.Code, addResult.Message);
    }

    private static Result<ProcessScriptEditorMode?> ParseScriptEditorMode(
        CreateProcessNodeRequest request)
    {
        if (!Enum.TryParse<ProcessNodeKind>(request.Kind, ignoreCase: true, out var nodeKind)
            || nodeKind != ProcessNodeKind.PythonScript)
        {
            return Result.Success<ProcessScriptEditorMode?>(null);
        }

        if (string.IsNullOrWhiteSpace(request.ScriptEditorMode))
        {
            return Result.Success<ProcessScriptEditorMode?>(ProcessScriptEditorMode.Blockly);
        }

        return Enum.TryParse<ProcessScriptEditorMode>(
            request.ScriptEditorMode,
            ignoreCase: true,
            out var editorMode)
            ? Result.Success<ProcessScriptEditorMode?>(editorMode)
            : Result.Failure<ProcessScriptEditorMode?>(ApplicationError.Validation(
                "Processes.InvalidScriptEditorMode",
                $"Process node {request.NodeId} has unsupported script editor mode {request.ScriptEditorMode}."));
    }

    private static ApplicationError? AddTransition(
        ProcessDefinition definition,
        CreateProcessTransitionRequest request)
    {
        var loopPolicy = ParseLoopPolicy(request);
        if (loopPolicy.IsFailure)
        {
            return loopPolicy.Error;
        }

        var addResult = definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId(request.TransitionId),
            new ProcessNodeId(request.FromNodeId),
            new ProcessNodeId(request.ToNodeId),
            request.Label,
            loopPolicy.Value,
            request.MaxTraversals));

        return addResult.Succeeded
            ? null
            : ApplicationError.Validation(addResult.Code, addResult.Message);
    }

    private static Result<ProcessTransitionLoopPolicy> ParseLoopPolicy(
        CreateProcessTransitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LoopPolicy))
        {
            return Result.Success(ProcessTransitionLoopPolicy.None);
        }

        return Enum.TryParse<ProcessTransitionLoopPolicy>(
            request.LoopPolicy,
            ignoreCase: true,
            out var loopPolicy)
            ? Result.Success(loopPolicy)
            : Result.Failure<ProcessTransitionLoopPolicy>(ApplicationError.Validation(
                "Processes.InvalidTransitionLoopPolicy",
                $"Process transition {request.TransitionId} has unsupported loop policy {request.LoopPolicy}."));
    }

    private static ApplicationError? ValidateCreateRequest(CreateProcessDefinitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId))
        {
            return ApplicationError.Validation(
                "Processes.ProcessDefinitionIdRequired",
                "ProcessDefinitionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.VersionId))
        {
            return ApplicationError.Validation(
                "Processes.VersionIdRequired",
                "VersionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ApplicationError.Validation(
                "Processes.DisplayNameRequired",
                "DisplayName is required.");
        }

        if (request.Nodes is null)
        {
            return ApplicationError.Validation(
                "Processes.NodesRequired",
                "Nodes collection is required.");
        }

        if (request.Transitions is null)
        {
            return ApplicationError.Validation(
                "Processes.TransitionsRequired",
                "Transitions collection is required.");
        }

        return null;
    }

    private static ApplicationError NotFound(string processDefinitionId)
    {
        return ApplicationError.NotFound(
            "Processes.DefinitionNotFound",
            $"Process definition {processDefinitionId} was not found.");
    }
}
