using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;
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
    private readonly string _hostRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-external-program-host-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProviderAdapterCarriesRunOperationAndProductionUnitInputThenMapsExactResult()
    {
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")),
            argumentTemplates: ["--product", "{{product.identity}}", "--run", "{{run.id}}"]);
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
            },
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        Assert.Equal(
            "{\"test.outcome\":\"Passed\",\"test.voltage\":12.5}",
            result.Payload);
        Assert.Equal(ResultJudgement.Passed, result.ResultJudgement);
        Assert.IsType<ProjectReleaseProcessCommandRoute>(capturedRoute);
        Assert.NotNull(capturedContext);
        using var invocation = JsonDocument.Parse(capturedContext.InputPayload!);
        var root = invocation.RootElement;
        Assert.Equal("openlineops.external-test-invocation", root.GetProperty("schema").GetString());
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
        Assert.Equal("ExecuteTestProgram", root.GetProperty("commandName").GetString());
        Assert.Equal("System", root.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal("station-eol", root.GetProperty("target").GetProperty("id").GetString());
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
            ProviderMustNotRun,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.Completed,
            result.Reason);
        using var resultPayload = JsonDocument.Parse(result.Payload!);
        Assert.Equal("Passed", resultPayload.RootElement.GetProperty("test.outcome").GetString());
        Assert.Equal(12.5, resultPayload.RootElement.GetProperty("test.voltage").GetDouble());
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
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")));

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(RuntimeCommandExecutionResult.Completed(
                $"{{\"outcome\":\"{vendorToken}\",\"metrics\":{{\"voltage\":12.5}}}}")),
            CreateHost());

        Assert.Equal(expectedTransportOutcome, result.Outcome);
        Assert.Equal(expectedResultJudgement, result.ResultJudgement);
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
            ProviderMustNotRun,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("exited with code 7", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationExecutableTimeoutTerminatesAndFailsClosed()
    {
        var executable = CopyDelayProgramIntoApplication();
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            executable,
            providerRoute: null,
            argumentTemplates: DelayArguments(),
            timeoutMilliseconds: 100);

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(TimeSpan.FromMilliseconds(100)),
            route,
            ProviderMustNotRun,
            CreateHost());

        Assert.True(
            result.Outcome == RuntimeCommandExecutionOutcome.TimedOut,
            result.Reason);
        Assert.Contains("100 ms", result.Reason, StringComparison.Ordinal);
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
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments,
            timeoutMilliseconds: 500);

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(TimeSpan.FromMilliseconds(500)),
            route,
            ProviderMustNotRun,
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
    public async Task ApplicationExecutableStreamingOutputLimitTerminatesProcessAndFreezesBoundedLog()
    {
        var program = CreateLargeOutputProgram();
        var route = CreateRoute(
            ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
            program.Executable,
            providerRoute: null,
            argumentTemplates: program.Arguments);

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            ProviderMustNotRun,
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
            ProjectReleaseExternalTestProgramLaunchKinds.Provider,
            executable: null,
            providerRoute: new ProjectReleaseProcessCommandRoute(
                "provider.external-test",
                new DeviceCapabilityId("test.external")));

        var result = await ProjectReleaseExternalTestProgramCommandExecutor.ExecuteAsync(
            CreateContext(),
            route,
            (_, _, _) => ValueTask.FromResult(
                RuntimeCommandExecutionResult.Completed(providerOutput)),
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(ResultJudgement.Unknown, result.ResultJudgement);
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
            },
            CreateHost());

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
            ProviderMustNotRun,
            CreateHost());

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
            ProviderMustNotRun,
            CreateHost());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("frozen executable", result.Reason, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_applicationRoot))
        {
            Directory.Delete(_applicationRoot, recursive: true);
        }

        if (Directory.Exists(_hostRoot))
        {
            Directory.Delete(_hostRoot, recursive: true);
        }
    }

    private ExternalProgramHost CreateHost(int maximumStandardOutputBytes = 4 * 1024 * 1024)
    {
        return new ExternalProgramHost(new ExternalProgramHostOptions
        {
            WorkspaceRootPath = Path.Combine(_hostRoot, "workspaces"),
            EvidenceRootPath = Path.Combine(_hostRoot, "evidence"),
            MaximumStandardOutputBytes = maximumStandardOutputBytes
        });
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
                new ExternalTestProgramRouteInputMapping("$product.identity", "serial"),
                new ExternalTestProgramRouteInputMapping("$product.model", "model"),
                new ExternalTestProgramRouteInputMapping("$run.id", "runId"),
                new ExternalTestProgramRouteInputMapping("$operation.id", "operationId")
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
            "operation-external",
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

    private string CopyDelayProgramIntoApplication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CopyShellIntoApplication();
        }

        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-test");
        Directory.CreateDirectory(programsDirectory);
        const string fileName = "delay-program.exe";
        File.Copy(
            Path.Combine(Environment.SystemDirectory, "ping.exe"),
            Path.Combine(programsDirectory, fileName));
        return $"programs/external-test/{fileName}";
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

        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-test");
        Directory.CreateDirectory(programsDirectory);
        var executablePath = Path.Combine(programsDirectory, "test-program.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cscript.exe"), executablePath);
        var scriptRelativePath = "programs/external-test/output.js";
        File.WriteAllText(
            Path.Combine(_applicationRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "var shell = WScript.CreateObject('WScript.Shell');"
            + "var output = shell.Environment('PROCESS')('OPENLINEOPS_OUTPUT_DIRECTORY');"
            + "var fso = WScript.CreateObject('Scripting.FileSystemObject');"
            + "var csv = fso.CreateTextFile(fso.BuildPath(output, 'measurements.csv'), true);"
            + "csv.WriteLine('name,value');csv.WriteLine('voltage,12.5');csv.Close();"
            + "WScript.StdOut.Write('{\"outcome\":\"Passed\",\"metrics\":{\"voltage\":12.5}}');");
        return new FrozenTestProgram(
            "programs/external-test/test-program.exe",
            ["//nologo", scriptRelativePath]);
    }

    private FrozenTestProgram CreateLargeOutputProgram()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FrozenTestProgram(
                CopyShellIntoApplication(),
                ["-c", "while true; do printf '%s' '0123456789abcdef'; done"]);
        }

        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-test");
        Directory.CreateDirectory(programsDirectory);
        var executablePath = Path.Combine(programsDirectory, "large-output-program.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cscript.exe"), executablePath);
        var scriptRelativePath = "programs/external-test/large-output.js";
        File.WriteAllText(
            Path.Combine(_applicationRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "while (true) { WScript.StdOut.Write('0123456789abcdef'); }");
        return new FrozenTestProgram(
            "programs/external-test/large-output-program.exe",
            ["//nologo", scriptRelativePath]);
    }

    private FrozenTestProgram CreateProcessTreeProgram()
    {
        var programsDirectory = Path.Combine(_applicationRoot, "programs", "external-test");
        Directory.CreateDirectory(programsDirectory);
        var executablePath = Path.Combine(programsDirectory, "process-tree-program.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cscript.exe"), executablePath);
        var scriptRelativePath = "programs/external-test/process-tree.js";
        File.WriteAllText(
            Path.Combine(_applicationRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "var shell = WScript.CreateObject('WScript.Shell');"
            + "var output = shell.Environment('PROCESS')('OPENLINEOPS_OUTPUT_DIRECTORY');"
            + "var child = shell.Exec(shell.ExpandEnvironmentStrings('%SystemRoot%')"
            + " + '\\\\System32\\\\ping.exe -t 127.0.0.1');"
            + "var fso = WScript.CreateObject('Scripting.FileSystemObject');"
            + "var id = fso.CreateTextFile(fso.BuildPath(output, 'child-process-id.txt'), true);"
            + "id.WriteLine(child.ProcessID);id.Close();"
            + "while (true) { WScript.Sleep(1000); }");
        return new FrozenTestProgram(
            "programs/external-test/process-tree-program.exe",
            ["//nologo", scriptRelativePath]);
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

    private static IReadOnlyCollection<string> NonZeroExitArguments()
    {
        return OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", "exit /b 7"]
            : ["-c", "exit 7"];
    }

    private static IReadOnlyCollection<string> DelayArguments()
    {
        return OperatingSystem.IsWindows()
            ? ["127.0.0.1", "-n", "6"]
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
