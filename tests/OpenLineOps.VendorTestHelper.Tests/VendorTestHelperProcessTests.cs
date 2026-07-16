using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenLineOps.VendorTestHelper.Tests;

public sealed class VendorTestHelperProcessTests
{
    private const int ProcessTimeoutMilliseconds = 15_000;
    private const string ChildProcessIdFileName = "child-process-id.txt";
    private const string ForceChildPidPublicationFailureEnvironmentVariable =
        "OPENLINEOPS_VENDOR_TEST_HELPER_FORCE_CHILD_PID_PUBLICATION_FAILURE";

    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    [InlineData("Aborted")]
    public async Task TerminalOutcomeModesReturnOneJsonObjectAndCompleteEvidence(
        string mode)
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);

        var result = await RunHelperAsync(
            invocation,
            "--mode",
            mode,
            "--operation-attempt",
            "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"mode={mode}", result.StandardError, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(mode, document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("metrics").GetProperty("attempt").GetInt32());
        Assert.Equal(12.5M, document.RootElement.GetProperty("metrics").GetProperty("voltage").GetDecimal());
        Assert.Equal(
            invocation.ProductionRunId,
            document.RootElement.GetProperty("productionRunId").GetString());
        Assert.Equal(
            invocation.ProductionUnitIdentity,
            document.RootElement.GetProperty("productionUnitIdentity").GetString());

        await AssertEvidenceAsync(invocation);
    }

    [Theory]
    [InlineData(1, "Failed")]
    [InlineData(2, "Passed")]
    public async Task FailedModeCanPassAfterFirstOperationAttempt(
        int operationAttempt,
        string expectedOutcome)
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt);

        var result = await RunHelperAsync(
            invocation,
            "--mode",
            "Failed",
            "--operation-attempt",
            operationAttempt.ToString(CultureInfo.InvariantCulture),
            "--pass-after-first-attempt");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(expectedOutcome, document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            operationAttempt,
            document.RootElement.GetProperty("metrics").GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task CrashReturnsNonZeroAndPreservesEvidenceWithoutAResultPayload()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);

        var result = await RunHelperAsync(invocation, "--mode", "Crash");

        Assert.Equal(23, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("simulated an unexpected vendor process crash", result.StandardError, StringComparison.Ordinal);
        await AssertEvidenceAsync(invocation);
    }

    [Fact]
    public async Task InvalidJsonCompletesWithMalformedProtocolPayload()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);

        var result = await RunHelperAsync(invocation, "--mode", "InvalidJson");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("{\"outcome\":", result.StandardOutput);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(result.StandardOutput));
    }

    [Fact]
    public async Task UnknownTokenCompletesWithAnUnmappedExactOutcome()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);

        var result = await RunHelperAsync(invocation, "--mode", "UnknownToken");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("NeedsReview", document.RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("--mode", "passed")]
    [InlineData("--unknown", "Passed")]
    [InlineData("--mode", "Passed", "--mode", "Failed")]
    public async Task CliRejectsUnknownCaseMismatchedAndDuplicateOptions(params string[] arguments)
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);

        var result = await RunHelperAsync(invocation, arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("usage error", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliAttemptMustExactlyMatchTheInvocationFile()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 2);

        var result = await RunHelperAsync(
            invocation,
            "--mode",
            "Passed",
            "--operation-attempt",
            "1");

        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("must exactly match", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelayAcknowledgesTheCancellationFile()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);
        using var process = StartHelper(
            invocation,
            "--mode",
            "Delay",
            "--delay-milliseconds",
            "300000");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForFileAsync(Path.Combine(invocation.OutputDirectory, "measurements.csv"));

        await File.WriteAllTextAsync(
            Path.Combine(invocation.OutputDirectory, "cancel.request"),
            "cancel",
            Encoding.UTF8);
        await WaitForExitAsync(process);

        Assert.Equal(130, process.ExitCode);
        Assert.Empty(await standardOutput);
        Assert.Contains("acknowledged cancellation", await standardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SpawnChildDelay")]
    [InlineData("SpawnChildDelayRecovery")]
    public async Task SpawnChildDelayReportsPidAndRemovesTheChildWhenCanceled(string mode)
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);
        using var process = StartHelper(
            invocation,
            "--mode",
            mode,
            "--delay-milliseconds",
            "300000");
        Process? childProcess = null;
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            var processIdPath = Path.Combine(invocation.OutputDirectory, "vendor-process-id.txt");
            var childProcessIdPath = Path.Combine(invocation.OutputDirectory, ChildProcessIdFileName);
            await WaitForFileAsync(processIdPath);
            await WaitForFileAsync(childProcessIdPath);
            var processIdText = await File.ReadAllTextAsync(processIdPath, Encoding.UTF8);
            var reportedProcessId = int.Parse(
                processIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            var childProcessIdText = await File.ReadAllTextAsync(childProcessIdPath, Encoding.UTF8);
            var childProcessId = int.Parse(
                childProcessIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            Assert.Equal(process.Id, reportedProcessId);
            childProcess = Process.GetProcessById(childProcessId);
            Assert.False(childProcess.HasExited);

            await File.WriteAllTextAsync(
                Path.Combine(invocation.OutputDirectory, "cancel.request"),
                "cancel",
                Encoding.UTF8);
            await WaitForExitAsync(process);
            await WaitForExitAsync(childProcess);

            Assert.Equal(130, process.ExitCode);
            Assert.Empty(await standardOutput);
            Assert.Contains("acknowledged cancellation", await standardError, StringComparison.Ordinal);
            Assert.True(process.HasExited);
            Assert.True(childProcess.HasExited);
            Assert.Empty(Directory.EnumerateFiles(
                invocation.OutputDirectory,
                $".{ChildProcessIdFileName}.*.tmp"));
        }
        finally
        {
            await TerminateProcessTreeIfRunningAsync(process);
            if (childProcess is not null)
            {
                await TerminateProcessTreeIfRunningAsync(childProcess);
                childProcess.Dispose();
            }
        }
    }

    [Fact]
    public async Task ChildPidPublicationFailureStillRemovesTheStartedChild()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);
        Process? childProcess = null;

        try
        {
            var result = await RunHelperAsync(
                invocation,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ForceChildPidPublicationFailureEnvironmentVariable] = "1"
                },
                "--mode",
                "SpawnChildDelay",
                "--delay-milliseconds",
                "300000");

            Assert.Equal(23, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("simulated child PID publication failure", result.StandardError, StringComparison.Ordinal);
            var match = Regex.Match(
                result.StandardError,
                @"Vendor helper started child process (?<pid>\d+)\.",
                RegexOptions.CultureInvariant);
            Assert.True(match.Success, result.StandardError);
            var childProcessId = int.Parse(
                match.Groups["pid"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            childProcess = TryGetProcessById(childProcessId);
            if (childProcess is not null)
            {
                await WaitForExitAsync(childProcess);
            }

            Assert.True(childProcess is null || childProcess.HasExited);
            Assert.Empty(Directory.EnumerateFiles(
                invocation.OutputDirectory,
                $".{ChildProcessIdFileName}.*.tmp"));
        }
        finally
        {
            if (childProcess is not null)
            {
                await TerminateProcessTreeIfRunningAsync(childProcess);
                childProcess.Dispose();
            }
        }
    }

    private static async Task AssertEvidenceAsync(VendorInvocation invocation)
    {
        var csv = await File.ReadAllTextAsync(
            Path.Combine(invocation.OutputDirectory, "measurements.csv"),
            Encoding.UTF8);
        Assert.Contains("voltage,12.5,V", csv, StringComparison.Ordinal);

        var png = await File.ReadAllBytesAsync(Path.Combine(invocation.OutputDirectory, "inspection.png"));
        Assert.True(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));

        var pdf = await File.ReadAllBytesAsync(Path.Combine(invocation.OutputDirectory, "report.pdf"));
        Assert.True(pdf.AsSpan().StartsWith("%PDF-1.4"u8));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);

        var invocationCopy = await File.ReadAllTextAsync(
            Path.Combine(invocation.OutputDirectory, "invocation-copy.json"),
            Encoding.UTF8);
        Assert.Equal(invocation.InvocationJson, invocationCopy);
    }

    private static async Task<ProcessResult> RunHelperAsync(
        VendorInvocation invocation,
        params string[] arguments)
    {
        return await RunHelperAsync(invocation, additionalEnvironment: null, arguments);
    }

    private static async Task<ProcessResult> RunHelperAsync(
        VendorInvocation invocation,
        IReadOnlyDictionary<string, string>? additionalEnvironment,
        params string[] arguments)
    {
        using var process = StartHelper(invocation, additionalEnvironment, arguments);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForExitAsync(process);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static Process StartHelper(
        VendorInvocation invocation,
        params string[] arguments)
    {
        return StartHelper(invocation, additionalEnvironment: null, arguments);
    }

    private static Process StartHelper(
        VendorInvocation invocation,
        IReadOnlyDictionary<string, string>? additionalEnvironment,
        params string[] arguments)
    {
        var executableName = OperatingSystem.IsWindows()
            ? "OpenLineOps.VendorTestHelper.exe"
            : "OpenLineOps.VendorTestHelper";
        var executablePath = Path.Combine(AppContext.BaseDirectory, "vendor-helper", executableName);
        Assert.True(File.Exists(executablePath), $"Vendor helper executable was not copied to '{executablePath}'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = invocation.RootDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["OPENLINEOPS_INVOCATION_FILE"] = invocation.InvocationFilePath;
        startInfo.Environment["OPENLINEOPS_OUTPUT_DIRECTORY"] = invocation.OutputDirectory;
        if (additionalEnvironment is not null)
        {
            foreach (var (name, value) in additionalEnvironment)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Vendor helper test process could not be started.");
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await TerminateProcessTreeIfRunningAsync(process);

            throw new TimeoutException("Vendor helper test process did not exit before the test timeout.");
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeoutMilliseconds);
        while (!File.Exists(path))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static Process? TryGetProcessById(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task TerminateProcessTreeIfRunningAsync(Process process)
    {
        if (HasExited(process))
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (HasExited(process))
        {
        }
        catch (Win32Exception) when (HasExited(process))
        {
        }

        if (!HasExited(process))
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class VendorInvocation : IAsyncDisposable
    {
        private VendorInvocation(
            string rootDirectory,
            string invocationFilePath,
            string outputDirectory,
            string invocationJson,
            string productionRunId,
            string productionUnitIdentity)
        {
            RootDirectory = rootDirectory;
            InvocationFilePath = invocationFilePath;
            OutputDirectory = outputDirectory;
            InvocationJson = invocationJson;
            ProductionRunId = productionRunId;
            ProductionUnitIdentity = productionUnitIdentity;
        }

        public string RootDirectory { get; }

        public string InvocationFilePath { get; }

        public string OutputDirectory { get; }

        public string InvocationJson { get; }

        public string ProductionRunId { get; }

        public string ProductionUnitIdentity { get; }

        public static async Task<VendorInvocation> CreateAsync(int operationAttempt)
        {
            var rootDirectory = Path.Combine(
                Path.GetTempPath(),
                "openlineops-vendor-helper-tests",
                Guid.NewGuid().ToString("N"));
            var outputDirectory = Path.Combine(rootDirectory, "output");
            Directory.CreateDirectory(outputDirectory);
            var invocationFilePath = Path.Combine(rootDirectory, "invocation.json");
            var productionRunId = Guid.NewGuid().ToString("D");
            const string productionUnitIdentity = "BOARD-0001";
            var invocationJson = JsonSerializer.Serialize(new
            {
                schema = "openlineops.external-test-invocation",
                productionRunId,
                operationAttempt,
                productionUnit = new
                {
                    modelId = "main-board",
                    identityInputKey = "serialNumber",
                    identityValue = productionUnitIdentity
                },
                inputs = new
                {
                    station = "station-eol"
                }
            });
            await File.WriteAllTextAsync(invocationFilePath, invocationJson, Encoding.UTF8);
            return new VendorInvocation(
                rootDirectory,
                invocationFilePath,
                outputDirectory,
                invocationJson,
                productionRunId,
                productionUnitIdentity);
        }

        public async ValueTask DisposeAsync()
        {
            const int maximumAttempts = 40;
            for (var attempt = 1; attempt < maximumAttempts; attempt++)
            {
                if (!Directory.Exists(RootDirectory))
                {
                    return;
                }

                try
                {
                    Directory.Delete(RootDirectory, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
