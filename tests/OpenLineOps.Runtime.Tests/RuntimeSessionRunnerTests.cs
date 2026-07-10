using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeSessionRunnerTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsyncWithSuccessfulFakeProcessCompletesSession()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed("scan-ok"),
            RuntimeCommandExecutionResult.Completed("measure-ok"));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);
        var request = CreateStartRequest(CreateTwoNodeProcess());

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(request.SessionId, result.Value.SessionId);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        Assert.Equal(2, result.Value.CompletedSteps);
        Assert.Equal(2, result.Value.CommandCount);
        Assert.Equal(0, result.Value.IncidentCount);
        Assert.Equal(2, commandExecutor.Contexts.Count);
        Assert.Equal("station-a", commandExecutor.Contexts[0].StationId.Value);
        Assert.Equal("snapshot-20260629-001", commandExecutor.Contexts[0].ConfigurationSnapshotId.Value);
        Assert.Equal("node-scan", commandExecutor.Contexts[0].NodeId.Value);
        Assert.Equal("node-measure", commandExecutor.Contexts[1].NodeId.Value);
        Assert.Equal(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            commandExecutor.Contexts[0].ProductionRunId.Value);
        Assert.Equal("line.main", commandExecutor.Contexts[0].ProductionLineDefinitionId);
        Assert.Equal("stage.main", commandExecutor.Contexts[0].ProductionStageId);
        Assert.Equal(1, commandExecutor.Contexts[0].StageSequence);
        Assert.Equal("workstation.main", commandExecutor.Contexts[0].WorkstationId);
        Assert.Equal("dut.default", commandExecutor.Contexts[0].DutIdentity.ModelId);
        Assert.Equal("serialNumber", commandExecutor.Contexts[0].DutIdentity.InputKey);
        Assert.Equal("DUT-DEFAULT", commandExecutor.Contexts[0].DutIdentity.Value);

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);
        Assert.NotNull(persisted);
        Assert.Equal(RuntimeSessionStatus.Completed, persisted.Status);
        Assert.All(persisted.Steps, step => Assert.Equal(RuntimeStepStatus.Completed, step.Status));
        Assert.All(persisted.Commands, command => Assert.Equal(RuntimeCommandStatus.Completed, command.Status));

        var eventNames = eventPublisher.Events.Select(domainEvent => domainEvent.EventName).ToArray();
        Assert.Equal("RuntimeSession.Created", eventNames[0]);
        Assert.Contains("RuntimeCommand.StatusChanged", eventNames);
        Assert.IsType<RuntimeSessionStatusChangedDomainEvent>(eventPublisher.Events.Last());
    }

    [Fact]
    public async Task RunAsyncWithFailedCommandFailsSessionAndStopsProcess()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Failed("scanner returned NG"),
            RuntimeCommandExecutionResult.Completed("should-not-run"));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateTwoNodeProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        Assert.Equal(0, result.Value.CompletedSteps);
        Assert.Equal(1, result.Value.CommandCount);
        Assert.Equal(1, result.Value.IncidentCount);
        Assert.Single(commandExecutor.Contexts);

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);
        Assert.NotNull(persisted);
        Assert.Equal(RuntimeSessionStatus.Failed, persisted.Status);
        Assert.Equal("Runtime.CommandFailed", Assert.Single(persisted.Incidents).Code);
        Assert.Equal(RuntimeStepStatus.Failed, Assert.Single(persisted.Steps).Status);
        Assert.Equal(RuntimeCommandStatus.Failed, Assert.Single(persisted.Commands).Status);
    }

    [Fact]
    public async Task SemanticFailedResultPersistsTypedJudgementAndVendorEvidence()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.SemanticFailed(
                "External adapter reported failure.",
                "{\"judgement\":\"Failed\"}"));
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateTwoNodeProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        var command = Assert.Single(persisted.Commands);
        Assert.Equal(RuntimeCommandStatus.Failed, command.Status);
        Assert.Equal(RuntimeCommandSemanticOutcome.Failed, command.SemanticOutcome);
        Assert.Equal("{\"judgement\":\"Failed\"}", command.ResultPayload);
    }

    [Fact]
    public async Task SemanticAbortedResultCancelsSessionAndPersistsVendorEvidence()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.SemanticAborted(
                "External adapter reported abort.",
                "{\"judgement\":\"Aborted\"}"));
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateTwoNodeProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Canceled, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        var command = Assert.Single(persisted.Commands);
        Assert.Equal(RuntimeCommandStatus.Canceled, command.Status);
        Assert.Equal(RuntimeCommandSemanticOutcome.Aborted, command.SemanticOutcome);
        Assert.Equal("{\"judgement\":\"Aborted\"}", command.ResultPayload);
    }

    [Fact]
    public async Task RunAsyncWithDecisionNodeRoutesToMatchingBranch()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed("""{"status":"ok"}"""),
            RuntimeCommandExecutionResult.Completed("pass-completed"),
            RuntimeCommandExecutionResult.Completed("should-not-run"));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateBranchingProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        Assert.Equal(2, result.Value.CompletedSteps);
        Assert.Equal(2, result.Value.CommandCount);
        Assert.Equal(0, result.Value.IncidentCount);
        Assert.Equal(2, commandExecutor.Contexts.Count);
        Assert.Equal("node-scan", commandExecutor.Contexts[0].NodeId.Value);
        Assert.Equal("node-pass", commandExecutor.Contexts[1].NodeId.Value);
        Assert.DoesNotContain(commandExecutor.Contexts, context => context.NodeId.Value == "node-fail");

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);

        Assert.NotNull(persisted);
        Assert.Equal(RuntimeSessionStatus.Completed, persisted.Status);
        Assert.Equal(2, persisted.Steps.Count);
        Assert.Empty(persisted.Incidents);
    }

    [Fact]
    public async Task RunAsyncWithDecisionNodeFailsSessionWhenNoBranchMatches()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed("""{"status":"unknown"}"""),
            RuntimeCommandExecutionResult.Completed("should-not-run"));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateBranchingProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        Assert.Equal(1, result.Value.CompletedSteps);
        Assert.Equal(1, result.Value.CommandCount);
        Assert.Equal(1, result.Value.IncidentCount);
        Assert.Single(commandExecutor.Contexts);
        Assert.Equal("node-scan", commandExecutor.Contexts[0].NodeId.Value);

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);

        Assert.NotNull(persisted);
        Assert.Equal(RuntimeSessionStatus.Failed, persisted.Status);
        Assert.Equal("Runtime.DecisionBranchNotMatched", Assert.Single(persisted.Incidents).Code);
    }

    [Theory]
    [InlineData("{\"status\":\"OK\"}")]
    [InlineData("{\"branch\":\"ok\"}")]
    [InlineData(" ok ")]
    public async Task DecisionRoutingRejectsCaseAliasesAndPaddedBranchValues(string payload)
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed(payload));
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateBranchingProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        Assert.Equal("Runtime.DecisionBranchNotMatched", Assert.Single(persisted.Incidents).Code);
        Assert.Single(commandExecutor.Contexts);
    }

    [Fact]
    public async Task RunAsyncWithLoopTransitionRetriesUntilDecisionRoutesOut()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed("""{"status":"retry"}"""),
            RuntimeCommandExecutionResult.Completed("""{"status":"ok"}"""));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateLoopingProcess(maxLoopTraversals: 2)));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        Assert.Equal(2, result.Value.CompletedSteps);
        Assert.Equal(2, result.Value.CommandCount);
        Assert.Equal(0, result.Value.IncidentCount);
        Assert.Equal(2, commandExecutor.Contexts.Count);
        Assert.All(commandExecutor.Contexts, context => Assert.Equal("node-scan", context.NodeId.Value));

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);

        Assert.NotNull(persisted);
        Assert.Equal(RuntimeSessionStatus.Completed, persisted.Status);
        Assert.Empty(persisted.Incidents);
    }

    [Fact]
    public async Task RunAsyncWithLoopTransitionFailsWhenMaxTraversalsExceeded()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed("""{"status":"retry"}"""),
            RuntimeCommandExecutionResult.Completed("""{"status":"retry"}"""));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateLoopingProcess(maxLoopTraversals: 1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        Assert.Equal(2, result.Value.CompletedSteps);
        Assert.Equal(2, result.Value.CommandCount);
        Assert.Equal(1, result.Value.IncidentCount);
        Assert.Equal(2, commandExecutor.Contexts.Count);

        var persisted = await repository.GetByIdAsync(result.Value.SessionId);

        Assert.NotNull(persisted);
        Assert.Equal(RuntimeSessionStatus.Failed, persisted.Status);
        Assert.Equal("Runtime.LoopTransitionLimitExceeded", Assert.Single(persisted.Incidents).Code);
    }

    [Fact]
    public async Task RunAsyncWithEmptyProcessReturnsValidationFailure()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor();
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var emptyProcess = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-empty"),
            new ProcessVersionId("process-empty@1.0.0"),
            []);

        var result = await runner.RunAsync(CreateStartRequest(emptyProcess));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Runtime.ProcessHasNoNodes", result.Error.Code);
        Assert.Equal(0, repository.SaveCount);
        Assert.Empty(eventPublisher.Events);
        Assert.Empty(commandExecutor.Contexts);
    }

    [Fact]
    public async Task CommandCompletionAndSessionTerminalStatePersistWhenCancellationRacesAfterExecution()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner(
            repository,
            eventPublisher,
            new CancelingCommandExecutor(cancellation));
        var request = CreateStartRequest(new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-cancellation-race"),
            new ProcessVersionId("process-cancellation-race@1.0.0"),
            [
                Node(
                    new RuntimeNodeId("node-cancellation-race"),
                    "Cancellation race",
                    new RuntimeCapabilityId("capability.cancellation-race"),
                    "execute",
                    TimeSpan.FromSeconds(1))
            ]));

        var result = await runner.RunAsync(request, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(request.SessionId));
        Assert.Equal(RuntimeSessionStatus.Completed, persisted.Status);
        Assert.Equal(RuntimeCommandStatus.Completed, Assert.Single(persisted.Commands).Status);
        Assert.Equal(RuntimeStepStatus.Completed, Assert.Single(persisted.Steps).Status);
    }

    [Fact]
    public async Task CommandExecutorExceptionBecomesPersistedFailedCommandStepAndSession()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            new ThrowingCommandExecutor());
        var request = CreateStartRequest(new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-executor-fault"),
            new ProcessVersionId("process-executor-fault@1.0.0"),
            [
                Node(
                    new RuntimeNodeId("node-executor-fault"),
                    "Executor fault",
                    new RuntimeCapabilityId("capability.executor-fault"),
                    "execute",
                    TimeSpan.FromSeconds(1))
            ]));

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(request.SessionId));
        Assert.Equal(RuntimeSessionStatus.Failed, persisted.Status);
        Assert.Equal(RuntimeCommandStatus.Failed, Assert.Single(persisted.Commands).Status);
        Assert.Equal(RuntimeStepStatus.Failed, Assert.Single(persisted.Steps).Status);
        Assert.Contains(
            typeof(InvalidOperationException).FullName!,
            Assert.Single(persisted.Incidents).Message,
            StringComparison.Ordinal);
    }

    private static ExecutableRuntimeNode Node(
        RuntimeNodeId nodeId,
        string displayName,
        RuntimeCapabilityId capability,
        string commandName,
        TimeSpan timeout,
        string? inputPayload = null)
    {
        return new ExecutableRuntimeNode(
            nodeId,
            displayName,
            capability,
            commandName,
            timeout,
            inputPayload,
            new RuntimeActionId($"{nodeId.Value}:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.Capability, capability.Value));
    }

    private static RuntimeSessionRunner CreateRunner(
        InMemoryRuntimeSessionRepository repository,
        InMemoryRuntimeDomainEventPublisher eventPublisher,
        IRuntimeCommandExecutor commandExecutor)
    {
        return new RuntimeSessionRunner(
            repository,
            eventPublisher,
            commandExecutor,
            new DeterministicRuntimeIdProvider(),
            new FixedClock(StartedAtUtc));
    }

    private static StartRuntimeSessionRequest CreateStartRequest(ExecutableRuntimeProcess process)
    {
        return new StartRuntimeSessionRequest(
            RuntimeSessionId.New(),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RecipeSnapshotId("recipe-20260629-001"),
            process,
            RuntimeTestReleaseIdentity.TraceMetadata());
    }

    private static ExecutableRuntimeProcess CreateTwoNodeProcess()
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@1.0.0"),
            [
                Node(
                    new RuntimeNodeId("node-scan"),
                    "Scan barcode",
                    new RuntimeCapabilityId("device.scanner"),
                    "Scan",
                    TimeSpan.FromSeconds(30)),
                Node(
                    new RuntimeNodeId("node-measure"),
                    "Measure voltage",
                    new RuntimeCapabilityId("device.multimeter"),
                    "MeasureVoltage",
                    TimeSpan.FromSeconds(30))
            ]);
    }

    private static ExecutableRuntimeProcess CreateBranchingProcess()
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-branching"),
            new ProcessVersionId("process-branching@1.0.0"),
            [
                Node(
                    new RuntimeNodeId("node-scan"),
                    "Read inspection result",
                    new RuntimeCapabilityId("device.scanner"),
                    "ReadResult",
                    TimeSpan.FromSeconds(30)),
                Node(
                    new RuntimeNodeId("node-pass"),
                    "Open pass gate",
                    new RuntimeCapabilityId("device.io"),
                    "OpenPassGate",
                    TimeSpan.FromSeconds(30)),
                Node(
                    new RuntimeNodeId("node-fail"),
                    "Open fail gate",
                    new RuntimeCapabilityId("device.io"),
                    "OpenFailGate",
                    TimeSpan.FromSeconds(30))
            ])
        {
            StartNodeId = new RuntimeNodeId("node-start"),
            RoutingNodes =
            [
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId("node-start"),
                    "Start",
                    ExecutableRuntimeRoutingNodeKind.Start),
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId("node-route"),
                    "Route inspection result",
                    ExecutableRuntimeRoutingNodeKind.Decision),
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId("node-end"),
                    "End",
                    ExecutableRuntimeRoutingNodeKind.End)
            ],
            Transitions =
            [
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-start"),
                    new RuntimeNodeId("node-scan"),
                    null),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-scan"),
                    new RuntimeNodeId("node-route"),
                    null),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-route"),
                    new RuntimeNodeId("node-pass"),
                    "ok"),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-route"),
                    new RuntimeNodeId("node-fail"),
                    "ng"),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-pass"),
                    new RuntimeNodeId("node-end"),
                    null),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-fail"),
                    new RuntimeNodeId("node-end"),
                    null)
            ]
        };
    }

    private static ExecutableRuntimeProcess CreateLoopingProcess(int maxLoopTraversals)
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-looping"),
            new ProcessVersionId("process-looping@1.0.0"),
            [
                Node(
                    new RuntimeNodeId("node-scan"),
                    "Read inspection result",
                    new RuntimeCapabilityId("device.scanner"),
                    "ReadResult",
                    TimeSpan.FromSeconds(30))
            ])
        {
            StartNodeId = new RuntimeNodeId("node-start"),
            RoutingNodes =
            [
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId("node-start"),
                    "Start",
                    ExecutableRuntimeRoutingNodeKind.Start),
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId("node-route"),
                    "Route inspection result",
                    ExecutableRuntimeRoutingNodeKind.Decision),
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId("node-end"),
                    "End",
                    ExecutableRuntimeRoutingNodeKind.End)
            ],
            Transitions =
            [
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-start"),
                    new RuntimeNodeId("node-scan"),
                    null),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-scan"),
                    new RuntimeNodeId("node-route"),
                    null),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-route"),
                    new RuntimeNodeId("node-scan"),
                    "retry",
                    maxLoopTraversals),
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId("node-route"),
                    new RuntimeNodeId("node-end"),
                    "ok")
            ]
        };
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DeterministicRuntimeIdProvider : IRuntimeIdProvider
    {
        private int _value;

        public RuntimeSessionId NewSessionId()
        {
            return new RuntimeSessionId(NextGuid());
        }

        public RuntimeStepId NewStepId()
        {
            return new RuntimeStepId(NextGuid());
        }

        public RuntimeCommandId NewCommandId()
        {
            return new RuntimeCommandId(NextGuid());
        }

        private Guid NextGuid()
        {
            _value++;
            return Guid.Parse($"00000000-0000-0000-0000-{_value:000000000000}");
        }
    }

    private sealed class ScriptedRuntimeCommandExecutor : IRuntimeCommandExecutor
    {
        private readonly Queue<RuntimeCommandExecutionResult> _results;

        public ScriptedRuntimeCommandExecutor(params RuntimeCommandExecutionResult[] results)
        {
            _results = new Queue<RuntimeCommandExecutionResult>(results);
        }

        public List<RuntimeCommandExecutionContext> Contexts { get; } = [];

        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);

            var result = _results.Count == 0
                ? RuntimeCommandExecutionResult.Completed()
                : _results.Dequeue();

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelingCommandExecutor(CancellationTokenSource cancellation)
        : IRuntimeCommandExecutor
    {
        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = cancellationToken;
            cancellation.Cancel();
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed("completed"));
        }
    }

    private sealed class ThrowingCommandExecutor : IRuntimeCommandExecutor
    {
        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = cancellationToken;
            throw new InvalidOperationException("simulated executor fault");
        }
    }


}
