using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.VendorTestHelper;

namespace OpenLineOps.Devices.Tests;

public sealed class ProjectReleaseExternalProgramCommandExecutorTests : IDisposable
{
    private readonly string _applicationRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-external-program-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _hostRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-external-program-host-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _appContainerProfileName =
        $"OpenLineOps.Tests.External.{Guid.NewGuid():N}";

    [Fact]
    public async Task ProviderResourceCarriesRunOperationAndProductionUnitInputThenMapsExactResult()
    {
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")),
            argumentTemplates: ["--product", "{{product.identity}}", "--run", "{{run.id}}"]);
        RuntimeCommandExecutionContext? capturedContext = null;
        ProjectReleaseRuntimeCommandRoute? capturedRoute = null;

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (context, providerRoute, _) =>
            {
                capturedContext = context;
                capturedRoute = providerRoute;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                    "{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.50}}"));
            },
            AllowFences,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        Assert.Equal(
            "{\"test.outcome\":{\"kind\":\"Text\",\"value\":\"Passed\"},"
            + "\"test.voltage\":{\"kind\":\"FixedPoint\",\"value\":\"12.5\"}}",
            result.Payload);
        Assert.Equal(ResultJudgement.Passed, result.ResultJudgement);
        Assert.IsType<ProjectReleaseProcessCommandRoute>(capturedRoute);
        Assert.NotNull(capturedContext);
        using var invocation = JsonDocument.Parse(capturedContext.InputPayload!);
        var root = invocation.RootElement;
        Assert.Equal("openlineops.external-program-invocation", root.GetProperty("schema").GetString());
        Assert.Equal(CreateContext().ProductionRunId.ToString(), root.GetProperty("productionRunId").GetString());
        Assert.Equal("line-main", root.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal("operation-external", root.GetProperty("operationId").GetString());
        Assert.Equal(2, root.GetProperty("operationAttempt").GetInt32());
        Assert.Equal("station-eol", root.GetProperty("stationSystemId").GetString());
        Assert.Equal(CreateContext().StepId.ToString(), root.GetProperty("runtimeStepId").GetString());
        Assert.Equal(CreateContext().CommandId.ToString(), root.GetProperty("runtimeCommandId").GetString());
        Assert.Equal("node-external", root.GetProperty("runtimeNodeId").GetString());
        Assert.Equal("node-external:action:1", root.GetProperty("actionId").GetString());
        Assert.Equal("test.external", root.GetProperty("capabilityId").GetString());
        Assert.Equal("ExecuteExternalProgram", root.GetProperty("commandName").GetString());
        Assert.Equal("System", root.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal("station-eol", root.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal(
            CreateContext().ProductionUnitId.ToString(),
            root.GetProperty("productionUnit").GetProperty("id").GetString());
        Assert.Equal("model-main", root.GetProperty("productionUnit").GetProperty("modelId").GetString());
        Assert.Equal("MODEL-MAIN", root.GetProperty("productionUnit").GetProperty("modelCode").GetString());
        Assert.Equal("serialNumber", root.GetProperty("productionUnit").GetProperty("identityInputKey").GetString());
        Assert.Equal("SERIAL-001", root.GetProperty("productionUnit").GetProperty("identityValue").GetString());
        Assert.Equal("SERIAL-001", root.GetProperty("inputs").GetProperty("serial").GetString());
        Assert.Equal("MODEL-MAIN", root.GetProperty("inputs").GetProperty("model").GetString());
        Assert.Equal("operation-external", root.GetProperty("inputs").GetProperty("operationId").GetString());
        Assert.Equal(
            ["--product", "SERIAL-001", "--run", CreateContext().ProductionRunId.ToString()],
            root.GetProperty("arguments")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task VendorBoundaryNormalizesUtcTimestampBeforePersistingTypedOutput()
    {
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")),
            extraResultMapping: new ExternalProgramRouteResultMapping(
                "$.completedAtUtc",
                "test.completedAtUtc",
                ProductionContextValueKind.DateTimeUtc));

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                "{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.50},"
                + "\"completedAtUtc\":\"2026-07-15T08:00:00.0000000+08:00\"}")),
            AllowFences,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        using var payload = JsonDocument.Parse(result.Payload!);
        Assert.Equal(
            "12.5",
            payload.RootElement.GetProperty("test.voltage").GetProperty("value").GetString());
        var completedAtUtc = payload.RootElement.GetProperty("test.completedAtUtc");
        Assert.Equal("DateTimeUtc", completedAtUtc.GetProperty("kind").GetString());
        Assert.Equal(
            "2026-07-15T00:00:00.0000000+00:00",
            completedAtUtc.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ProviderResourceMapsTypedProductionInputIntoInvocationAndArguments()
    {
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")),
            argumentTemplates: ["--limit", "{{input.limit}}"],
            productionInputMapping: new ExternalProgramRouteInputMapping(
                "$production.inspection.limit",
                "limit"));
        RuntimeCommandExecutionContext? capturedContext = null;

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(new Dictionary<string, ProductionContextValue>
            {
                ["inspection.limit"] = new(ProductionContextValueKind.FixedPoint, "3.5")
            }),
            route,
            (context, _, _) =>
            {
                capturedContext = context;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                    "{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.5}}"));
            },
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        using var invocation = JsonDocument.Parse(capturedContext!.InputPayload!);
        Assert.Equal(3.50m, invocation.RootElement.GetProperty("inputs").GetProperty("limit").GetDecimal());
        Assert.Equal(
            ["--limit", "3.5"],
            invocation.RootElement.GetProperty("arguments")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task MissingProductionInputRejectsBeforeVendorProviderRuns()
    {
        var providerInvoked = false;
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")),
            productionInputMapping: new ExternalProgramRouteInputMapping(
                "$production.inspection.limit",
                "limit"));

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) =>
            {
                providerInvoked = true;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed("{}"));
            },
            AllowFences,
            new RecordingExternalProgramHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.False(providerInvoked);
        Assert.Contains("unsupported or duplicated input mapping", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleFenceRejectsBeforeProviderOrExternalProgramHostInvocation()
    {
        var providerInvoked = false;
        var host = new RecordingExternalProgramHost();
        var rejectingFence = static (
            RuntimeCommandExecutionContext _,
            CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeCommandExecutionResult?>(
                RuntimeCommandExecutionResult.Rejected("Resource fencing token is stale."));
        };
        var providerRoute = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")));
        var program = CreateJsonOutputProgram();
        var executableRoute = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments);

        var providerResult = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            providerRoute,
            (_, _, _) =>
            {
                providerInvoked = true;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed("{}"));
            },
            rejectingFence,
            host);
        var executableResult = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            executableRoute,
            ProviderMustNotRun,
            rejectingFence,
            host);

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, providerResult.Outcome);
        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, executableResult.Outcome);
        Assert.False(providerInvoked);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public async Task ApplicationExecutableRunsOnlyFrozenProgramAndMapsResult()
    {
        var program = CreateJsonOutputProgram();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        using var resultPayload = JsonDocument.Parse(result.Payload!);
        Assert.Equal(
            "Text",
            resultPayload.RootElement.GetProperty("test.outcome").GetProperty("kind").GetString());
        Assert.Equal(
            "Passed",
            resultPayload.RootElement.GetProperty("test.outcome").GetProperty("value").GetString());
        Assert.Equal(
            "FixedPoint",
            resultPayload.RootElement.GetProperty("test.voltage").GetProperty("kind").GetString());
        Assert.Equal(
            "12.5",
            resultPayload.RootElement.GetProperty("test.voltage").GetProperty("value").GetString());
        var evidence = Assert.IsType<RuntimeCommandEvidence>(
            RuntimeCommandEvidencePayload.Read(result.Payload));
        Assert.Equal(ExecutionStatus.Completed, evidence.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, evidence.ResultJudgement);
        Assert.Contains(evidence.Artifacts, artifact => artifact.Name == "stdout.log");
        Assert.Contains(evidence.Artifacts, artifact => artifact.Name == "stderr.log");
        var csv = Assert.Single(evidence.Artifacts, artifact => artifact.Name == "measurements.csv");
        Assert.Equal("Csv", csv.Kind);
        Assert.True(File.Exists(Path.Combine(
            _hostRoot,
            "evidence",
            csv.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(_hostRoot, "workspaces"),
            "*",
            SearchOption.AllDirectories));
        Assert.Equal(ResultJudgement.Passed, result.ResultJudgement);
    }

    [Fact]
    public async Task ExplicitNetworkPermissionReachesVendorAsInternetClientCapability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = CreateJsonOutputProgram();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments,
            networkAccessAllowed: true);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        var evidence = Assert.IsType<RuntimeCommandEvidence>(
            RuntimeCommandEvidencePayload.Read(result.Payload));
        var stdout = Assert.Single(evidence.Artifacts, artifact => artifact.Name == "stdout.log");
        var stdoutPath = Path.Combine(
            _hostRoot,
            "evidence",
            stdout.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(stdoutPath));
        Assert.True(output.RootElement.GetProperty("internetClientCapability").GetBoolean());
    }

    [Theory]
    [InlineData(
        "Failed",
        RuntimeCommandExecutionOutcome.Completed,
        ResultJudgement.Failed)]
    [InlineData(
        "Aborted",
        RuntimeCommandExecutionOutcome.Completed,
        ResultJudgement.Aborted)]
    public async Task FrozenOutcomeMappingDrivesRuntimeTerminalSemantics(
        string vendorToken,
        RuntimeCommandExecutionOutcome expectedTransportOutcome,
        ResultJudgement expectedResultJudgement)
    {
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")));

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                $"{{\"outcome\":\"{vendorToken}\",\"metrics\":{{\"voltage\":12.5}}}}")),
            AllowFences,
            CreateHost());

        Assert.Equal(expectedTransportOutcome, result.Outcome);
        Assert.Equal(expectedResultJudgement, result.ResultJudgement);
        Assert.Equal(
            $"{{\"test.outcome\":{{\"kind\":\"Text\",\"value\":\"{vendorToken}\"}},"
            + "\"test.voltage\":{\"kind\":\"FixedPoint\",\"value\":\"12.5\"}}",
            result.Payload);
    }

    [Fact]
    public async Task ApplicationExecutableNonZeroExitFailsClosed()
    {
        var executable = OperatingSystem.IsWindows()
            ? CopyVendorHelperIntoApplication()
            : CopyShellIntoApplication();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            executable,
            providerRoute: null,
            argumentTemplates: OperatingSystem.IsWindows()
                ? ["sandbox-exit", "7"]
                : NonZeroExitArguments());

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("exited with code 7", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationExecutableTimeoutTerminatesAndFailsClosed()
    {
        var program = CreateAtomicOutputResidueProgram(delayMilliseconds: 5_000);
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments,
            timeoutMilliseconds: 2_000);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(timeout: TimeSpan.FromSeconds(2)),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.TimedOut,
            result.Reason);
        Assert.Contains("2000 ms", result.Reason, StringComparison.Ordinal);
        Assert.Contains(
            OpenLineOps.VendorTestHelper.Program.AtomicOutputResidueMarker,
            result.Reason,
            StringComparison.Ordinal);
        var evidence = Assert.IsType<RuntimeCommandEvidence>(
            RuntimeCommandEvidencePayload.Read(result.Payload));
        Assert.DoesNotContain(
            evidence.Artifacts,
            artifact => artifact.Name == OpenLineOps.VendorTestHelper.Program.AtomicOutputResidueFileName);
        Assert.Empty(Directory.EnumerateFiles(
            _hostRoot,
            OpenLineOps.VendorTestHelper.Program.AtomicOutputResidueFileName,
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SuccessfulExecutableCannotHideNonCanonicalArtifactAsAtomicWriteResidue()
    {
        var program = CreateAtomicOutputResidueProgram(delayMilliseconds: 0);
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("artifact path is not canonical", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutClosesWindowsJobAndTerminatesSpawnedChildProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = CreateProcessTreeProgram();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments,
            timeoutMilliseconds: 2_000);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(timeout: TimeSpan.FromSeconds(2)),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.TimedOut, result.Outcome);
        var evidence = Assert.IsType<RuntimeCommandEvidence>(
            RuntimeCommandEvidencePayload.Read(result.Payload));
        var processIdArtifact = Assert.Single(
            evidence.Artifacts,
            artifact => artifact.Name == "child-process-id.txt");
        var processIdPath = Path.Combine(
            _hostRoot,
            "evidence",
            processIdArtifact.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        var childProcessId = int.Parse(
            (await File.ReadAllTextAsync(processIdPath)).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(await IsProcessRunningAsync(childProcessId));
    }

    [Fact]
    public async Task OperatorCancellationClosesWindowsJobAndTerminatesSpawnedChildProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = CreateProcessTreeProgram();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments,
            timeoutMilliseconds: 30_000);
        using var cancellation = new CancellationTokenSource();
        var execution = ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
                CreateContext(timeout: TimeSpan.FromSeconds(30)),
                route,
                ProviderMustNotRun,
                AllowFences,
                CreateHost(),
                cancellation.Token)
            .AsTask();
        var childProcessId = await WaitForActiveChildProcessIdAsync();

        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Canceled,
            result.Reason);
        Assert.Equal(ResultJudgement.Aborted, result.ResultJudgement);
        Assert.False(await IsProcessRunningAsync(childProcessId));
    }

    [Fact]
    public async Task ApplicationExecutableStreamingOutputLimitTerminatesProcessAndFreezesBoundedLog()
    {
        var program = CreateLargeOutputProgram();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost(maximumStandardOutputBytes: 256));

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("streaming output limit", result.Reason, StringComparison.Ordinal);
        var evidence = Assert.IsType<RuntimeCommandEvidence>(
            RuntimeCommandEvidencePayload.Read(result.Payload));
        var stdout = Assert.Single(evidence.Artifacts, artifact => artifact.Name == "stdout.log");
        Assert.Equal(256, stdout.SizeBytes);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"outcome\":\"Passed\",\"outcome\":\"Failed\"}")]
    [InlineData("{\"different\":true}")]
    [InlineData("{\"outcome\":\"passed\",\"metrics\":{\"voltage\":12.5}}")]
    [InlineData("{\"outcome\":\"Unknown\",\"metrics\":{\"voltage\":12.5}}")]
    public async Task InvalidOrUnmappedProviderOutputFailsClosed(string providerOutput)
    {
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")));

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(
                RuntimeCommandExecutionResult.Completed(providerOutput)),
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(ResultJudgement.Unknown, result.ResultJudgement);
    }

    [Fact]
    public async Task UnsupportedTemplateIsRejectedBeforeProviderInvocation()
    {
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-program",
                new DeviceCapabilityId("test.external")),
            argumentTemplates: ["{{unknown.value}}"]);
        var invoked = false;

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed("{}"));
            },
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ExecutableOutsideFrozenProgramsDirectoryIsRejected()
    {
        Directory.CreateDirectory(_applicationRoot);
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            "../outside.exe",
            providerRoute: null,
            argumentTemplates: []);

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task FrozenExecutableChangedAfterRouteResolutionIsRejected()
    {
        var executable = CopyShellIntoApplication();
        var route = CreateRoute(
            ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
            executable,
            providerRoute: null,
            argumentTemplates: NonZeroExitArguments());
        await File.AppendAllTextAsync(
            Path.Combine(
                _applicationRoot,
                executable.Replace('/', Path.DirectorySeparatorChar)),
            "tampered");

        var result = await ProjectReleaseExternalProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
            AllowFences,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("frozen executable", result.Reason, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        DeleteDirectoryBounded(_applicationRoot);
        DeleteDirectoryBounded(_hostRoot);
    }

    private static void DeleteDirectoryBounded(string path)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
        }

        throw new IOException(
            $"External program test directory remained locked after bounded process shutdown: {path}",
            lastFailure);
    }

    private ExternalProgramHost CreateHost(int maximumStandardOutputBytes = 4 * 1024 * 1024)
    {
        var options = new ExternalProgramHostOptions
        {
            WorkspaceRootPath = Path.Combine(_hostRoot, "workspaces"),
            EvidenceRootPath = Path.Combine(_hostRoot, "evidence"),
            MaximumStandardOutputBytes = maximumStandardOutputBytes,
            RequireRestrictedHostIdentity = true,
            RequireImmutableContentProtection = false,
            RequireAppContainerIsolation = true,
            AppContainerProfileName = _appContainerProfileName,
            RestrictedServiceSid = "S-1-5-80-123-456-789-1011-1213"
        };
        return new ExternalProgramHost(
            options,
            processLauncher: null,
            contentProtector: null,
            policyEnforcer: new ExternalProgramHostPolicyEnforcer(
                options,
                new TestHostIdentityReader()));
    }

    private ProjectReleaseExternalProgramCommandRoute CreateRoute(
        string launchKind,
        string? executable,
        ProjectReleaseRuntimeCommandRoute? providerRoute,
        IReadOnlyCollection<string>? argumentTemplates = null,
        long timeoutMilliseconds = 30_000,
        bool networkAccessAllowed = false,
        ExternalProgramRouteInputMapping? productionInputMapping = null,
        ExternalProgramRouteResultMapping? extraResultMapping = null)
    {
        var resourceRelativePath = executable is null
            ? "external-programs/resource.external-program"
            : Path.GetDirectoryName(executable)!.Replace('\\', '/');
        var entryPoint = executable is null ? null : Path.GetFileName(executable);
        long? executableSizeBytes = null;
        string? executableSha256 = null;
        if (executable is not null)
        {
            var path = Path.GetFullPath(Path.Combine(
                _applicationRoot,
                executable.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                executableSizeBytes = bytes.LongLength;
                executableSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            else
            {
                executableSizeBytes = 0;
                executableSha256 = new string('0', 64);
            }
        }

        var resourceDirectory = Path.GetFullPath(Path.Combine(
            _applicationRoot,
            resourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var files = Directory.Exists(resourceDirectory)
            ? Directory.EnumerateFiles(resourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var bytes = File.ReadAllBytes(path);
                    return new ExternalProgramRouteFile(
                        Path.GetRelativePath(resourceDirectory, path).Replace('\\', '/'),
                        bytes.LongLength,
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                })
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray()
            : [];

        return new ProjectReleaseExternalProgramCommandRoute(
            ProjectReleaseRuntimeProviderKinds.ExternalSystem,
            executable is null ? "provider.external-program" : "resource.external-program",
            new DeviceCapabilityId("test.external"),
            "resource.external-program",
            launchKind,
            _applicationRoot,
            resourceRelativePath,
            "model-main",
            "MODEL-MAIN",
            "serialNumber",
            entryPoint,
            executableSizeBytes,
            executableSha256,
            files,
            argumentTemplates ?? [],
            new ExternalProgramRouteInputMapping[]
            {
                new ExternalProgramRouteInputMapping("$product.identity", "serial"),
                new ExternalProgramRouteInputMapping("$product.model", "model"),
                new ExternalProgramRouteInputMapping("$run.id", "runId"),
                new ExternalProgramRouteInputMapping("$operation.id", "operationId")
            }.Concat(productionInputMapping is null ? [] : [productionInputMapping]).ToArray(),
            new ExternalProgramRouteResultMapping[]
            {
                new ExternalProgramRouteResultMapping(
                    "$.outcome",
                    "test.outcome",
                    ProductionContextValueKind.Text),
                new ExternalProgramRouteResultMapping(
                    "$.metrics.voltage",
                    "test.voltage",
                    ProductionContextValueKind.FixedPoint)
            }.Concat(extraResultMapping is null ? [] : [extraResultMapping]).ToArray(),
            new ExternalProgramRouteOutcomeMapping(
                "$.outcome",
                "Passed",
                "Failed",
                "Aborted"),
            new ExternalProgramRoutePermissionProfile(
                "Restricted",
                networkAccessAllowed,
                AllowedEnvironmentVariables: ["SystemRoot"]),
            new ExternalProgramRouteExecutionLimits(
                timeoutMilliseconds,
                MaximumProcessCount: 8,
                MaximumWorkingSetBytes: 512L * 1024 * 1024,
                MaximumCpuTimeMilliseconds: timeoutMilliseconds,
                MaximumStandardOutputBytes: 4 * 1024 * 1024,
                MaximumStandardErrorBytes: 4 * 1024 * 1024,
                MaximumArtifactCount: 64,
                MaximumArtifactBytes: 64L * 1024 * 1024,
                MaximumTotalArtifactBytes: 256L * 1024 * 1024),
            providerRoute);
    }

    private static RuntimeCommandExecutionContext CreateContext(
        IReadOnlyDictionary<string, ProductionContextValue>? productionInputs = null,
        TimeSpan? timeout = null)
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new ProductionRunId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
            new ProductionUnitId(Guid.Parse("00000000-0000-0000-0000-000000000011")),
            "line-main",
            "operation-external",
            "operation-external@0002",
            2,
            "station-eol",
            new ProductionUnitIdentity("model-main", "serialNumber", "SERIAL-001"),
            "lot-main",
            "carrier-main",
            "fixture-eol",
            "device-eol",
            new ConfigurationSnapshotId("configuration-eol"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-external"),
            new RuntimeCapabilityId("test.external"),
            "ExecuteExternalProgram",
            "{\"externalProgramResourceId\":\"resource.external-program\"}",
            timeout ?? TimeSpan.FromSeconds(30),
            new RuntimeActionId("node-external:action:1"),
            "System",
            "station-eol",
            "project-main",
            "application-main",
            "snapshot-main",
            productionInputs ?? new Dictionary<string, ProductionContextValue>(),
            [new ResourceLeaseFenceEvidence(
                new ResourceRequirement(ResourceKind.Station, "station-eol"),
                1,
                new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
    }

    private sealed class TestHostIdentityReader : IExternalProgramHostIdentityReader
    {
        public ExternalProgramHostIdentity Read(string requiredServiceSid) => new(
            "S-1-5-19",
            requiredServiceSid,
            ServiceLogonSidEnabled: true,
            TokenHasRestrictions: true,
            ServiceSidEnabled: true,
            ServiceSidRestricted: true);
    }

    private sealed class RecordingExternalProgramHost : IExternalProgramHost
    {
        public int CallCount { get; private set; }

        public ValueTask<ExternalProgramExecutionResult> ExecuteAsync(
            ExternalProgramExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            CallCount++;
            throw new InvalidOperationException("External Program Host must not run with a stale fence.");
        }
    }

    private string CopyShellIntoApplication()
    {
        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-program");
        Directory.CreateDirectory(programsDirectory);
        var source = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec")
                ?? throw new InvalidOperationException("ComSpec is required for this test.")
            : "/bin/sh";
        var fileName = OperatingSystem.IsWindows() ? "test-shell.exe" : "test-shell";
        var destination = Path.Combine(programsDirectory, fileName);
        File.Copy(source, destination);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                destination,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }

        return $"programs/external-program/{fileName}";
    }

    private FrozenTestProgram CreateAtomicOutputResidueProgram(int delayMilliseconds)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FrozenTestProgram(
                CopyVendorHelperIntoApplication(),
                [
                    "sandbox-atomic-output-residue",
                    delayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]);
        }

        var command = "printf '%s' 'uncommitted' > \"$OPENLINEOPS_OUTPUT_DIRECTORY/"
                      + OpenLineOps.VendorTestHelper.Program.AtomicOutputResidueFileName
                      + "\"; printf '%s\\n' '"
                      + OpenLineOps.VendorTestHelper.Program.AtomicOutputResidueMarker
                      + "' >&2";
        if (delayMilliseconds > 0)
        {
            var delaySeconds = Math.Max(1, (delayMilliseconds + 999) / 1_000);
            command += "; sleep "
                       + delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new FrozenTestProgram(CopyShellIntoApplication(), ["-c", command]);
    }

    private FrozenTestProgram CreateJsonOutputProgram()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FrozenTestProgram(
                CopyShellIntoApplication(),
                [
                    "-c",
                    "printf '%s' '{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.5}}'; "
                    + "printf '%s\\n' 'name,value' 'voltage,12.5' > \"$OPENLINEOPS_OUTPUT_DIRECTORY/measurements.csv\""
                ]);
        }

        return new FrozenTestProgram(
            CopyVendorHelperIntoApplication(),
            ["--mode", "Passed"]);
    }

    private FrozenTestProgram CreateLargeOutputProgram()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FrozenTestProgram(
                CopyShellIntoApplication(),
                ["-c", "while true; do printf '%s' '0123456789abcdef'; done"]);
        }

        return new FrozenTestProgram(
            CopyVendorHelperIntoApplication(),
            ["sandbox-large-output"]);
    }

    private FrozenTestProgram CreateProcessTreeProgram()
    {
        return new FrozenTestProgram(
            CopyVendorHelperIntoApplication(),
            ["--mode", "SpawnChildDelay", "--delay-milliseconds", "60000"]);
    }

    private string CopyVendorHelperIntoApplication()
    {
        var sourceAssembly = typeof(VendorTestHelperMarker).Assembly.Location;
        var sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        var destinationDirectory = Path.Combine(
            _applicationRoot,
            "programs",
            "external-program");
        Directory.CreateDirectory(destinationDirectory);
        foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var source = Path.Combine(
                sourceDirectory,
                "OpenLineOps.VendorTestHelper" + extension);
            File.Copy(
                source,
                Path.Combine(destinationDirectory, Path.GetFileName(source)),
                overwrite: true);
        }

        return "programs/external-program/OpenLineOps.VendorTestHelper.exe";
    }

    private static async Task<bool> IsProcessRunningAsync(int processId)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            await Task.Delay(50);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }

    private async Task<int> WaitForActiveChildProcessIdAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var path = Directory.Exists(_hostRoot)
                ? Directory.EnumerateFiles(
                        _hostRoot,
                        "child-process-id.txt",
                        SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;
            if (path is not null)
            {
                try
                {
                    return int.Parse(
                        (await File.ReadAllTextAsync(path)).Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(
            "Vendor child process id was not durably written before the test deadline.");
    }

    private static IReadOnlyCollection<string> NonZeroExitArguments()
    {
        return OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", "exit /b 7"]
            : ["-c", "exit 7"];
    }

    private static ValueTask<RuntimeCommandExecutionResult> ProviderMustNotRun(
        RuntimeCommandExecutionContext context,
        ProjectReleaseRuntimeCommandRoute route,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = route;
        _ = cancellationToken;
        throw new InvalidOperationException("Provider execution must not run for an Application executable route.");
    }

    private static ValueTask<RuntimeCommandExecutionResult?> AllowFences(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RuntimeCommandExecutionResult?>(null);
    }

    private sealed record FrozenTestProgram(
        string Executable,
        IReadOnlyCollection<string> Arguments);
}
