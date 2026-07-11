using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenLineOps.VendorTestHelper.Tests;

public sealed class VendorTestHelperProcessTests
{
    private const int ProcessTimeoutMilliseconds = 15_000;

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

    [Fact]
    public async Task SpawnChildDelayReportsPidAndRemovesTheChildWhenCanceled()
    {
        await using var invocation = await VendorInvocation.CreateAsync(operationAttempt: 1);
        using var process = StartHelper(
            invocation,
            "--mode",
            "SpawnChildDelay",
            "--delay-milliseconds",
            "300000");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var childProcessIdPath = Path.Combine(invocation.OutputDirectory, "child-process-id.txt");
        await WaitForFileAsync(childProcessIdPath);
        var childProcessIdText = await File.ReadAllTextAsync(childProcessIdPath, Encoding.UTF8);
        var childProcessId = int.Parse(childProcessIdText, NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.True(IsProcessRunning(childProcessId));

        await File.WriteAllTextAsync(
            Path.Combine(invocation.OutputDirectory, "cancel.request"),
            "cancel",
            Encoding.UTF8);
        await WaitForExitAsync(process);
        await WaitForProcessExitAsync(childProcessId);

        Assert.Equal(130, process.ExitCode);
        Assert.Empty(await standardOutput);
        Assert.Contains("acknowledged cancellation", await standardError, StringComparison.Ordinal);
        Assert.False(IsProcessRunning(childProcessId));
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
        using var process = StartHelper(invocation, arguments);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForExitAsync(process);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static Process StartHelper(
        VendorInvocation invocation,
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
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

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

    private static async Task WaitForProcessExitAsync(int processId)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeoutMilliseconds);
        while (IsProcessRunning(processId))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
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

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
