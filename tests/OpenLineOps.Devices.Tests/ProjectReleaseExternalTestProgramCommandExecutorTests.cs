using System.Text.Json;
using System.Security.Cryptography;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Devices.Tests;

public sealed class ProjectReleaseExternalTestProgramCommandExecutorTests : IDisposable
{
    private readonly string _applicationRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-external-test-program-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProviderAdapterCarriesRunStageAndDutInputThenMapsExactResult()
    {
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")),
            argumentTemplates: ["--dut", "{{dut.identity}}", "--run", "{{run.id}}"]);
        RuntimeCommandExecutionContext? capturedContext = null;
        ProjectReleaseRuntimeCommandRoute? capturedRoute = null;

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (context, providerRoute, _) =>
            {
                capturedContext = context;
                capturedRoute = providerRoute;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                    "{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.5}}"));
            });

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        Assert.Equal(
            "{\"test.outcome\":\"Passed\",\"test.voltage\":12.5}",
            result.Payload);
        Assert.Equal(RuntimeCommandSemanticOutcome.Passed, result.SemanticOutcome);
        Assert.IsType<ProjectReleaseProcessCommandRoute>(capturedRoute);
        Assert.NotNull(capturedContext);
        using var invocation = JsonDocument.Parse(capturedContext.InputPayload!);
        var root = invocation.RootElement;
        Assert.Equal("openlineops.external-test-invocation", root.GetProperty("schema").GetString());
        Assert.Equal(CreateContext().ProductionRunId.ToString(), root.GetProperty("productionRunId").GetString());
        Assert.Equal("line-main", root.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal("stage-external", root.GetProperty("productionStageId").GetString());
        Assert.Equal(2, root.GetProperty("stageSequence").GetInt32());
        Assert.Equal("workstation-eol", root.GetProperty("workstationId").GetString());
        Assert.Equal(CreateContext().StepId.ToString(), root.GetProperty("runtimeStepId").GetString());
        Assert.Equal(CreateContext().CommandId.ToString(), root.GetProperty("runtimeCommandId").GetString());
        Assert.Equal("node-external", root.GetProperty("runtimeNodeId").GetString());
        Assert.Equal("node-external:action:1", root.GetProperty("actionId").GetString());
        Assert.Equal("test.external", root.GetProperty("capabilityId").GetString());
        Assert.Equal("ExecuteTestProgram", root.GetProperty("commandName").GetString());
        Assert.Equal("System", root.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal("station-eol", root.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal("model-main", root.GetProperty("dut").GetProperty("modelId").GetString());
        Assert.Equal("MODEL-MAIN", root.GetProperty("dut").GetProperty("modelCode").GetString());
        Assert.Equal("serialNumber", root.GetProperty("dut").GetProperty("identityInputKey").GetString());
        Assert.Equal("SERIAL-001", root.GetProperty("dut").GetProperty("identityValue").GetString());
        Assert.Equal("SERIAL-001", root.GetProperty("inputs").GetProperty("serial").GetString());
        Assert.Equal("MODEL-MAIN", root.GetProperty("inputs").GetProperty("model").GetString());
        Assert.Equal("stage-external", root.GetProperty("inputs").GetProperty("stageId").GetString());
        Assert.Equal(
            ["--dut", "SERIAL-001", "--run", CreateContext().ProductionRunId.ToString()],
            root.GetProperty("arguments")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task ApplicationExecutableRunsOnlyFrozenProgramAndMapsResult()
    {
        var program = CreateJsonOutputProgram();
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments);

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun);

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        Assert.Equal(
            "{\"test.outcome\":\"Passed\",\"test.voltage\":12.5}",
            result.Payload);
        Assert.Equal(RuntimeCommandSemanticOutcome.Passed, result.SemanticOutcome);
    }

    [Theory]
    [InlineData(
        "Failed",
        RuntimeCommandExecutionOutcome.Failed,
        RuntimeCommandSemanticOutcome.Failed)]
    [InlineData(
        "Aborted",
        RuntimeCommandExecutionOutcome.Canceled,
        RuntimeCommandSemanticOutcome.Aborted)]
    public async Task FrozenOutcomeMappingDrivesRuntimeTerminalSemantics(
        string vendorToken,
        RuntimeCommandExecutionOutcome expectedTransportOutcome,
        RuntimeCommandSemanticOutcome expectedSemanticOutcome)
    {
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")));

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                $"{{\"outcome\":\"{vendorToken}\",\"metrics\":{{\"voltage\":12.5}}}}")));

        Assert.Equal(expectedTransportOutcome, result.Outcome);
        Assert.Equal(expectedSemanticOutcome, result.SemanticOutcome);
        Assert.Equal(
            $"{{\"test.outcome\":\"{vendorToken}\",\"test.voltage\":12.5}}",
            result.Payload);
    }

    [Fact]
    public async Task ApplicationExecutableNonZeroExitFailsClosed()
    {
        var executable = CopyShellIntoApplication();
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            executable,
            providerRoute: null,
            argumentTemplates: NonZeroExitArguments());

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun);

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("exited with code 7", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationExecutableTimeoutTerminatesAndFailsClosed()
    {
        var executable = CopyShellIntoApplication();
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            executable,
            providerRoute: null,
            argumentTemplates: DelayArguments(),
            timeoutMilliseconds: 100);

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(TimeSpan.FromMilliseconds(100)),
            route,
            ProviderMustNotRun);

        Assert.Equal(RuntimeCommandExecutionOutcome.TimedOut, result.Outcome);
        Assert.Contains("100 ms", result.Reason, StringComparison.Ordinal);
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
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")));

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(
                RuntimeCommandExecutionResult.Completed(providerOutput)));

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Null(result.SemanticOutcome);
    }

    [Fact]
    public async Task UnsupportedTemplateIsRejectedBeforeProviderInvocation()
    {
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")),
            argumentTemplates: ["{{unknown.value}}"]);
        var invoked = false;

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed("{}"));
            });

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ExecutableOutsideFrozenProgramsDirectoryIsRejected()
    {
        Directory.CreateDirectory(_applicationRoot);
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            "../outside.exe",
            providerRoute: null,
            argumentTemplates: []);

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun);

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task FrozenExecutableChangedAfterRouteResolutionIsRejected()
    {
        var executable = CopyShellIntoApplication();
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            executable,
            providerRoute: null,
            argumentTemplates: NonZeroExitArguments());
        await File.AppendAllTextAsync(
            Path.Combine(
                _applicationRoot,
                executable.Replace('/', Path.DirectorySeparatorChar)),
            "tampered");

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun);

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("frozen executable", result.Reason, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_applicationRoot))
        {
            Directory.Delete(_applicationRoot, recursive: true);
        }
    }

    private ProjectReleaseExternalTestProgramCommandRoute CreateRoute(
        string launchKind,
        string? executable,
        ProjectReleaseRuntimeCommandRoute? providerRoute,
        IReadOnlyCollection<string>? argumentTemplates = null,
        long timeoutMilliseconds = 30_000)
    {
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

        return new ProjectReleaseExternalTestProgramCommandRoute(
            ProjectReleaseRuntimeProviderKinds.ExternalSystem,
            executable is null ? "provider.external-test" : "adapter.external-test",
            new DeviceCapabilityId("test.external"),
            "adapter.external-test",
            launchKind,
            _applicationRoot,
            "model-main",
            "MODEL-MAIN",
            "serialNumber",
            executable,
            executableSizeBytes,
            executableSha256,
            argumentTemplates ?? [],
            [
                new ExternalTestProgramRouteInputMapping("$dut.identity", "serial"),
                new ExternalTestProgramRouteInputMapping("$dut.model", "model"),
                new ExternalTestProgramRouteInputMapping("$run.id", "runId"),
                new ExternalTestProgramRouteInputMapping("$stage.id", "stageId")
            ],
            [
                new ExternalTestProgramRouteResultMapping("$.outcome", "test.outcome"),
                new ExternalTestProgramRouteResultMapping("$.metrics.voltage", "test.voltage")
            ],
            new ExternalTestProgramRouteOutcomeMapping(
                "$.outcome",
                "Passed",
                "Failed",
                "Aborted"),
            timeoutMilliseconds,
            providerRoute);
    }

    private static RuntimeCommandExecutionContext CreateContext(TimeSpan? timeout = null)
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new ProductionRunId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
            "line-main",
            "stage-external",
            2,
            "workstation-eol",
            new DutIdentity("model-main", "serialNumber", "SERIAL-001"),
            new StationId("station-eol"),
            new ConfigurationSnapshotId("configuration-eol"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-external"),
            new RuntimeCapabilityId("test.external"),
            "ExecuteTestProgram",
            "{\"externalTestProgramAdapterId\":\"adapter.external-test\"}",
            timeout ?? TimeSpan.FromSeconds(30),
            new RuntimeActionId("node-external:action:1"),
            "System",
            "station-eol",
            "project-main",
            "application-main",
            "snapshot-main");
    }

    private string CopyShellIntoApplication()
    {
        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-test");
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

        return $"programs/external-test/{fileName}";
    }

    private FrozenTestProgram CreateJsonOutputProgram()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FrozenTestProgram(
                CopyShellIntoApplication(),
                ["-c", "printf '%s' '{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.5}}'"]);
        }

        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-test");
        Directory.CreateDirectory(programsDirectory);
        var executablePath = Path.Combine(programsDirectory, "test-program.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cscript.exe"), executablePath);
        var scriptRelativePath = "programs/external-test/output.js";
        File.WriteAllText(
            Path.Combine(_applicationRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "WScript.StdOut.Write('{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.5}}');");
        return new FrozenTestProgram(
            "programs/external-test/test-program.exe",
            ["//nologo", scriptRelativePath]);
    }

    private static IReadOnlyCollection<string> NonZeroExitArguments()
    {
        return OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", "exit /b 7"]
            : ["-c", "exit 7"];
    }

    private static IReadOnlyCollection<string> DelayArguments()
    {
        return OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", "ping 127.0.0.1 -n 6 > nul"]
            : ["-c", "sleep 5"];
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

    private sealed record FrozenTestProgram(
        string Executable,
        IReadOnlyCollection<string> Arguments);
}
