using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
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

        var result = await runner.RunAsync(CreateStartRequest(CreateTwoNodeProcess()));

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        Assert.Equal(2, result.Value.CompletedSteps);
        Assert.Equal(2, result.Value.CommandCount);
        Assert.Equal(0, result.Value.IncidentCount);
        Assert.Equal(2, commandExecutor.Contexts.Count);
        Assert.Equal("station-a", commandExecutor.Contexts[0].StationId.Value);
        Assert.Equal("snapshot-20260629-001", commandExecutor.Contexts[0].ConfigurationSnapshotId.Value);
        Assert.Equal("node-scan", commandExecutor.Contexts[0].NodeId.Value);
        Assert.Equal("node-measure", commandExecutor.Contexts[1].NodeId.Value);

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
    public async Task RunAsyncRejectsDynamicContainerTimeoutOutsideTimerRange()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor();
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(
            CreateDynamicProcess(TimeSpan.FromDays(100))));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Runtime.DynamicContainerTimeoutUnsupported", result.Error.Code);
        Assert.Equal(0, repository.SaveCount);
        Assert.Empty(eventPublisher.Events);
        Assert.Empty(commandExecutor.Contexts);
    }

    [Fact]
    public async Task RunAsyncMaterializesAutomationPlanChildrenInsideAggregate()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed(
                """{"automation_plan":[{"type":"axis.move","axis":"X"},{"type":"flow.wait","duration_ms":0}]}"""),
            RuntimeCommandExecutionResult.Completed("axis-complete"),
            RuntimeCommandExecutionResult.Completed("wait-complete"));
        var runner = CreateRunner(repository, eventPublisher, commandExecutor);
        var request = new StartRuntimeSessionRequest(
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RecipeSnapshotId("recipe-20260629-001"),
            CreateDynamicProcess(),
            new RuntimeSessionTraceMetadata(
                null,
                null,
                null,
                null,
                null,
                "project-a",
                "application-a",
                "release-a"));

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(RuntimeSessionStatus.Completed, result.Value.Status);
        Assert.Equal(3, result.Value.CompletedSteps);
        Assert.Equal(3, result.Value.CommandCount);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        var parent = Assert.Single(persisted.Steps, step => step.ParentStepId is null);
        Assert.Equal("script-node:action:1", parent.ActionId.Value);
        var children = persisted.Steps
            .Where(step => step.ParentStepId == parent.Id)
            .OrderBy(step => step.DynamicSequence)
            .ToArray();
        Assert.Equal(2, children.Length);
        Assert.Equal("script-node:action:1:child:1", children[0].ActionId.Value);
        Assert.Equal("script-node:action:1:automation-plan:node:1", children[0].NodeId.Value);
        Assert.Equal(1, children[0].DynamicSequence);
        Assert.Equal("script-node:action:1:child:2", children[1].ActionId.Value);
        Assert.All(persisted.Commands, command => Assert.Equal(
            persisted.Steps.Single(step => step.Id == command.StepId).ActionId,
            command.ActionId));

        Assert.Equal(3, commandExecutor.Contexts.Count);
        Assert.All(commandExecutor.Contexts, context =>
        {
            Assert.Equal("project-a", context.ProjectId);
            Assert.Equal("application-a", context.ApplicationId);
            Assert.Equal("release-a", context.ProjectSnapshotId);
            Assert.NotNull(context.ActionId);
        });
        Assert.Equal(parent.Id, commandExecutor.Contexts[1].ParentStepId);
        Assert.Equal(1, commandExecutor.Contexts[1].DynamicSequence);
        Assert.Equal(RuntimeFlowCommand.Capability, commandExecutor.Contexts[2].TargetCapability.Value);
        var parentCommand = persisted.Commands.Single(command => command.StepId == parent.Id);
        var completedCommandIds = eventPublisher.Events
            .OfType<RuntimeCommandStatusChangedDomainEvent>()
            .Where(runtimeEvent => runtimeEvent.ToStatus == RuntimeCommandStatus.Completed)
            .Select(runtimeEvent => runtimeEvent.CommandId)
            .ToArray();
        Assert.Equal(parentCommand.Id, completedCommandIds[^1]);
    }

    [Fact]
    public async Task RunAsyncPreflightsCompleteAutomationPlanBeforeStartingChildren()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed(
                """{"automation_plan":[{"type":"axis.move"},{"type":"unknown.custom"}]}"""));
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateDynamicProcess()));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        Assert.Single(commandExecutor.Contexts);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        Assert.Single(persisted.Steps);
        Assert.Single(persisted.Commands);
        Assert.Equal(RuntimeStepStatus.Failed, persisted.Steps.Single().Status);
        Assert.Equal(RuntimeCommandStatus.Failed, persisted.Commands.Single().Status);
        Assert.Equal("Runtime.AutomationPlanInvalid", Assert.Single(persisted.Incidents).Code);
    }

    [Fact]
    public async Task RunAsyncChildRejectionFailsChildParentAndSessionAndStopsPlan()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed(
                """{"automation_plan":[{"type":"axis.move"},{"type":"io.light"}]}"""),
            RuntimeCommandExecutionResult.Rejected("route unavailable"),
            RuntimeCommandExecutionResult.Completed("must-not-run"));
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateDynamicProcess()));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        Assert.Equal(2, commandExecutor.Contexts.Count);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        Assert.Equal(2, persisted.Steps.Count);
        Assert.All(persisted.Steps, step => Assert.Equal(RuntimeStepStatus.Failed, step.Status));
        Assert.Contains(persisted.Commands, command => command.Status == RuntimeCommandStatus.Rejected);
        Assert.Contains(persisted.Commands, command => command.Status == RuntimeCommandStatus.Failed);
        Assert.Equal("Runtime.ChildCommandRejected", Assert.Single(persisted.Incidents).Code);
    }

    [Theory]
    [InlineData(RuntimeCommandExecutionOutcome.TimedOut, RuntimeSessionStatus.Failed)]
    [InlineData(RuntimeCommandExecutionOutcome.Canceled, RuntimeSessionStatus.Canceled)]
    public async Task RunAsyncPropagatesChildTerminalOutcomeToParentAndSession(
        RuntimeCommandExecutionOutcome childOutcome,
        RuntimeSessionStatus expectedSessionStatus)
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var terminalResult = childOutcome == RuntimeCommandExecutionOutcome.TimedOut
            ? RuntimeCommandExecutionResult.TimedOut("child timeout")
            : RuntimeCommandExecutionResult.Canceled("child canceled");
        var commandExecutor = new ScriptedRuntimeCommandExecutor(
            RuntimeCommandExecutionResult.Completed(
                """{"automation_plan":[{"type":"axis.move"}]}"""),
            terminalResult);
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(CreateDynamicProcess()));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(expectedSessionStatus, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        var parent = persisted.Steps.Single(step => step.ParentStepId is null);
        var child = persisted.Steps.Single(step => step.ParentStepId == parent.Id);
        var parentCommand = persisted.Commands.Single(command => command.StepId == parent.Id);
        var childCommand = persisted.Commands.Single(command => command.StepId == child.Id);
        if (childOutcome == RuntimeCommandExecutionOutcome.TimedOut)
        {
            Assert.Equal(RuntimeStepStatus.Failed, parent.Status);
            Assert.Equal(RuntimeStepStatus.Failed, child.Status);
            Assert.Equal(RuntimeCommandStatus.Failed, parentCommand.Status);
            Assert.Equal(RuntimeCommandStatus.TimedOut, childCommand.Status);
            Assert.Equal("Runtime.ChildCommandTimedOut", Assert.Single(persisted.Incidents).Code);
        }
        else
        {
            Assert.Equal(RuntimeStepStatus.Canceled, parent.Status);
            Assert.Equal(RuntimeStepStatus.Canceled, child.Status);
            Assert.Equal(RuntimeCommandStatus.Canceled, parentCommand.Status);
            Assert.Equal(RuntimeCommandStatus.Canceled, childCommand.Status);
            Assert.Empty(persisted.Incidents);
        }
    }

    [Fact]
    public async Task RunAsyncUsesOneTotalTimeoutBudgetForDynamicContainer()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new BlockingDynamicChildRuntimeCommandExecutor(
            """{"automation_plan":[{"type":"axis.move"},{"type":"io.light"}]}""");
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);

        var result = await runner.RunAsync(CreateStartRequest(
            CreateDynamicProcess(TimeSpan.FromMilliseconds(50))));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);
        Assert.Equal(2, commandExecutor.Contexts.Count);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        var parent = persisted.Steps.Single(step => step.ParentStepId is null);
        var child = persisted.Steps.Single(step => step.ParentStepId == parent.Id);
        Assert.Equal(
            RuntimeCommandStatus.TimedOut,
            persisted.Commands.Single(command => command.StepId == parent.Id).Status);
        Assert.Equal(
            RuntimeCommandStatus.TimedOut,
            persisted.Commands.Single(command => command.StepId == child.Id).Status);
        Assert.Equal("Runtime.CommandTimedOut", Assert.Single(persisted.Incidents).Code);
    }

    [Fact]
    public async Task RunAsyncExternalCancellationPersistsChildParentAndSessionCancellation()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var commandExecutor = new BlockingDynamicChildRuntimeCommandExecutor(
            """{"automation_plan":[{"type":"axis.move"}]}""");
        var runner = CreateRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            commandExecutor);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await runner.RunAsync(
            CreateStartRequest(CreateDynamicProcess(TimeSpan.FromSeconds(5))),
            cancellation.Token);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(RuntimeSessionStatus.Canceled, result.Value.Status);
        var persisted = Assert.IsType<RuntimeSession>(
            await repository.GetByIdAsync(result.Value.SessionId));
        Assert.All(persisted.Steps, step => Assert.Equal(RuntimeStepStatus.Canceled, step.Status));
        Assert.All(persisted.Commands, command => Assert.Equal(RuntimeCommandStatus.Canceled, command.Status));
        Assert.Empty(persisted.Incidents);
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
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RecipeSnapshotId("recipe-20260629-001"),
            process);
    }

    private static ExecutableRuntimeProcess CreateTwoNodeProcess()
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@1.0.0"),
            [
                new ExecutableRuntimeNode(
                    new RuntimeNodeId("node-scan"),
                    "Scan barcode",
                    new RuntimeCapabilityId("device.scanner"),
                    "Scan",
                    TimeSpan.FromSeconds(30)),
                new ExecutableRuntimeNode(
                    new RuntimeNodeId("node-measure"),
                    "Measure voltage",
                    new RuntimeCapabilityId("device.multimeter"),
                    "MeasureVoltage",
                    TimeSpan.FromSeconds(30))
            ]);
    }

    private static ExecutableRuntimeProcess CreateDynamicProcess(TimeSpan? timeout = null)
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-dynamic"),
            new ProcessVersionId("process-dynamic@1.0.0"),
            [
                new ExecutableRuntimeNode(
                    new RuntimeNodeId("script-node"),
                    "Run Blockly plan",
                    new RuntimeCapabilityId(RuntimeScriptCommand.PythonCapability),
                    RuntimeScriptCommand.PythonCommandName,
                    timeout ?? TimeSpan.FromSeconds(30),
                    ActionId: new RuntimeActionId("script-node:action:1"),
                    DynamicChildren: new ExecutableRuntimeDynamicActionSlot(
                        "script-node:action:1:automation-plan",
                        "script-node:action:1:child:",
                        1,
                        "ContainerOnly"))
            ]);
    }

    private static ExecutableRuntimeProcess CreateBranchingProcess()
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process-branching"),
            new ProcessVersionId("process-branching@1.0.0"),
            [
                new ExecutableRuntimeNode(
                    new RuntimeNodeId("node-scan"),
                    "Read inspection result",
                    new RuntimeCapabilityId("device.scanner"),
                    "ReadResult",
                    TimeSpan.FromSeconds(30)),
                new ExecutableRuntimeNode(
                    new RuntimeNodeId("node-pass"),
                    "Open pass gate",
                    new RuntimeCapabilityId("device.io"),
                    "OpenPassGate",
                    TimeSpan.FromSeconds(30)),
                new ExecutableRuntimeNode(
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
                new ExecutableRuntimeNode(
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

    private sealed class BlockingDynamicChildRuntimeCommandExecutor(string automationPlanPayload)
        : IRuntimeCommandExecutor
    {
        public List<RuntimeCommandExecutionContext> Contexts { get; } = [];

        public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            if (Contexts.Count == 1)
            {
                return RuntimeCommandExecutionResult.Completed(automationPlanPayload);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RuntimeCommandExecutionResult.Completed("unreachable");
        }
    }

}
