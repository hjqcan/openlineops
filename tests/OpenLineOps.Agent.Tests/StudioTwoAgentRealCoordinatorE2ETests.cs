using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenLineOps.Agent.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OpenLineOps.Agent.Tests;

public sealed partial class StagedAgentRabbitMqProcessE2ETests
{
    private const string StudioTwoAgentCleanupGateVariable =
        "OPENLINEOPS_STUDIO_TWO_AGENT_CLEANUP_GATE";
    private static readonly JsonSerializerOptions StudioHttpJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
    private static readonly JsonSerializerOptions StudioEvidenceJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StudioProjectRunsThroughStagedCoordinatorAndTwoStagedAgents()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gate = ResolveStudioTwoAgentGate();
        if (gate is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var cancellationToken = timeout.Token;
        await using var fixtureLease = StudioProductionFixtureLease.Open(gate.HandoffPath);
        var fixture = fixtureLease.Fixture;
        await using var postgres = await StudioPostgresSchemaLease.CreateAsync(
            gate.PostgreSqlConnectionString,
            gate.ServiceScope,
            cancellationToken);
        await using var agents = await StudioTwoAgentExternalProcessHarness.PrepareAsync(
            fixture,
            gate.Prerequisites.AgentBundleRoot,
            gate.Release.Agent.EntrypointSha256,
            gate.Prerequisites.BrokerUri,
            gate.ServiceScope,
            cancellationToken);

        AssertDistinctStationBoundary(agents);
        var coordinatorWorkRoot = Path.Combine(agents.RootPath, "coordinator");
        Directory.CreateDirectory(coordinatorWorkRoot);
        var deployments = agents.Deployments;
        var firstDeployment = ToRealDeployment(deployments[0]);
        var secondDeployment = ToRealDeployment(deployments[1]);
        var credentials = CreateCoordinatorCredentials(agents, firstDeployment, secondDeployment);
        var apiExecutablePath = RequiredDirectFile(
            gate.Prerequisites.ApiBundleRoot,
            "OpenLineOps.Api.exe");
        var pluginHostPath = ResolveStagedExecutable(
            gate.Prerequisites.ApiBundleRoot,
            gate.Prerequisites.AgentBundleRoot,
            "OpenLineOps.PluginHost.exe");
        var scriptWorkerPath = ResolveStagedExecutable(
            gate.Prerequisites.ApiBundleRoot,
            gate.Prerequisites.AgentBundleRoot,
            "OpenLineOps.ScriptWorker.exe");
        var coordinatorOptions = new RealCoordinatorProcessOptions
        {
            ApiExecutablePath = apiExecutablePath,
            ExpectedApiExecutableSha256 = gate.Release.Api.EntrypointSha256,
            WorkRoot = coordinatorWorkRoot,
            LoopbackPort = AllocateStudioLoopbackPort(),
            StartupProjectFile = fixture.ProjectFilePath,
            PostgreSqlConnectionString = postgres.ConnectionString,
            RabbitMqBrokerUri = gate.Prerequisites.BrokerUri,
            RabbitMqRequireTls = IsTls(gate.Prerequisites.BrokerUri),
            CoordinatorId = agents.CoordinatorId,
            DeploymentCatalogDirectory = agents.DeploymentCatalogPath,
            FirstDeployment = firstDeployment,
            SecondDeployment = secondDeployment,
            Credentials = credentials,
            StatePaths = RealCoordinatorStatePaths.Under(coordinatorWorkRoot),
            PluginHostExecutablePath = pluginHostPath,
            PythonWorkerExecutablePath = scriptWorkerPath,
            StartupTimeout = TimeSpan.FromMinutes(2),
            HttpRequestTimeout = TimeSpan.FromSeconds(20)
        };

        await using var coordinator = await RealCoordinatorProcessHarness.StartAsync(
            coordinatorOptions,
            cancellationToken);
        await agents.StartAgentsAsync(coordinator.BaseUri, cancellationToken);
        var materialArrivalIpcIsolation = agents.MaterialArrivalIpcIsolation;
        Assert.True(materialArrivalIpcIsolation.EntryServiceTokenConnected);
        Assert.True(materialArrivalIpcIsolation.EntryPipeExactAclVerified);
        Assert.True(materialArrivalIpcIsolation.DownstreamServiceTokenExplicitAccessDenied);
        Assert.True(materialArrivalIpcIsolation.BothServicesRunningOnOriginalPids);
        var entryAgentPid = agents.EntryAgentProcessId;
        var downstreamAgentPid = agents.DownstreamAgentProcessId;
        Assert.NotEqual(entryAgentPid, downstreamAgentPid);
        var entryAgentNonAdministrative = agents.EntryAgentNonAdministrative;
        var downstreamAgentNonAdministrative = agents.DownstreamAgentNonAdministrative;
        var serviceAccountName = agents.ServiceAccountName;
        var serviceAccountSid = agents.ServiceAccountSid;
        var entryServiceSid = agents.EntryAgentServiceSid;
        var downstreamServiceSid = agents.DownstreamAgentServiceSid;
        Assert.True(entryAgentNonAdministrative);
        Assert.True(downstreamAgentNonAdministrative);
        Assert.Equal(RestrictedAgentIdentity.LocalServiceAccountName, serviceAccountName);
        Assert.Equal(RestrictedAgentIdentity.LocalServiceAccountSid, serviceAccountSid);
        Assert.NotEqual(entryServiceSid, downstreamServiceSid);

        var clock = new StudioLogicalClock();
        using var operatorClient = coordinator.CreateOperatorClient();
        await RegisterStudioSlotsAsync(operatorClient, fixture, clock, cancellationToken);

        var unitA = await RegisterArriveAndStartAsync(
            operatorClient,
            fixture,
            agents.EntryAgent,
            "Delay",
            reserveSlot: true,
            clock,
            cancellationToken);
        var unitB = await RegisterArriveAndStartAsync(
            operatorClient,
            fixture,
            agents.EntryAgent,
            "Passed",
            reserveSlot: false,
            clock,
            cancellationToken);

        var runABeforeRestart = await WaitForOperationAsync(
            operatorClient,
            unitA.RunId,
            fixture.EntryStation.OperationId,
            "Completed",
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var runABeforeRestartSha256 = runABeforeRestart.Sha256;
        await coordinator.CrashAsync(cancellationToken);
        Assert.Equal(entryAgentPid, agents.EntryAgentProcessId);
        Assert.Equal(downstreamAgentPid, agents.DownstreamAgentProcessId);
        _ = await coordinator.RestartAsync(cancellationToken);
        using var operatorAfterRestart = coordinator.CreateOperatorClient();
        var restoredRunA = await WaitForOperationAsync(
            operatorAfterRestart,
            unitA.RunId,
            fixture.EntryStation.OperationId,
            "Completed",
            TimeSpan.FromSeconds(45),
            cancellationToken);
        Assert.Equal(
            unitA.RunId.ToString("D", CultureInfo.InvariantCulture),
            StudioApiRequiredString(restoredRunA.Root, "productionRunId"));
        Assert.Equal(runABeforeRestartSha256, restoredRunA.Sha256);

        await MoveBetweenStudioSlotsAsync(
            operatorAfterRestart,
            fixture,
            unitA,
            fixture.EntryStation,
            fixture.DownstreamStation,
            clock,
            cancellationToken);
        _ = await WaitForOperationAsync(
            operatorAfterRestart,
            unitA.RunId,
            fixture.DownstreamStation.OperationId,
            "Running",
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var vendorA = Assert.Single(await WaitForVendorStartsForAgentAsync(
            agents,
            downstreamAgentPid,
            1,
            TimeSpan.FromSeconds(30),
            cancellationToken));
        Assert.DoesNotContain(entryAgentPid, vendorA.AncestorProcessIds);

        await ReserveLoadStartStudioSlotAsync(
            operatorAfterRestart,
            fixture,
            unitB,
            fixture.EntryStation,
            clock,
            cancellationToken);
        await SubmitStudioRunAsync(
            operatorAfterRestart,
            fixture,
            unitB,
            cancellationToken);
        _ = await WaitForOperationAsync(
            operatorAfterRestart,
            unitB.RunId,
            fixture.EntryStation.OperationId,
            "Running",
            TimeSpan.FromMinutes(1),
            cancellationToken);
        var parallelLineState = await WaitForParallelLineStateAsync(
            operatorAfterRestart,
            fixture,
            unitA.RunId,
            unitB.RunId,
            cancellationToken);
        var parallelEvidence = AssertParallelResourceLeases(
            parallelLineState.Root,
            fixture,
            unitA.RunId,
            unitB.RunId);

        _ = await WaitForOperationAsync(
            operatorAfterRestart,
            unitB.RunId,
            fixture.EntryStation.OperationId,
            "Completed",
            TimeSpan.FromMinutes(2),
            cancellationToken);
        _ = await CompleteAndUnloadStudioSlotAsync(
            operatorAfterRestart,
            fixture,
            unitB,
            fixture.EntryStation,
            fixture.DownstreamStation.StationSystemId,
            clock,
            cancellationToken);

        var terminalA = await WaitForTerminalRunAsync(
            operatorAfterRestart,
            unitA.RunId,
            "Completed",
            "Passed",
            cancellationToken);
        var unitAFinalUnloadAtUtc = await CompleteAndUnloadStudioSlotAsync(
            operatorAfterRestart,
            fixture,
            unitA,
            fixture.DownstreamStation,
            fixture.DownstreamStation.StationSystemId,
            clock,
            cancellationToken);
        await ReserveLoadStartStudioSlotAsync(
            operatorAfterRestart,
            fixture,
            unitB,
            fixture.DownstreamStation,
            clock,
            cancellationToken);
        var terminalB = await WaitForTerminalRunAsync(
            operatorAfterRestart,
            unitB.RunId,
            "Completed",
            "Passed",
            cancellationToken);
        var unitBFinalUnloadAtUtc = await CompleteAndUnloadStudioSlotAsync(
            operatorAfterRestart,
            fixture,
            unitB,
            fixture.DownstreamStation,
            fixture.DownstreamStation.StationSystemId,
            clock,
            cancellationToken);

        var boundVendorStarts = await WaitForVendorStartsForAgentAsync(
            agents,
            downstreamAgentPid,
            2,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.Equal(2, boundVendorStarts.Count);
        Assert.Equal(2, boundVendorStarts.Select(entry => entry.ProcessId).Distinct().Count());
        Assert.All(boundVendorStarts, entry =>
        {
            Assert.Contains(downstreamAgentPid, entry.AncestorProcessIds);
            Assert.DoesNotContain(entryAgentPid, entry.AncestorProcessIds);
        });

        var traceA = await WaitForStudioTraceAsync(
            operatorAfterRestart,
            unitA.RunId,
            cancellationToken);
        var traceB = await WaitForStudioTraceAsync(
            operatorAfterRestart,
            unitB.RunId,
            cancellationToken);
        var artifactEvidence = await VerifyStudioVendorArtifactsAsync(
            operatorAfterRestart,
            traceB.Root,
            fixture.DownstreamStation.OperationId,
            cancellationToken);

        var unitC = await RegisterArriveAndStartAsync(
            operatorAfterRestart,
            fixture,
            agents.EntryAgent,
            "SpawnChildDelayRecovery",
            reserveSlot: true,
            clock,
            cancellationToken);
        _ = await WaitForOperationAsync(
            operatorAfterRestart,
            unitC.RunId,
            fixture.EntryStation.OperationId,
            "Completed",
            TimeSpan.FromMinutes(2),
            cancellationToken);
        await MoveBetweenStudioSlotsAsync(
            operatorAfterRestart,
            fixture,
            unitC,
            fixture.EntryStation,
            fixture.DownstreamStation,
            clock,
            cancellationToken);
        var recoveryRunning = await WaitForOperationAsync(
            operatorAfterRestart,
            unitC.RunId,
            fixture.DownstreamStation.OperationId,
            "Running",
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var recoveryOperation = StudioApiRequiredArray(
            recoveryRunning.Root,
            "operations").Single(operation => string.Equals(
            StudioApiRequiredString(operation, "operationId"),
            fixture.DownstreamStation.OperationId,
            StringComparison.Ordinal));
        var recoveryOperationRunId = StudioApiRequiredString(
            recoveryOperation,
            "operationRunId");
        var recoveryVendorTree = await WaitForStudioRecoveryVendorTreeAsync(
            agents,
            downstreamAgentPid,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.Equal(4, recoveryVendorTree.AllStarts.Count);
        Assert.Equal(3, recoveryVendorTree.RootInvocations.Count);
        var recoveryVendorStart = recoveryVendorTree.RecoveryRoot;
        var recoveryVendorChild = Assert.Single(recoveryVendorTree.RecoveryChildren);
        Assert.Contains(downstreamAgentPid, recoveryVendorStart.AncestorProcessIds);
        Assert.Contains(recoveryVendorStart.ProcessId, recoveryVendorChild.AncestorProcessIds);
        Assert.Contains(downstreamAgentPid, recoveryVendorChild.AncestorProcessIds);

        await coordinator.CrashAsync(cancellationToken);
        Assert.Equal(entryAgentPid, agents.EntryAgentProcessId);
        Assert.Equal(downstreamAgentPid, agents.DownstreamAgentProcessId);
        await WaitForStudioProcessExitAsync(
            recoveryVendorStart.ProcessId,
            TimeSpan.FromMinutes(1),
            cancellationToken);
        await WaitForStudioProcessExitAsync(
            recoveryVendorChild.ProcessId,
            TimeSpan.FromMinutes(1),
            cancellationToken);
        _ = await coordinator.RestartAsync(cancellationToken);
        using var operatorAfterRecoveryRestart = coordinator.CreateOperatorClient();
        var recoveryRequired = await WaitForStudioRunAsync(
            operatorAfterRecoveryRestart,
            unitC.RunId,
            root => !StudioRequiredBoolean(root, "isTerminal")
                    && string.Equals(
                        StudioApiRequiredString(root, "controlState"),
                        "RecoveryRequired",
                        StringComparison.Ordinal),
            TimeSpan.FromMinutes(1),
            cancellationToken);
        var recoveryOperationCount = StudioApiRequiredArray(
                recoveryRequired.Root,
                "operations")
            .Count(operation => string.Equals(
                StudioApiRequiredString(operation, "operationId"),
                fixture.DownstreamStation.OperationId,
                StringComparison.Ordinal));
        Assert.Equal(1, recoveryOperationCount);
        var postgresBeforeReconcile = await WaitForStudioPostgresResultCountAsync(
            postgres,
            expectedResultCount: 6,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.Equal(6, postgresBeforeReconcile.StationJobResultCount);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var allStartsBeforeReconcile = agents.ReadVendorProcessStarts()
            .Where(entry => entry.AncestorProcessIds.Contains(downstreamAgentPid))
            .ToList();
        var rootsBeforeReconcile = StudioRootVendorInvocations(allStartsBeforeReconcile);
        Assert.Equal(4, allStartsBeforeReconcile.Count);
        Assert.Equal(3, rootsBeforeReconcile.Count);
        var recoveryDecisionId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var recoveryDecidedAtUtc = clock.Next().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
        var reconcileResponse = await StudioPostJsonAsync(
            operatorAfterRecoveryRestart,
            $"api/production-runs/{unitC.RunId:D}/commands/Reconcile",
            new
            {
                reason = "Operator confirmed the interrupted vendor action completed safely.",
                operationId = (string?)null,
                recoveryDecision = new
                {
                    decisionId = recoveryDecisionId,
                    evidenceReference = $"urn:openlineops:e2e:recovery:{unitC.RunId:D}:{recoveryVendorStart.ProcessId}",
                    decidedAtUtc = recoveryDecidedAtUtc,
                    operationRunId = recoveryOperationRunId,
                    operationId = (string?)null,
                    observedJudgement = "Passed",
                    observedOutputs = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["operator.observation"] = new
                        {
                            kind = "Text",
                            canonicalValue = "completed-before-coordinator-restart"
                        }
                    }
                }
            },
            HttpStatusCode.OK,
            cancellationToken);
        var terminalC = await WaitForTerminalRunAsync(
            operatorAfterRecoveryRestart,
            unitC.RunId,
            "Completed",
            "Passed",
            cancellationToken);
        var recoveryDecisions = StudioApiRequiredArray(
            terminalC.Root,
            "recoveryDecisions");
        var recoveryDecision = Assert.Single(recoveryDecisions);
        Assert.Equal(
            recoveryDecisionId,
            StudioApiRequiredString(recoveryDecision, "decisionId"));
        Assert.Equal("Reconcile", StudioApiRequiredString(recoveryDecision, "kind"));
        Assert.Equal(
            recoveryOperationRunId,
            StudioApiRequiredString(recoveryDecision, "operationRunId"));
        Assert.Equal(1, StudioApiRequiredArray(terminalC.Root, "operations")
            .Count(operation => string.Equals(
                StudioApiRequiredString(operation, "operationId"),
                fixture.DownstreamStation.OperationId,
                StringComparison.Ordinal)));
        var unitCFinalUnloadAtUtc = await CompleteAndUnloadStudioSlotAsync(
            operatorAfterRecoveryRestart,
            fixture,
            unitC,
            fixture.DownstreamStation,
            fixture.DownstreamStation.StationSystemId,
            clock,
            cancellationToken);
        var traceC = await WaitForStudioTraceAsync(
            operatorAfterRecoveryRestart,
            unitC.RunId,
            cancellationToken);
        var finalUnloadEvidence = new[]
        {
            await AssertStudioFinalUnloadLifecycleAsync(
                operatorAfterRecoveryRestart,
                fixture,
                unitA,
                fixture.DownstreamStation,
                unitAFinalUnloadAtUtc,
                cancellationToken),
            await AssertStudioFinalUnloadLifecycleAsync(
                operatorAfterRecoveryRestart,
                fixture,
                unitB,
                fixture.DownstreamStation,
                unitBFinalUnloadAtUtc,
                cancellationToken),
            await AssertStudioFinalUnloadLifecycleAsync(
                operatorAfterRecoveryRestart,
                fixture,
                unitC,
                fixture.DownstreamStation,
                unitCFinalUnloadAtUtc,
                cancellationToken)
        };
        var recoveryAuditPresent = StudioApiRequiredArray(traceC.Root, "auditEntries")
            .Any(entry => string.Equals(
                StudioApiRequiredString(entry, "action"),
                "ProductionRun.Recovery.Reconcile",
                StringComparison.Ordinal));
        Assert.True(recoveryAuditPresent);
        var replayObservationWindow = TimeSpan.FromSeconds(5);
        await Task.Delay(replayObservationWindow, cancellationToken);
        var runAfterReplayWindow = await WaitForStudioRunAsync(
            operatorAfterRecoveryRestart,
            unitC.RunId,
            root => StudioRequiredBoolean(root, "isTerminal"),
            TimeSpan.FromSeconds(10),
            cancellationToken);
        var operationCountAfterReplayWindow = StudioApiRequiredArray(
                runAfterReplayWindow.Root,
                "operations")
            .Count(operation => string.Equals(
                StudioApiRequiredString(operation, "operationId"),
                fixture.DownstreamStation.OperationId,
                StringComparison.Ordinal));
        var postgresAfterReplayWindow = await postgres.ReadEvidenceAsync(cancellationToken);
        Assert.Equal(6, postgresAfterReplayWindow.StationJobCount);
        Assert.Equal(6, postgresAfterReplayWindow.StationJobResultCount);
        Assert.Equal(1, operationCountAfterReplayWindow);
        var frozenVendorLedger = await agents.FreezeVendorProcessLedgerAsync();
        var startsAfterReconcile = frozenVendorLedger.Entries
            .Where(entry => entry.AncestorProcessIds.Contains(downstreamAgentPid))
            .ToList();
        var rootsAfterReconcile = StudioRootVendorInvocations(startsAfterReconcile);
        var recoveryNoReplay = startsAfterReconcile.Count == recoveryVendorTree.AllStarts.Count
                               && rootsAfterReconcile.Count
                               == recoveryVendorTree.RootInvocations.Count
                               && recoveryOperationCount == 1
                               && operationCountAfterReplayWindow == 1
                               && postgresAfterReplayWindow.StationJobCount == 6
                               && postgresAfterReplayWindow.StationJobResultCount == 6;
        Assert.True(recoveryNoReplay);

        var terminalBAfterRestart = await WaitForTerminalRunAsync(
            operatorAfterRecoveryRestart,
            unitB.RunId,
            "Completed",
            "Passed",
            cancellationToken);
        var recoveryEvidence = new StudioRecoveryEvidence(
            unitC.UnitId,
            unitC.RunId,
            recoveryOperationRunId,
            recoveryDecisionId,
            recoveryVendorStart.ProcessId,
            recoveryVendorChild.ProcessId,
            recoveryRequired.Sha256,
            reconcileResponse.Sha256,
            terminalC.Sha256,
            traceC.Sha256,
            recoveryOperationCount,
            recoveryVendorTree.RootInvocations.Count,
            rootsAfterReconcile.Count,
            recoveryVendorTree.AllStarts.Count,
            startsAfterReconcile.Count,
            postgresBeforeReconcile.StationJobResultCount,
            checked((long)replayObservationWindow.TotalMilliseconds),
            operationCountAfterReplayWindow,
            postgresAfterReplayWindow.StationJobCount,
            postgresAfterReplayWindow.StationJobResultCount,
            recoveryAuditPresent,
            recoveryNoReplay);

        var activeRuns = await StudioGetJsonAsync(
            operatorAfterRecoveryRestart,
            "api/operations/active-runs",
            HttpStatusCode.OK,
            cancellationToken);
        Assert.Empty(StudioApiRequiredArray(activeRuns.Root, "runs"));
        var finalLineState = await StudioGetJsonAsync(
            operatorAfterRecoveryRestart,
            $"api/operations/lines/{Uri.EscapeDataString(fixture.ProductionLineDefinitionId)}/state",
            HttpStatusCode.OK,
            cancellationToken);
        Assert.Equal(0, StudioRequiredInt32(finalLineState.Root, "activeRunCount"));

        var entryTokenHash = agents.Credentials.Single(credential =>
            string.Equals(credential.ActorId, agents.EntryAgent.AgentId, StringComparison.Ordinal))
            .TokenSha256;
        var downstreamTokenHash = agents.Credentials.Single(credential =>
            string.Equals(credential.ActorId, agents.DownstreamAgent.AgentId, StringComparison.Ordinal))
            .TokenSha256;
        Assert.NotEqual(entryTokenHash, downstreamTokenHash);
        var entryServiceSidSha256 = StudioSha256Text(entryServiceSid);
        var downstreamServiceSidSha256 = StudioSha256Text(downstreamServiceSid);
        var queueStateWithConsumers = await agents.ReadQueueStateAsync();
        Assert.All(queueStateWithConsumers, queue => Assert.Equal(0U, queue.Value.Messages));
        Assert.Equal(2, queueStateWithConsumers.Count(queue => queue.Value.Consumers == 1));
        var agentPidsUnchangedBeforeStop = agents.EntryAgentProcessId == entryAgentPid
                                           && agents.DownstreamAgentProcessId
                                           == downstreamAgentPid;
        Assert.True(agentPidsUnchangedBeforeStop);

        var agentExitCodes = await agents.StopAgentsAsync();
        Assert.Equal(0, agentExitCodes.EntryExitCode);
        Assert.Equal(0, agentExitCodes.DownstreamExitCode);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        var drainedQueues = await agents.ReadQueueStateAsync();
        Assert.All(drainedQueues, queue => Assert.Equal(0U, queue.Value.Messages));
        Assert.Equal(1, drainedQueues.Count(queue => queue.Value.Consumers == 1));
        await coordinator.StopAsync(cancellationToken);
        var postgresEvidence = await postgres.ReadEvidenceAsync(cancellationToken);
        var postgresFinalUnloadEvidence = await postgres
            .AssertFinalUnloadEvidenceAsync(finalUnloadEvidence, cancellationToken);
        Assert.Equal(3, postgresEvidence.ProductionRunCount);
        Assert.Equal(3, postgresEvidence.TerminalEvidenceCount);
        Assert.Equal(6, postgresEvidence.StationJobCount);
        Assert.Equal(6, postgresEvidence.PublishedStationJobCount);
        Assert.Equal(0, postgresEvidence.UnpublishedStationJobCount);
        Assert.Equal(0, postgresEvidence.QuarantinedStationJobCount);
        Assert.Equal(6, postgresEvidence.StationJobResultCount);
        Assert.Equal(0, postgresEvidence.ActiveLeaseCount);
        Assert.Equal(3, postgresEvidence.ProductionUnitCount);
        Assert.Equal(2, postgresEvidence.AvailableSlotCount);
        Assert.Equal(3, postgresEvidence.DistinctTimelineRunCount);
        Assert.True(postgresEvidence.MaterialTimelineCount >= 30);
        Assert.Equal(3, postgresFinalUnloadEvidence.Count);

        var coordinatorStarts = coordinator.StartHistory.ToArray();
        Assert.Equal(3, coordinatorStarts.Length);
        Assert.Equal(3, coordinatorStarts.Select(start => start.ProcessId).Distinct().Count());
        Assert.Single(coordinatorStarts.Select(start => start.ExecutableSha256).Distinct());
        Assert.Single(coordinatorStarts.Select(start => start.EnvironmentSha256).Distinct());
        var onlyApiWasRestarted = agentPidsUnchangedBeforeStop
                                  && coordinatorStarts.Select(start => start.ProcessId)
                                      .Distinct().Count() == coordinatorStarts.Length;
        var persistentStateRestored = string.Equals(
            runABeforeRestartSha256,
            restoredRunA.Sha256,
            StringComparison.Ordinal);
        Assert.True(onlyApiWasRestarted);
        Assert.True(persistentStateRestored);

        var privateExecutionRoot = fixture.PrivateExecutionRoot;
        var handoffPath = fixture.HandoffPath;
        var agentHarnessRoot = agents.RootPath;
        var postgresSecrets = new NpgsqlConnectionStringBuilder(
            gate.PostgreSqlConnectionString);
        var privateEvidenceValues = agents.Credentials
            .Select(static credential => credential.Token)
            .Append(gate.PostgreSqlConnectionString)
            .Append(postgresSecrets.ConnectionString)
            .Append(postgresSecrets.Username)
            .Append(postgresSecrets.Password)
            .Append(gate.Prerequisites.BrokerUri.AbsoluteUri)
            .Append(gate.Prerequisites.BrokerUri.UserInfo)
            .Append(gate.HandoffPath)
            .Append(privateExecutionRoot)
            .Append(agentHarnessRoot)
            .Append(agents.EntryAgent.Token)
            .Append(agents.DownstreamAgent.Token)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        var rawApiProofs = new[]
        {
            CreateStudioRawApiProof("restored-run-a", restoredRunA, privateEvidenceValues),
            CreateStudioRawApiProof("terminal-run-a", terminalA, privateEvidenceValues),
            CreateStudioRawApiProof("terminal-run-b", terminalB, privateEvidenceValues),
            CreateStudioRawApiProof("terminal-run-c", terminalC, privateEvidenceValues),
            CreateStudioRawApiProof("trace-run-a", traceA, privateEvidenceValues),
            CreateStudioRawApiProof("trace-run-b", traceB, privateEvidenceValues),
            CreateStudioRawApiProof("trace-run-c", traceC, privateEvidenceValues),
            CreateStudioRawApiProof("parallel-line-state", parallelLineState, privateEvidenceValues),
            CreateStudioRawApiProof("final-line-state", finalLineState, privateEvidenceValues),
            CreateStudioRawApiProof("active-runs", activeRuns, privateEvidenceValues),
            CreateStudioRawApiProof("recovery-required", recoveryRequired, privateEvidenceValues),
            CreateStudioRawApiProof("reconcile-response", reconcileResponse, privateEvidenceValues),
            CreateStudioRawApiProof("replay-window-run", runAfterReplayWindow, privateEvidenceValues)
        };
        await fixtureLease.DisposeAsync();
        var privateHandoffDeleted = !File.Exists(handoffPath);
        var privateProjectDeleted = !Directory.Exists(privateExecutionRoot);
        Assert.True(privateHandoffDeleted);
        Assert.True(privateProjectDeleted);
        await postgres.DisposeAsync();
        var postgresSchemaDropped = !await postgres.SchemaExistsAsync(cancellationToken);
        Assert.True(postgresSchemaDropped);
        await agents.DisposeAsync();
        var privateAgentHarnessDeleted = !Directory.Exists(agentHarnessRoot);
        Assert.True(privateAgentHarnessDeleted);
        var rabbitCleanup = agents.RabbitMqCleanup;
        Assert.True(rabbitCleanup.Attempted);
        Assert.True(rabbitCleanup.Succeeded);
        Assert.Equal(9, rabbitCleanup.QueueCount);
        var cleanupEvidence = new StudioCleanupEvidence(
            privateHandoffDeleted,
            privateProjectDeleted,
            privateAgentHarnessDeleted,
            postgresSchemaDropped,
            rabbitCleanup.Attempted,
            rabbitCleanup.Succeeded,
            rabbitCleanup.QueueCount);

        WriteStudioTwoAgentEvidence(
            gate.EvidenceManifestPath,
            fixture,
            gate.Prerequisites,
            gate.Release,
            coordinatorStarts,
            agents.AgentExecutableSha256,
            entryAgentPid,
            downstreamAgentPid,
            agents.EntryAgent,
            agents.DownstreamAgent,
            entryTokenHash,
            downstreamTokenHash,
            serviceAccountName,
            serviceAccountSid,
            entryServiceSidSha256,
            downstreamServiceSidSha256,
            materialArrivalIpcIsolation,
            entryAgentNonAdministrative,
            downstreamAgentNonAdministrative,
            onlyApiWasRestarted,
            persistentStateRestored,
            runABeforeRestartSha256,
            restoredRunA.Sha256,
            terminalA,
            terminalB,
            terminalBAfterRestart,
            traceA,
            traceB,
            parallelLineState,
            parallelEvidence,
            finalLineState,
            activeRuns,
            startsAfterReconcile,
            frozenVendorLedger,
            artifactEvidence,
            rawApiProofs,
            finalUnloadEvidence,
            postgresFinalUnloadEvidence,
            recoveryEvidence,
            drainedQueues,
            postgresEvidence,
            agentExitCodes,
            cleanupEvidence,
            privateEvidenceValues);
    }

    [Fact]
    public async Task CleanupStudioTwoAgentPostgreSqlRabbitMqAndPrivateHandoff()
    {
        var enabled = Environment.GetEnvironmentVariable(
            StudioTwoAgentCleanupGateVariable);
        if (enabled is null)
        {
            return;
        }

        if (!string.Equals(enabled, "true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{StudioTwoAgentCleanupGateVariable} must be exactly 'true'.");
        }

        var serviceScope = RequiredText(
            Environment.GetEnvironmentVariable(StudioTwoAgentServiceScopeVariable),
            StudioTwoAgentServiceScopeVariable);
        if (serviceScope.Length != 32 || serviceScope.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException("Studio cleanup service scope is invalid.");
        }

        var handoffPath = RequiredText(
            Environment.GetEnvironmentVariable(ProductionClosureHandoffVariable),
            ProductionClosureHandoffVariable);
        var connectionString = RequiredText(
            Environment.GetEnvironmentVariable(PostgresConnectionStringVariable),
            PostgresConnectionStringVariable);
        var brokerText = RequiredText(
            Environment.GetEnvironmentVariable(BrokerUriVariable),
            BrokerUriVariable);
        if (!Uri.TryCreate(brokerText, UriKind.Absolute, out var broker)
            || broker.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException("Studio cleanup RabbitMQ URI is invalid.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var cancellationToken = timeout.Token;
        await using var fixtureLease = StudioProductionFixtureLease.Open(handoffPath);
        var fixture = fixtureLease.Fixture;
        var endpoints = new[]
        {
            (AgentId: $"agent.studio.entry.{serviceScope}", fixture.EntryStation.StationId),
            (AgentId: $"agent.studio.downstream.{serviceScope}", fixture.DownstreamStation.StationId)
        };
        var queues = endpoints.SelectMany(endpoint => new[]
            {
                StationTransportRoute.JobQueue(endpoint.AgentId, endpoint.StationId),
                StationTransportRoute.SafetyQueue(
                    endpoint.AgentId,
                    endpoint.StationId,
                    "emergency-stop"),
                StationTransportRoute.SafetyQueue(
                    endpoint.AgentId,
                    endpoint.StationId,
                    "safe-stop"),
                StationTransportRoute.SafetyQueue(
                    endpoint.AgentId,
                    endpoint.StationId,
                    "job-cancel")
            })
            .Append($"openlineops.coordinator.coordinator-studio-{serviceScope}.station-results")
            .ToArray();
        var factory = new ConnectionFactory
        {
            Uri = broker,
            ClientProvidedName = $"openlineops-studio-cleanup-{serviceScope}",
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(5),
            ContinuationTimeout = TimeSpan.FromSeconds(5),
            SocketReadTimeout = TimeSpan.FromSeconds(5),
            SocketWriteTimeout = TimeSpan.FromSeconds(5)
        };
        await using (var rabbit = await factory.CreateConnectionAsync(cancellationToken))
        {
            foreach (var queue in queues)
            {
                try
                {
                    await using var channel = await rabbit.CreateChannelAsync(
                        cancellationToken: cancellationToken);
                    await channel.QueueDeleteAsync(
                        queue,
                        ifUnused: false,
                        ifEmpty: false,
                        cancellationToken: cancellationToken);
                }
                catch (OperationInterruptedException exception)
                    when (exception.ShutdownReason?.ReplyCode == 404)
                {
                }
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = string.Empty,
            ApplicationName = "OpenLineOps.StudioTwoAgent.Compensation"
        };
        await using var postgres = new NpgsqlConnection(builder.ConnectionString);
        await postgres.OpenAsync(cancellationToken);
        await using var command = postgres.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = $"DROP SCHEMA IF EXISTS \"olo_studio_{serviceScope}\" CASCADE;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        NpgsqlConnection.ClearPool(postgres);
    }

    private static StudioTwoAgentGate? ResolveStudioTwoAgentGate()
    {
        var formalGate = Environment.GetEnvironmentVariable(
            StudioTwoAgentFormalGateVariable);
        var serviceScope = Environment.GetEnvironmentVariable(
            StudioTwoAgentServiceScopeVariable);
        var handoff = Environment.GetEnvironmentVariable(ProductionClosureHandoffVariable);
        var postgres = Environment.GetEnvironmentVariable(PostgresConnectionStringVariable);
        var evidence = Environment.GetEnvironmentVariable(StudioTwoAgentEvidenceVariable);
        var releaseManifest = Environment.GetEnvironmentVariable(StudioReleaseManifestVariable);
        if (formalGate is null
            && handoff is null
            && postgres is null
            && evidence is null
            && releaseManifest is null
            && serviceScope is null)
        {
            return null;
        }

        if (!string.Equals(formalGate, "true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{StudioTwoAgentFormalGateVariable} must be exactly 'true' whenever the formal two-Agent gate is invoked.");
        }

        var canonicalServiceScope = RequiredText(
            serviceScope,
            StudioTwoAgentServiceScopeVariable);
        if (canonicalServiceScope.Length != 32
            || canonicalServiceScope.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{StudioTwoAgentServiceScopeVariable} must be 32 lowercase hexadecimal characters.");
        }

        var prerequisites = ResolvePrerequisites()
            ?? throw new InvalidOperationException(
                "Studio two-Agent E2E requires all staged process prerequisites.");
        var release = LoadStudioReleaseAttestation(
            RequiredText(releaseManifest, StudioReleaseManifestVariable),
            prerequisites);
        var handoffPath = Path.GetFullPath(RequiredText(
            handoff,
            ProductionClosureHandoffVariable));
        if (!Path.IsPathFullyQualified(handoffPath)
            || !string.Equals(
                Path.GetFileName(handoffPath),
                "production-closure-handoff.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{ProductionClosureHandoffVariable} must be an absolute production-closure-handoff.json path.");
        }
        ValidateStudioPrivateHandoffPath(handoffPath);

        var evidencePath = Path.GetFullPath(RequiredText(
            evidence,
            StudioTwoAgentEvidenceVariable));
        if (!Path.IsPathFullyQualified(evidencePath)
            || !string.Equals(
                Path.GetFileName(evidencePath),
                "evidence-manifest.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{StudioTwoAgentEvidenceVariable} must be an absolute evidence-manifest.json path.");
        }

        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (evidencePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Studio two-Agent public evidence cannot be written beneath the private temp root.");
        }

        if (File.Exists(evidencePath)
            || File.Exists(Path.Combine(Path.GetDirectoryName(evidencePath)!, "evidence.json")))
        {
            throw new InvalidDataException(
                "Studio two-Agent evidence refuses to overwrite an existing result.");
        }

        return new StudioTwoAgentGate(
            prerequisites,
            release,
            handoffPath,
            RequiredText(postgres, PostgresConnectionStringVariable),
            evidencePath,
            canonicalServiceScope);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertDistinctStationBoundary(
        StudioTwoAgentExternalProcessHarness agents)
    {
        Assert.NotEqual(agents.EntryAgent.AgentId, agents.DownstreamAgent.AgentId);
        Assert.NotEqual(agents.EntryAgent.StationId, agents.DownstreamAgent.StationId);
        Assert.NotEqual(
            agents.EntryAgent.StationSystemId,
            agents.DownstreamAgent.StationSystemId);
        Assert.NotEqual(
            agents.EntryAgent.PackageContentSha256,
            agents.DownstreamAgent.PackageContentSha256);
        Assert.Equal(2, agents.Deployments.Count);
        Assert.Equal(4, agents.Credentials.Count);
        Assert.Equal(4, agents.Credentials.Select(credential => credential.TokenSha256)
            .Distinct(StringComparer.Ordinal).Count());
    }

    private static RealCoordinatorDeployment ToRealDeployment(
        StudioCoordinatorDeployment deployment) => new(
        deployment.ProjectId,
        deployment.ApplicationId,
        deployment.StationSystemId,
        deployment.AgentId,
        deployment.StationId);

    [SupportedOSPlatform("windows")]
    private static RealCoordinatorProcessCredentials CreateCoordinatorCredentials(
        StudioTwoAgentExternalProcessHarness agents,
        RealCoordinatorDeployment firstDeployment,
        RealCoordinatorDeployment secondDeployment)
    {
        var random = RealCoordinatorProcessCredentials.CreateRandom(
            firstDeployment,
            secondDeployment);
        var operatorCredential = agents.OperatorCredential;
        var safetyCredential = agents.SafetyCredential;
        var firstStation = agents.Credentials.Single(credential =>
            string.Equals(credential.ActorId, firstDeployment.AgentId, StringComparison.Ordinal));
        var secondStation = agents.Credentials.Single(credential =>
            string.Equals(credential.ActorId, secondDeployment.AgentId, StringComparison.Ordinal));
        return new RealCoordinatorProcessCredentials(
            random.Engineering,
            new RealCoordinatorCredential(
                operatorCredential.CredentialId,
                operatorCredential.ActorId,
                operatorCredential.Token),
            new RealCoordinatorCredential(
                safetyCredential.CredentialId,
                safetyCredential.ActorId,
                safetyCredential.Token),
            new RealCoordinatorStationAgentCredential(
                firstStation.CredentialId,
                firstDeployment.AgentId,
                firstDeployment.StationId,
                firstStation.Token),
            new RealCoordinatorStationAgentCredential(
                secondStation.CredentialId,
                secondDeployment.AgentId,
                secondDeployment.StationId,
                secondStation.Token));
    }

    private static string ResolveStagedExecutable(
        string preferredRoot,
        string fallbackRoot,
        string fileName)
    {
        var preferred = Path.Combine(preferredRoot, fileName);
        return File.Exists(preferred)
            ? Path.GetFullPath(preferred)
            : RequiredDirectFile(fallbackRoot, fileName);
    }

    private static int AllocateStudioLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RegisterStudioSlotsAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioLogicalClock clock,
        CancellationToken cancellationToken)
    {
        foreach (var station in new[] { fixture.EntryStation, fixture.DownstreamStation })
        {
            _ = await StudioPostJsonAsync(
                client,
                "api/slot-occupancies",
                new
                {
                    lineId = fixture.ProductionLineDefinitionId,
                    stationSystemId = station.StationSystemId,
                    slotId = station.SlotId,
                    occurredAtUtc = clock.Next()
                },
                HttpStatusCode.Created,
                cancellationToken);
        }
    }

    private static async Task<StudioRunIdentity> RegisterArriveAndStartAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioAgentEndpoint entryAgent,
        string identityValue,
        bool reserveSlot,
        StudioLogicalClock clock,
        CancellationToken cancellationToken)
    {
        var unit = new StudioRunIdentity(Guid.NewGuid(), Guid.NewGuid(), identityValue);
        _ = await StudioPostJsonAsync(
            client,
            "api/production-units",
            new
            {
                productionUnitId = unit.UnitId,
                productModelId = fixture.ProductModelId,
                identityKey = fixture.IdentityInputKey,
                identityValue,
                lotId = (string?)null,
                occurredAtUtc = clock.Next()
            },
            HttpStatusCode.Created,
            cancellationToken);
        _ = await StudioPostJsonAsync(
            client,
            $"api/production-units/{unit.UnitId:D}/arrivals",
            new
            {
                projectId = fixture.ProjectId,
                applicationId = fixture.ApplicationId,
                projectSnapshotId = fixture.ProjectSnapshotId,
                packageContentSha256 = fixture.EntryStation.PackageContentSha256,
                stationId = entryAgent.StationId,
                lineId = fixture.ProductionLineDefinitionId,
                stationSystemId = fixture.EntryStation.StationSystemId,
                occurredAtUtc = clock.Next()
            },
            HttpStatusCode.OK,
            cancellationToken);
        if (reserveSlot)
        {
            await ReserveLoadStartStudioSlotAsync(
                client,
                fixture,
                unit,
                fixture.EntryStation,
                clock,
                cancellationToken);
            await SubmitStudioRunAsync(client, fixture, unit, cancellationToken);
        }

        return unit;
    }

    private static async Task SubmitStudioRunAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioRunIdentity unit,
        CancellationToken cancellationToken)
    {
        var response = await StudioPostJsonAsync(
            client,
            "api/production-runs",
            new
            {
                projectId = fixture.ProjectId,
                projectSnapshotId = fixture.ProjectSnapshotId,
                productionRunId = unit.RunId,
                productionUnitId = unit.UnitId
            },
            HttpStatusCode.Accepted,
            cancellationToken);
        Assert.Equal(
            unit.RunId.ToString("D", CultureInfo.InvariantCulture),
            StudioApiRequiredString(response.Root, "productionRunId"));
    }

    private static async Task ReserveLoadStartStudioSlotAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioRunIdentity unit,
        StudioStationFixture station,
        StudioLogicalClock clock,
        CancellationToken cancellationToken)
    {
        foreach (var command in new[] { "Reserve", "Load", "Start" })
        {
            await StudioSlotCommandAsync(
                client,
                fixture,
                unit,
                station,
                command,
                destination: null,
                clock,
                cancellationToken);
        }
    }

    private static async Task<DateTimeOffset> CompleteAndUnloadStudioSlotAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioRunIdentity unit,
        StudioStationFixture station,
        string destinationStationSystemId,
        StudioLogicalClock clock,
        CancellationToken cancellationToken)
    {
        _ = await StudioSlotCommandAsync(
            client,
            fixture,
            unit,
            station,
            "Complete",
            destination: null,
            clock,
            cancellationToken);
        return await StudioSlotCommandAsync(
            client,
            fixture,
            unit,
            station,
            "Unload",
            new
            {
                kind = "StationQueue",
                lineId = fixture.ProductionLineDefinitionId,
                stationSystemId = destinationStationSystemId,
                slotId = (string?)null,
                carrierId = (string?)null,
                carrierPositionId = (string?)null
            },
            clock,
            cancellationToken);
    }

    private static async Task MoveBetweenStudioSlotsAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioRunIdentity unit,
        StudioStationFixture source,
        StudioStationFixture destination,
        StudioLogicalClock clock,
        CancellationToken cancellationToken)
    {
        await CompleteAndUnloadStudioSlotAsync(
            client,
            fixture,
            unit,
            source,
            destination.StationSystemId,
            clock,
            cancellationToken);
        await ReserveLoadStartStudioSlotAsync(
            client,
            fixture,
            unit,
            destination,
            clock,
            cancellationToken);
    }

    private static async Task<DateTimeOffset> StudioSlotCommandAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        StudioRunIdentity unit,
        StudioStationFixture station,
        string command,
        object? destination,
        StudioLogicalClock clock,
        CancellationToken cancellationToken)
    {
        var occurredAtUtc = clock.Next();
        _ = await StudioPostJsonAsync(
            client,
            $"api/slot-occupancies/{Uri.EscapeDataString(fixture.ProductionLineDefinitionId)}"
            + $"/{Uri.EscapeDataString(station.StationSystemId)}"
            + $"/{Uri.EscapeDataString(station.SlotId)}/commands/{command}",
            new
            {
                materialKind = "ProductionUnit",
                materialId = unit.UnitId,
                destination,
                reason = (string?)null,
                occurredAtUtc
            },
            HttpStatusCode.OK,
            cancellationToken);
        return occurredAtUtc;
    }

    private static Task<StudioHttpJson> WaitForOperationAsync(
        HttpClient client,
        Guid runId,
        string operationId,
        string executionStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        WaitForStudioRunAsync(
            client,
            runId,
            root => StudioApiRequiredArray(root, "operations").Any(operation =>
                string.Equals(
                    StudioApiRequiredString(operation, "operationId"),
                    operationId,
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(operation, "executionStatus"),
                    executionStatus,
                    StringComparison.Ordinal)),
            timeout,
        cancellationToken);

    private static async Task<StudioFinalUnloadEvidence>
        AssertStudioFinalUnloadLifecycleAsync(
            HttpClient client,
            StudioProductionFixture fixture,
            StudioRunIdentity unit,
            StudioStationFixture station,
            DateTimeOffset unloadAtUtc,
            CancellationToken cancellationToken)
    {
        var lifecycle = await StudioGetJsonAsync(
            client,
            $"api/traceability/production-units/{unit.UnitId:D}/material-lifecycle",
            HttpStatusCode.OK,
            cancellationToken);
        RejectStudioPrivateJsonStrings(lifecycle.Root);
        Assert.Equal(unit.UnitId, StudioRequiredGuid(lifecycle.Root, "productionUnitId"));
        var location = Assert.Single(
            StudioApiRequiredArray(lifecycle.Root, "materialLocationTransitions"),
            transition =>
                StudioOptionalGuidEquals(transition, "productionRunId", unit.RunId)
                && StudioRequiredUtcTimestamp(transition, "occurredAtUtc") == unloadAtUtc
                && string.Equals(
                    StudioApiRequiredString(
                        StudioRequiredObject(transition, "source"),
                        "kind"),
                    "Slot",
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(
                        StudioRequiredObject(transition, "source"),
                        "slotId"),
                    station.SlotId,
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(
                        StudioRequiredObject(transition, "destination"),
                        "kind"),
                    "StationQueue",
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(
                        StudioRequiredObject(transition, "destination"),
                        "stationSystemId"),
                    station.StationSystemId,
                    StringComparison.Ordinal));
        var slot = Assert.Single(
            StudioApiRequiredArray(lifecycle.Root, "slotOccupancyTransitions"),
            transition =>
                StudioOptionalGuidEquals(transition, "productionRunId", unit.RunId)
                && StudioRequiredUtcTimestamp(transition, "occurredAtUtc") == unloadAtUtc
                && string.Equals(
                    StudioApiRequiredString(transition, "stationSystemId"),
                    station.StationSystemId,
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(transition, "slotId"),
                    station.SlotId,
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(transition, "previousStatus"),
                    "Occupied",
                    StringComparison.Ordinal)
                && string.Equals(
                    StudioApiRequiredString(transition, "currentStatus"),
                    "Available",
                    StringComparison.Ordinal));
        return new StudioFinalUnloadEvidence(
            unit.UnitId,
            unit.RunId,
            unloadAtUtc,
            StudioRequiredGuid(location, "evidenceId"),
            StudioRequiredGuid(slot, "evidenceId"),
            lifecycle.SizeBytes,
            lifecycle.Sha256);
    }

    private static Task<StudioHttpJson> WaitForTerminalRunAsync(
        HttpClient client,
        Guid runId,
        string executionStatus,
        string judgement,
        CancellationToken cancellationToken) =>
        WaitForStudioRunAsync(
            client,
            runId,
            root => StudioRequiredBoolean(root, "isTerminal")
                    && string.Equals(
                        StudioApiRequiredString(root, "executionStatus"),
                        executionStatus,
                        StringComparison.Ordinal)
                    && string.Equals(
                        StudioApiRequiredString(root, "judgement"),
                        judgement,
                        StringComparison.Ordinal),
            TimeSpan.FromMinutes(2),
            cancellationToken);

    private static async Task<StudioHttpJson> WaitForStudioRunAsync(
        HttpClient client,
        Guid runId,
        Func<JsonElement, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        StudioHttpJson? latest = null;
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await StudioGetJsonAsync(
                client,
                $"api/production-runs/{runId:D}",
                HttpStatusCode.OK,
                cancellationToken);
            if (predicate(latest.Root))
            {
                return latest;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException(
            $"Production Run {runId:D} did not reach the expected state. Latest response SHA-256: {latest?.Sha256 ?? "none"}.");
    }

    private static async Task<StudioHttpJson> WaitForParallelLineStateAsync(
        HttpClient client,
        StudioProductionFixture fixture,
        Guid runA,
        Guid runB,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        StudioHttpJson? latest = null;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(45))
        {
            latest = await StudioGetJsonAsync(
                client,
                $"api/operations/lines/{Uri.EscapeDataString(fixture.ProductionLineDefinitionId)}/state",
                HttpStatusCode.OK,
                cancellationToken);
            var stations = StudioApiRequiredArray(latest.Root, "stations");
            var slots = StudioApiRequiredArray(latest.Root, "slots");
            var entryActive = stations.Any(station =>
                string.Equals(
                    StudioApiRequiredString(station, "stationSystemId"),
                    fixture.EntryStation.StationSystemId,
                    StringComparison.Ordinal)
                && StudioApiRequiredArray(station, "activeOperations").Any(operation =>
                    StudioRequiredGuid(operation, "productionRunId") == runB));
            var downstreamActive = stations.Any(station =>
                string.Equals(
                    StudioApiRequiredString(station, "stationSystemId"),
                    fixture.DownstreamStation.StationSystemId,
                    StringComparison.Ordinal)
                && StudioApiRequiredArray(station, "activeOperations").Any(operation =>
                    StudioRequiredGuid(operation, "productionRunId") == runA));
            var bothSlotsRunning = slots.Count(slot =>
                string.Equals(
                    StudioApiRequiredString(slot, "status"),
                    "Running",
                    StringComparison.Ordinal)
                && (string.Equals(
                        StudioApiRequiredString(slot, "slotId"),
                        fixture.EntryStation.SlotId,
                        StringComparison.Ordinal)
                    || string.Equals(
                        StudioApiRequiredString(slot, "slotId"),
                        fixture.DownstreamStation.SlotId,
                        StringComparison.Ordinal))) == 2;
            if (entryActive && downstreamActive && bothSlotsRunning)
            {
                return latest;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException(
            $"Line projection did not show two concurrent Stations. Latest SHA-256: {latest?.Sha256 ?? "none"}.");
    }

    private static StudioParallelEvidence AssertParallelResourceLeases(
        JsonElement lineState,
        StudioProductionFixture fixture,
        Guid runA,
        Guid runB)
    {
        var stations = StudioApiRequiredArray(lineState, "stations");
        var entryOperation = StudioApiRequiredArray(stations.Single(station =>
                string.Equals(
                    StudioApiRequiredString(station, "stationSystemId"),
                    fixture.EntryStation.StationSystemId,
                    StringComparison.Ordinal)),
            "activeOperations").Single(operation =>
            StudioRequiredGuid(operation, "productionRunId") == runB);
        var downstreamOperation = StudioApiRequiredArray(stations.Single(station =>
                string.Equals(
                    StudioApiRequiredString(station, "stationSystemId"),
                    fixture.DownstreamStation.StationSystemId,
                    StringComparison.Ordinal)),
            "activeOperations").Single(operation =>
            StudioRequiredGuid(operation, "productionRunId") == runA);
        var entryResources = ReadStudioResourceLeases(entryOperation);
        var downstreamResources = ReadStudioResourceLeases(downstreamOperation);
        Assert.NotEmpty(entryResources);
        Assert.NotEmpty(downstreamResources);
        Assert.All(entryResources.Concat(downstreamResources), resource =>
            Assert.True(resource.FencingToken > 0));
        var resourceIdentitiesDisjoint = !entryResources.Select(resource => resource.Identity)
            .Intersect(
                downstreamResources.Select(resource => resource.Identity),
                StringComparer.Ordinal).Any();
        var observed = entryResources.Count > 0 && downstreamResources.Count > 0;
        Assert.True(resourceIdentitiesDisjoint);
        Assert.True(observed);
        return new StudioParallelEvidence(
            entryResources.Count,
            downstreamResources.Count,
            entryResources.Select(resource => StudioSha256Text(resource.Identity)).ToArray(),
            downstreamResources.Select(resource => StudioSha256Text(resource.Identity)).ToArray(),
            entryResources.Select(resource => resource.FencingToken).ToArray(),
            downstreamResources.Select(resource => resource.FencingToken).ToArray(),
            observed,
            resourceIdentitiesDisjoint);
    }

    private static List<StudioResourceLeaseEvidence> ReadStudioResourceLeases(
        JsonElement operation) => StudioApiRequiredArray(operation, "resources")
        .Select(resource => new StudioResourceLeaseEvidence(
            $"{StudioApiRequiredString(resource, "kind")}:{StudioApiRequiredString(resource, "resourceId")}",
            StudioApiRequiredInt64(resource, "fencingToken")))
        .ToList();

    [SupportedOSPlatform("windows")]
    private static async Task<List<StudioVendorProcessStart>> WaitForVendorStartsForAgentAsync(
        StudioTwoAgentExternalProcessHarness agents,
        int agentProcessId,
        int count,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var matches = agents.ReadVendorProcessStarts()
                .Where(entry => entry.AncestorProcessIds.Contains(agentProcessId))
                .ToList();
            if (matches.Count >= count)
            {
                return matches;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            $"Vendor process observer did not bind {count} starts to staged Agent PID {agentProcessId}.");
    }

    private static List<StudioVendorProcessStart> StudioRootVendorInvocations(
        List<StudioVendorProcessStart> starts)
    {
        var observedProcessIds = starts.Select(start => start.ProcessId).ToHashSet();
        return starts.Where(start => !start.AncestorProcessIds.Any(
                observedProcessIds.Contains))
            .OrderBy(start => start.Sequence)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static async Task<StudioRecoveryVendorTree> WaitForStudioRecoveryVendorTreeAsync(
        StudioTwoAgentExternalProcessHarness agents,
        int downstreamAgentProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var allStarts = agents.ReadVendorProcessStarts()
                .Where(start => start.AncestorProcessIds.Contains(downstreamAgentProcessId))
                .OrderBy(start => start.Sequence)
                .ToList();
            var roots = StudioRootVendorInvocations(allStarts);
            if (roots.Count >= 3)
            {
                var recoveryRoot = roots[2];
                var children = allStarts.Where(start =>
                        start.ProcessId != recoveryRoot.ProcessId
                        && start.AncestorProcessIds.Contains(recoveryRoot.ProcessId))
                    .ToList();
                if (roots.Count == 3 && allStarts.Count == 4 && children.Count == 1)
                {
                    return new StudioRecoveryVendorTree(
                        allStarts,
                        roots,
                        recoveryRoot,
                        children);
                }

                if (roots.Count > 3 || children.Count > 1 || allStarts.Count > 4)
                {
                    throw new InvalidDataException(
                        "Recovery vendor observation contains a duplicate root invocation or more than one child.");
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            "Recovery vendor observer did not record exactly three root invocations and one child process.");
    }

    private static async Task<StudioPostgresEvidence> WaitForStudioPostgresResultCountAsync(
        StudioPostgresSchemaLease postgres,
        long expectedResultCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        StudioPostgresEvidence? latest = null;
        while (stopwatch.Elapsed < timeout)
        {
            latest = await postgres.ReadEvidenceAsync(cancellationToken);
            if (latest.StationJobResultCount == expectedResultCount)
            {
                return latest;
            }

            if (latest.StationJobResultCount > expectedResultCount)
            {
                throw new InvalidDataException(
                    "PostgreSQL Station result inbox contains a duplicate completion.");
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            $"PostgreSQL Station result inbox did not persist {expectedResultCount} results; latest={latest?.StationJobResultCount.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForStudioProcessExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited
                    || !string.Equals(
                        process.ProcessName,
                        "OpenLineOps.VendorTestHelper",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            $"Vendor helper PID {processId} remained alive after its Agent completed the interrupted job.");
    }

    private static async Task<StudioHttpJson> WaitForStudioTraceAsync(
        HttpClient client,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(45))
        {
            using var response = await client.GetAsync(
                $"api/traceability/records/{runId:D}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await StudioReadJsonAsync(response, cancellationToken);
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw await StudioUnexpectedResponseAsync(
                    response,
                    HttpStatusCode.OK,
                    cancellationToken);
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"Trace for Production Run {runId:D} was not projected.");
    }

    private static async Task<List<StudioArtifactEvidence>> VerifyStudioVendorArtifactsAsync(
        HttpClient client,
        JsonElement trace,
        string vendorOperationId,
        CancellationToken cancellationToken)
    {
        var operation = StudioApiRequiredArray(trace, "operations").Single(candidate =>
            string.Equals(
                StudioApiRequiredString(candidate, "operationId"),
                vendorOperationId,
                StringComparison.Ordinal)
            && string.Equals(
                StudioApiRequiredString(candidate, "executionStatus"),
                "Completed",
                StringComparison.Ordinal)
            && string.Equals(
                StudioApiRequiredString(candidate, "judgement"),
                "Passed",
                StringComparison.Ordinal));
        var artifacts = StudioApiRequiredArray(operation, "artifacts");
        var requiredNames = new[]
        {
            "measurements.csv",
            "inspection.png",
            "report.pdf",
            "stdout.log",
            "stderr.log"
        };
        var evidence = new List<StudioArtifactEvidence>();
        foreach (var name in requiredNames)
        {
            var artifact = artifacts.Single(candidate =>
                string.Equals(
                    StudioApiRequiredString(candidate, "name"),
                    name,
                    StringComparison.Ordinal));
            var storageKey = StudioApiRequiredString(artifact, "storageKey");
            var expectedHash = StudioApiRequiredSha256(artifact, "sha256");
            var expectedSize = StudioApiRequiredInt64(artifact, "sizeBytes");
            var encodedKey = string.Join(
                '/',
                storageKey.Split('/').Select(Uri.EscapeDataString));
            using var response = await client.GetAsync(
                $"api/traceability/artifacts/{encodedKey}",
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw await StudioUnexpectedResponseAsync(
                    response,
                    HttpStatusCode.OK,
                    cancellationToken);
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(expectedSize, content.LongLength);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(content));
            Assert.Equal(expectedHash, actualHash);
            evidence.Add(new StudioArtifactEvidence(
                name,
                StudioSha256Text(storageKey),
                expectedSize,
                actualHash,
                response.Content.Headers.ContentType?.MediaType,
                HashRecomputedFromCoordinatorDownload: true));
        }

        return evidence;
    }

    private static async Task<StudioHttpJson> StudioPostJsonAsync(
        HttpClient client,
        string path,
        object body,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            path,
            body,
            StudioHttpJsonOptions,
            cancellationToken);
        if (response.StatusCode != expectedStatus)
        {
            throw await StudioUnexpectedResponseAsync(
                response,
                expectedStatus,
                cancellationToken);
        }

        return await StudioReadJsonAsync(response, cancellationToken);
    }

    private static async Task<StudioHttpJson> StudioGetJsonAsync(
        HttpClient client,
        string path,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode != expectedStatus)
        {
            throw await StudioUnexpectedResponseAsync(
                response,
                expectedStatus,
                cancellationToken);
        }

        return await StudioReadJsonAsync(response, cancellationToken);
    }

    private static async Task<StudioHttpJson> StudioReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
        StudioRejectDuplicateProperties(document.RootElement, "formal API response");
        return new StudioHttpJson(
            document.RootElement.Clone(),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.LongLength,
            bytes);
    }

    private static async Task<InvalidOperationException> StudioUnexpectedResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new InvalidOperationException(
            $"Formal API expected HTTP {(int)expected} but returned {(int)response.StatusCode}; response SHA-256={Convert.ToHexStringLower(SHA256.HashData(bytes))}.");
    }

    private static JsonElement[] StudioApiRequiredArray(JsonElement element, string propertyName)
    {
        var property = StudioRequiredProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(item => item.Clone()).ToArray()
            : throw new InvalidDataException($"{propertyName} must be an array.");
    }

    private static string StudioApiRequiredString(JsonElement element, string propertyName) =>
        StudioRequiredProperty(element, propertyName).GetString()
        ?? throw new InvalidDataException($"{propertyName} must be a string.");

    private static Guid StudioRequiredGuid(JsonElement element, string propertyName) =>
        StudioRequiredProperty(element, propertyName).TryGetGuid(out var value)
            ? value
            : throw new InvalidDataException($"{propertyName} must be a UUID.");

    private static bool StudioOptionalGuidEquals(
        JsonElement element,
        string propertyName,
        Guid expected)
    {
        var property = StudioRequiredProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String
               && property.TryGetGuid(out var value)
               && value == expected;
    }

    private static bool StudioRequiredBoolean(JsonElement element, string propertyName)
    {
        var property = StudioRequiredProperty(element, propertyName);
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new InvalidDataException($"{propertyName} must be a boolean.");
    }

    private static int StudioRequiredInt32(JsonElement element, string propertyName) =>
        StudioRequiredProperty(element, propertyName).TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"{propertyName} must be an Int32.");

    private static long StudioApiRequiredInt64(JsonElement element, string propertyName) =>
        StudioRequiredProperty(element, propertyName).TryGetInt64(out var value)
            ? value
            : throw new InvalidDataException($"{propertyName} must be an Int64.");

    private static string StudioApiRequiredSha256(JsonElement element, string propertyName)
    {
        var value = StudioApiRequiredString(element, propertyName);
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidDataException($"{propertyName} must be a lowercase SHA-256.");
    }

    private static string StudioSha256Text(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static StudioRawApiProof CreateStudioRawApiProof(
        string name,
        StudioHttpJson response,
        params string[] forbiddenValues)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Raw API proof name must be canonical text.");
        }

        if (response.Bytes.LongLength != response.SizeBytes
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(response.Bytes)),
                response.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Raw API proof bytes differ from their response hash.");
        }

        foreach (var value in forbiddenValues.Where(static value =>
                     !string.IsNullOrWhiteSpace(value)))
        {
            if (response.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0)
            {
                throw new InvalidDataException(
                    "A raw API proof contains private execution state or a credential.");
            }
        }

        RejectStudioPrivateJsonStrings(response.Root);
        return new StudioRawApiProof(
            name,
            response.SizeBytes,
            response.Sha256,
            ValidatedFromPrivateResponseBytes: true);
    }

    private static void RejectStudioPrivateJsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                RejectStudioPrivateJsonStrings(property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectStudioPrivateJsonStrings(item);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = element.GetString() ?? string.Empty;
        var containsWindowsDrivePath = value.Length >= 3
                                       && char.IsAsciiLetter(value[0])
                                       && value[1] == ':'
                                       && value[2] is '\\' or '/';
        var containsCredentialUri = Uri.TryCreate(
                                        value,
                                        UriKind.Absolute,
                                        out var absoluteUri)
                                    && !string.IsNullOrEmpty(absoluteUri.UserInfo);
        if (containsWindowsDrivePath
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("../", StringComparison.Ordinal)
            || value.Contains("..\\", StringComparison.Ordinal)
            || containsCredentialUri)
        {
            throw new InvalidDataException(
                "A raw API proof contains an absolute path, a credential-bearing URI, or authorization material.");
        }
    }

    private static void WriteStudioTwoAgentEvidence(
        string manifestPath,
        StudioProductionFixture fixture,
        StagedPrerequisites prerequisites,
        StudioReleaseAttestation release,
        RealCoordinatorProcessIdentity[] coordinatorStarts,
        string agentExecutableSha256,
        int entryAgentPid,
        int downstreamAgentPid,
        StudioAgentEndpoint entryAgent,
        StudioAgentEndpoint downstreamAgent,
        string entryTokenHash,
        string downstreamTokenHash,
        string serviceAccountName,
        string serviceAccountSid,
        string entryServiceSidSha256,
        string downstreamServiceSidSha256,
        StudioMaterialArrivalIpcIsolationEvidence materialArrivalIpcIsolation,
        bool entryAgentNonAdministrative,
        bool downstreamAgentNonAdministrative,
        bool onlyApiWasRestarted,
        bool persistentStateRestored,
        string runBeforeRestartSha256,
        string restoredRunSha256,
        StudioHttpJson terminalA,
        StudioHttpJson terminalB,
        StudioHttpJson terminalBAfterRestart,
        StudioHttpJson traceA,
        StudioHttpJson traceB,
        StudioHttpJson parallelLineState,
        StudioParallelEvidence parallel,
        StudioHttpJson finalLineState,
        StudioHttpJson activeRuns,
        List<StudioVendorProcessStart> vendorStarts,
        StudioVendorProcessLedgerEvidence vendorLedger,
        IReadOnlyList<StudioArtifactEvidence> artifacts,
        IReadOnlyList<StudioRawApiProof> rawApiProofs,
        IReadOnlyList<StudioFinalUnloadEvidence> finalUnloadEvidence,
        IReadOnlyList<StudioPostgresFinalUnloadEvidence> postgresFinalUnloadEvidence,
        StudioRecoveryEvidence recovery,
        IReadOnlyDictionary<string, (uint Messages, uint Consumers)> queues,
        StudioPostgresEvidence postgres,
        (int EntryExitCode, int DownstreamExitCode) agentExitCodes,
        StudioCleanupEvidence cleanup,
        IReadOnlyCollection<string> forbiddenPublicValues)
    {
        var evidenceDirectory = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "evidence.json");
        var rawLedgerBytes = Convert.FromBase64String(vendorLedger.Base64);
        if (rawLedgerBytes.LongLength != vendorLedger.SizeBytes
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(rawLedgerBytes)),
                vendorLedger.Sha256,
                StringComparison.Ordinal)
            || vendorLedger.Entries.Count != vendorStarts.Count)
        {
            throw new InvalidDataException(
                "Frozen vendor process ledger evidence is not self-consistent.");
        }

        if (rawApiProofs.Select(static proof => proof.Name)
            .Distinct(StringComparer.Ordinal).Count() != rawApiProofs.Count)
        {
            throw new InvalidDataException("Raw API proof names must be unique.");
        }

        var queueSnapshotBytes = JsonSerializer.SerializeToUtf8Bytes(
            queues.Values
                .Select(static queue => new
                {
                    queue.Messages,
                    queue.Consumers
                })
                .OrderBy(static queue => queue.Messages)
                .ThenBy(static queue => queue.Consumers)
                .ToArray(),
            StudioEvidenceJsonOptions);

        var document = new
        {
            schema = "openlineops.studio-two-agent-production-e2e",
            schemaVersion = 1,
            verifiedAtUtc = DateTimeOffset.UtcNow,
            sourceStudioClosure = new
            {
                productionEvidenceManifestSha256 = fixture.EvidenceManifestSha256,
                productionSummarySha256 = fixture.SummarySha256,
                fixture.SigningPublicKeySha256,
                entryPackageContentSha256 = fixture.EntryStation.PackageContentSha256,
                downstreamPackageContentSha256 = fixture.DownstreamStation.PackageContentSha256,
                applicationPortability = new
                {
                    fixture.ApplicationPortability.SourceProjectId,
                    fixture.ApplicationPortability.TargetProjectId,
                    fixture.ApplicationPortability.ApplicationId,
                    fixture.ApplicationPortability.FileCount,
                    fixture.ApplicationPortability.TotalSizeBytes,
                    fixture.ApplicationPortability.SourceBeforeCopyTreeSha256,
                    fixture.ApplicationPortability.CopiedTreeSha256,
                    fixture.ApplicationPortability.AfterImportTreeSha256,
                    fixture.ApplicationPortability.AfterPublishTreeSha256,
                    fixture.ApplicationPortability.AfterExecutionTreeSha256,
                    fixture.ApplicationPortability.SourceAfterExecutionTreeSha256,
                    fixture.ApplicationPortability.Unchanged
                },
                immutableRunTrace = fixture.ImmutableRunTrace
            },
            release = new
            {
                release.Version,
                release.ManifestSha256,
                release.Agent,
                release.Api,
                release.SamplePlugin
            },
            stagedExecutables = new
            {
                api = new
                {
                    fileName = "OpenLineOps.Api.exe",
                    sha256 = coordinatorStarts[0].ExecutableSha256
                },
                agent = new
                {
                    fileName = "OpenLineOps.Agent.exe",
                    sha256 = agentExecutableSha256
                }
            },
            coordinator = new
            {
                processIds = coordinatorStarts.Select(start => start.ProcessId).ToArray(),
                startOrdinals = coordinatorStarts.Select(start => start.StartOrdinal).ToArray(),
                environmentSha256 = coordinatorStarts[0].EnvironmentSha256,
                onlyApiWasRestarted,
                persistentStateRestored,
                runBeforeRestartSha256,
                restoredRunSha256
            },
            agents = new[]
            {
                new
                {
                    role = "entry",
                    entryAgent.AgentId,
                    entryAgent.StationId,
                    entryAgent.StationSystemId,
                    processId = entryAgentPid,
                    credentialTokenSha256 = entryTokenHash,
                    nonAdministrativeToken = entryAgentNonAdministrative,
                    exitCode = agentExitCodes.EntryExitCode
                },
                new
                {
                    role = "downstream",
                    downstreamAgent.AgentId,
                    downstreamAgent.StationId,
                    downstreamAgent.StationSystemId,
                    processId = downstreamAgentPid,
                    credentialTokenSha256 = downstreamTokenHash,
                    nonAdministrativeToken = downstreamAgentNonAdministrative,
                    exitCode = agentExitCodes.DownstreamExitCode
                }
            },
            windowsIdentity = new
            {
                sharedLocalServiceAccount = true,
                serviceAccountName,
                serviceAccountSid,
                entryServiceSidSha256,
                downstreamServiceSidSha256,
                distinctRestrictedServiceSids = !string.Equals(
                    entryServiceSidSha256,
                    downstreamServiceSidSha256,
                    StringComparison.Ordinal),
                entryServiceTokenConnected =
                    materialArrivalIpcIsolation.EntryServiceTokenConnected,
                entryPipeExactAclVerified =
                    materialArrivalIpcIsolation.EntryPipeExactAclVerified,
                downstreamServiceTokenExplicitAccessDenied =
                    materialArrivalIpcIsolation.DownstreamServiceTokenExplicitAccessDenied,
                bothServicesRunningOnOriginalPids =
                    materialArrivalIpcIsolation.BothServicesRunningOnOriginalPids
            },
            broker = new
            {
                scheme = prerequisites.BrokerUri.Scheme,
                prerequisites.BrokerUri.Host,
                prerequisites.BrokerUri.Port,
                tls = IsTls(prerequisites.BrokerUri),
                queues = queues.Values.Select(queue => new
                {
                    queue.Messages,
                    queue.Consumers
                }).OrderBy(queue => queue.Messages)
                    .ThenBy(queue => queue.Consumers)
                    .ToArray(),
                snapshotSizeBytes = queueSnapshotBytes.LongLength,
                snapshotSha256 = Convert.ToHexStringLower(
                    SHA256.HashData(queueSnapshotBytes)),
                snapshotValidatedFromPrivateBrokerState = true,
                allQueuesDrained = queues.All(queue => queue.Value.Messages == 0)
            },
            parallelExecution = new
            {
                observed = parallel.Observed,
                lineStateSha256 = parallelLineState.Sha256,
                parallel.EntryResourceCount,
                parallel.DownstreamResourceCount,
                parallel.EntryResourceIdentityHashes,
                parallel.DownstreamResourceIdentityHashes,
                parallel.EntryFencingTokens,
                parallel.DownstreamFencingTokens,
                resourceIdentitiesDisjoint = parallel.ResourceIdentitiesDisjoint
            },
            runs = new[]
            {
                new
                {
                    productionRunId = StudioApiRequiredString(terminalA.Root, "productionRunId"),
                    productionUnitId = StudioApiRequiredString(terminalA.Root, "productionUnitId"),
                    executionStatus = StudioApiRequiredString(terminalA.Root, "executionStatus"),
                    judgement = StudioApiRequiredString(terminalA.Root, "judgement"),
                    responseSha256 = terminalA.Sha256,
                    traceSha256 = traceA.Sha256,
                    responseAfterRestartSha256 = (string?)null
                },
                new
                {
                    productionRunId = StudioApiRequiredString(terminalB.Root, "productionRunId"),
                    productionUnitId = StudioApiRequiredString(terminalB.Root, "productionUnitId"),
                    executionStatus = StudioApiRequiredString(terminalB.Root, "executionStatus"),
                    judgement = StudioApiRequiredString(terminalB.Root, "judgement"),
                    responseSha256 = terminalB.Sha256,
                    traceSha256 = traceB.Sha256,
                    responseAfterRestartSha256 = (string?)terminalBAfterRestart.Sha256
                }
            },
            vendorExecution = new
            {
                executableName = "OpenLineOps.VendorTestHelper.exe",
                boundStartCount = vendorStarts.Count,
                uniqueProcessIds = vendorStarts.Select(start => start.ProcessId).Distinct().Count(),
                ledgerSizeBytes = vendorLedger.SizeBytes,
                ledgerSha256 = vendorLedger.Sha256,
                starts = vendorStarts.Select(start => new
                {
                    start.Sequence,
                    start.ProcessId,
                    start.ParentProcessId,
                    start.StartedAtUtc,
                    ancestorDepth = start.AncestorProcessIds.Count,
                    boundToDownstreamAgent = start.AncestorProcessIds.Contains(downstreamAgentPid),
                    boundToEntryAgent = start.AncestorProcessIds.Contains(entryAgentPid)
                }).ToArray(),
                rootInvocationCount = StudioRootVendorInvocations(vendorStarts).Count,
                noAutomaticReplayAfterActiveCoordinatorCrash = recovery.NoAutomaticReplay
            },
            artifacts,
            apiResponseProofs = rawApiProofs,
            finalUnloadEvidence,
            postgresFinalUnloadEvidence,
            recovery,
            persistence = postgres,
            projections = new
            {
                activeRunsSha256 = activeRuns.Sha256,
                finalLineStateSha256 = finalLineState.Sha256,
                finalActiveRunCount = StudioRequiredInt32(finalLineState.Root, "activeRunCount")
            },
            cleanup
        };
        var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            StudioEvidenceJsonOptions);
        AssertStudioPublicEvidenceSafe(evidenceBytes, forbiddenPublicValues);
        StudioAtomicCreate(evidencePath, evidenceBytes);
        var manifest = new
        {
            schema = "openlineops.studio-two-agent-evidence-manifest",
            schemaVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            files = new[]
            {
                new
                {
                    relativePath = "evidence.json",
                    sizeBytes = evidenceBytes.LongLength,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(evidenceBytes))
                }
            }
        };
        StudioAtomicCreate(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, StudioEvidenceJsonOptions));
    }

    private static void AssertStudioPublicEvidenceSafe(
        byte[] bytes,
        IReadOnlyCollection<string> forbiddenValues)
    {
        foreach (var value in forbiddenValues.Where(static value =>
                     !string.IsNullOrWhiteSpace(value)))
        {
            if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0)
            {
                throw new InvalidDataException(
                    "Public Studio evidence contains private execution state or a credential.");
            }
        }

        using var document = JsonDocument.Parse(bytes);
        StudioRejectDuplicateProperties(document.RootElement, "public Studio evidence");
        RejectStudioForbiddenPublicEvidenceProperties(document.RootElement);
        RejectStudioPrivateJsonStrings(document.RootElement);
    }

    private static void RejectStudioForbiddenPublicEvidenceProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (name.Equals("logs", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("commandLine", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("resultPayload", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("textValue", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("message", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("failureReason", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("password", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("Base64", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Public Studio evidence contains forbidden property '{name}'.");
                }

                RejectStudioForbiddenPublicEvidenceProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            RejectStudioForbiddenPublicEvidenceProperties(item);
        }
    }

    private static void StudioAtomicCreate(string path, byte[] bytes)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record StudioTwoAgentGate(
        StagedPrerequisites Prerequisites,
        StudioReleaseAttestation Release,
        string HandoffPath,
        string PostgreSqlConnectionString,
        string EvidenceManifestPath,
        string ServiceScope);

    private sealed record StudioRunIdentity(Guid UnitId, Guid RunId, string IdentityValue);

    private sealed record StudioHttpJson(
        JsonElement Root,
        string Sha256,
        long SizeBytes,
        byte[] Bytes);

    private sealed record StudioRawApiProof(
        string Name,
        long SizeBytes,
        string Sha256,
        bool ValidatedFromPrivateResponseBytes);

    private sealed record StudioResourceLeaseEvidence(string Identity, long FencingToken);

    private sealed record StudioParallelEvidence(
        int EntryResourceCount,
        int DownstreamResourceCount,
        IReadOnlyList<string> EntryResourceIdentityHashes,
        IReadOnlyList<string> DownstreamResourceIdentityHashes,
        IReadOnlyList<long> EntryFencingTokens,
        IReadOnlyList<long> DownstreamFencingTokens,
        bool Observed,
        bool ResourceIdentitiesDisjoint);

    private sealed record StudioRecoveryVendorTree(
        List<StudioVendorProcessStart> AllStarts,
        List<StudioVendorProcessStart> RootInvocations,
        StudioVendorProcessStart RecoveryRoot,
        List<StudioVendorProcessStart> RecoveryChildren);

    private sealed record StudioRecoveryEvidence(
        Guid ProductionUnitId,
        Guid ProductionRunId,
        string OperationRunId,
        string DecisionId,
        int RootVendorProcessId,
        int ChildVendorProcessId,
        string RecoveryRequiredResponseSha256,
        string ReconcileResponseSha256,
        string TerminalResponseSha256,
        string TraceSha256,
        int OperationCount,
        int RootInvocationCountBeforeReconcile,
        int RootInvocationCountAfterReconcile,
        int ProcessStartCountBeforeReconcile,
        int ProcessStartCountAfterReconcile,
        long PersistedStationResultCountBeforeReconcile,
        long ReplayObservationWindowMilliseconds,
        int OperationCountAfterReplayWindow,
        long StationJobCountAfterReplayWindow,
        long StationJobResultCountAfterReplayWindow,
        bool AuditEntryPresent,
        bool NoAutomaticReplay);

    private sealed record StudioCleanupEvidence(
        bool PrivateStudioHandoffDeleted,
        bool PrivateStudioProjectDeleted,
        bool PrivateAgentHarnessDeleted,
        bool PostgresSchemaDropped,
        bool RabbitQueueCleanupAttempted,
        bool RabbitQueueCleanupSucceeded,
        int RabbitQueueCleanupCount);

    private sealed record StudioArtifactEvidence(
        string Name,
        string StorageKeySha256,
        long SizeBytes,
        string Sha256,
        string? MediaType,
        bool HashRecomputedFromCoordinatorDownload);

    private sealed record StudioFinalUnloadEvidence(
        Guid ProductionUnitId,
        Guid ProductionRunId,
        DateTimeOffset OccurredAtUtc,
        Guid LocationEvidenceId,
        Guid SlotEvidenceId,
        long LifecycleResponseSizeBytes,
        string LifecycleResponseSha256);

    private sealed record StudioPostgresFinalUnloadEvidence(
        Guid ProductionUnitId,
        Guid ProductionRunId,
        DateTimeOffset OccurredAtUtc,
        Guid LocationEvidenceId,
        Guid SlotEvidenceId,
        string LocationDocumentSha256,
        string SlotDocumentSha256,
        long SnapshotSizeBytes,
        string SnapshotSha256);

    private sealed record StudioPostgresEvidence(
        long ProductionRunCount,
        long TerminalEvidenceCount,
        long StationJobCount,
        long PublishedStationJobCount,
        long UnpublishedStationJobCount,
        long QuarantinedStationJobCount,
        long StationJobResultCount,
        long StationJobEventCount,
        long ActiveLeaseCount,
        long ProductionUnitCount,
        long AvailableSlotCount,
        long MaterialTimelineCount,
        long DistinctTimelineRunCount);

    private sealed class StudioLogicalClock
    {
        private long _milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public DateTimeOffset Next() =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _milliseconds));
    }

    private sealed class StudioPostgresSchemaLease : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private bool _disposed;

        private StudioPostgresSchemaLease(
            string adminConnectionString,
            string schemaName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            SchemaName = schemaName;
            ConnectionString = connectionString;
        }

        public string SchemaName { get; }

        public string ConnectionString { get; }

        public static async Task<StudioPostgresSchemaLease> CreateAsync(
            string baseConnectionString,
            string serviceScope,
            CancellationToken cancellationToken)
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = string.Empty,
                ApplicationName = "OpenLineOps.StudioTwoAgent.SchemaAdmin"
            };
            var schemaName = $"olo_studio_{serviceScope}";
            await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 10;
                command.CommandText = $"CREATE SCHEMA \"{schemaName}\";";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var runBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                SearchPath = schemaName,
                ApplicationName = "OpenLineOps.StudioTwoAgent.Coordinator"
            };
            return new StudioPostgresSchemaLease(
                adminBuilder.ConnectionString,
                schemaName,
                runBuilder.ConnectionString);
        }

        public async Task<StudioPostgresEvidence> ReadEvidenceAsync(
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT count(*) FROM olo_production_runs),
                    (SELECT count(*) FROM olo_production_terminal_evidence),
                    (SELECT count(*) FROM olo_station_job_outbox),
                    (SELECT count(*) FROM olo_station_job_outbox WHERE published_at_utc IS NOT NULL),
                    (SELECT count(*) FROM olo_station_job_outbox WHERE published_at_utc IS NULL),
                    (SELECT count(*) FROM olo_station_job_outbox WHERE quarantine_reason IS NOT NULL),
                    (SELECT count(*) FROM olo_station_job_result_inbox),
                    (SELECT count(*) FROM olo_station_job_event_inbox),
                    (SELECT count(*) FROM olo_resource_leases),
                    (SELECT count(*) FROM olo_production_units),
                    (SELECT count(*) FROM olo_slot_occupancies WHERE status = 'Available'),
                    (SELECT count(*) FROM olo_production_material_timeline),
                    (SELECT count(DISTINCT production_run_id)
                        FROM olo_production_material_timeline
                        WHERE production_run_id IS NOT NULL);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("PostgreSQL evidence query returned no row.");
            }

            return new StudioPostgresEvidence(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12));
        }

        public async Task<bool> SchemaExistsAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = @name);";
            command.Parameters.AddWithValue("name", SchemaName);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidDataException("PostgreSQL schema existence query returned null."));
        }

        public async Task<IReadOnlyList<StudioPostgresFinalUnloadEvidence>>
            AssertFinalUnloadEvidenceAsync(
                IReadOnlyList<StudioFinalUnloadEvidence> expected,
                CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<StudioPostgresFinalUnloadEvidence>(expected.Count);
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var unload in expected)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT evidence_id,
                           kind,
                           production_run_id,
                           production_unit_id,
                           occurred_at_utc,
                           document_json::text
                    FROM olo_production_material_timeline
                    WHERE evidence_id = @location_evidence_id
                       OR evidence_id = @slot_evidence_id
                    ORDER BY kind;
                    """;
                command.Parameters.AddWithValue(
                    "location_evidence_id",
                    unload.LocationEvidenceId);
                command.Parameters.AddWithValue("slot_evidence_id", unload.SlotEvidenceId);
                var rows = new List<(Guid EvidenceId, string Kind, string DocumentSha256)>();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        Assert.Equal(unload.ProductionRunId, reader.GetGuid(2));
                        Assert.Equal(unload.ProductionUnitId, reader.GetGuid(3));
                        Assert.Equal(unload.OccurredAtUtc, reader.GetFieldValue<DateTimeOffset>(4));
                        rows.Add((
                            reader.GetGuid(0),
                            reader.GetString(1),
                            StudioSha256Text(reader.GetString(5))));
                    }
                }

                Assert.Equal(2, rows.Count);
                var location = Assert.Single(rows, row =>
                    row.EvidenceId == unload.LocationEvidenceId
                    && string.Equals(
                        row.Kind,
                        "LocationTransition",
                        StringComparison.Ordinal));
                var slot = Assert.Single(rows, row =>
                    row.EvidenceId == unload.SlotEvidenceId
                    && string.Equals(
                        row.Kind,
                        "SlotOccupancyTransition",
                        StringComparison.Ordinal));
                var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        unload.ProductionUnitId,
                        unload.ProductionRunId,
                        unload.OccurredAtUtc,
                        unload.LocationEvidenceId,
                        unload.SlotEvidenceId,
                        locationDocumentSha256 = location.DocumentSha256,
                        slotDocumentSha256 = slot.DocumentSha256
                    },
                    StudioEvidenceJsonOptions);
                result.Add(new StudioPostgresFinalUnloadEvidence(
                    unload.ProductionUnitId,
                    unload.ProductionRunId,
                    unload.OccurredAtUtc,
                    unload.LocationEvidenceId,
                    unload.SlotEvidenceId,
                    location.DocumentSha256,
                    slot.DocumentSha256,
                    snapshotBytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(snapshotBytes))));
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await using (var runPoolIdentity = new NpgsqlConnection(ConnectionString))
            {
                NpgsqlConnection.ClearPool(runPoolIdentity);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{SchemaName}\" CASCADE;";
            await command.ExecuteNonQueryAsync(timeout.Token);
            _disposed = true;
        }
    }
}
