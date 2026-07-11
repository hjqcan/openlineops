using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed record ProcessStationRuntimeHostOptions(
    string ExecutablePath,
    string WorkingDirectoryRoot,
    string ArtifactRoot,
    TimeSpan Timeout,
    int MaximumStandardOutputBytes = 1024 * 1024);

public sealed class ProcessStationRuntimeHost : IStationRuntimeHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _executablePath;
    private readonly string _workingDirectoryRoot;
    private readonly string _artifactRoot;
    private readonly TimeSpan _timeout;
    private readonly int _maximumStandardOutputBytes;

    public ProcessStationRuntimeHost(ProcessStationRuntimeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ArtifactRoot);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Station runtime timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumStandardOutputBytes);
        _executablePath = Path.GetFullPath(options.ExecutablePath);
        _workingDirectoryRoot = Path.GetFullPath(options.WorkingDirectoryRoot);
        _artifactRoot = Path.GetFullPath(options.ArtifactRoot);
        _timeout = options.Timeout;
        _maximumStandardOutputBytes = options.MaximumStandardOutputBytes;
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("Station runtime executable does not exist.", _executablePath);
        }

        Directory.CreateDirectory(_workingDirectoryRoot);
        Directory.CreateDirectory(_artifactRoot);
    }

    public async ValueTask<StationOperationExecutionResult> ExecuteAsync(
        StationRuntimeExecutionRequest request,
        Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reportProgress);
        var workDirectory = Path.Combine(
            _workingDirectoryRoot,
            $"{request.Job.JobId.Value:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var inputsPath = Path.Combine(workDirectory, "inputs.json");
        var resultPath = Path.Combine(workDirectory, "result.json");
        try
        {
            await File.WriteAllTextAsync(
                    inputsPath,
                    request.Job.InputsJson,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            await reportProgress(
                    new StationOperationProgress(10, "starting-runtime"),
                    cancellationToken)
                .ConfigureAwait(false);

            using var process = new Process
            {
                StartInfo = CreateStartInfo(request, workDirectory, inputsPath, resultPath),
                EnableRaisingEvents = true
            };
            if (!process.Start())
            {
                throw new InvalidOperationException("Station runtime process did not start.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            var stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                _maximumStandardOutputBytes,
                () => Kill(process));
            var stderrTask = ReadBoundedAsync(
                process.StandardError,
                _maximumStandardOutputBytes,
                () => Kill(process));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                await ObserveOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await ObserveOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return Failure(
                    ExecutionStatus.TimedOut,
                    "Agent.RuntimeTimedOut",
                    $"Station runtime exceeded {_timeout}.");
            }

            string stdout;
            string stderr;
            try
            {
                (stdout, stderr) = await ReadOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                return Failure(
                    ExecutionStatus.Failed,
                    "Agent.RuntimeOutputLimitExceeded",
                    exception.Message);
            }

            if (process.ExitCode != 0)
            {
                return Failure(
                    ExecutionStatus.Failed,
                    "Agent.RuntimeProcessFailed",
                    $"Station runtime exited with code {process.ExitCode}. stderr={stderr}");
            }

            if (!File.Exists(resultPath))
            {
                return Failure(
                    ExecutionStatus.Failed,
                    "Agent.RuntimeResultMissing",
                    $"Station runtime produced no result. stdout={stdout}; stderr={stderr}");
            }

            StationRuntimeProcessResult result;
            try
            {
                var resultJson = await File.ReadAllTextAsync(resultPath, cancellationToken)
                    .ConfigureAwait(false);
                result = JsonSerializer.Deserialize<StationRuntimeProcessResult>(resultJson, JsonOptions)
                    ?? throw new JsonException("Station runtime result is null.");
            }
            catch (JsonException exception)
            {
                return Failure(
                    ExecutionStatus.Failed,
                    "Agent.RuntimeResultInvalid",
                    exception.Message);
            }

            ValidateResult(result);
            var artifacts = await PreserveArtifactsAsync(
                    request.Job.JobId.Value,
                    workDirectory,
                    result.Artifacts,
                    cancellationToken)
                .ConfigureAwait(false);
            await reportProgress(
                    new StationOperationProgress(95, "runtime-finished"),
                    cancellationToken)
                .ConfigureAwait(false);
            return new StationOperationExecutionResult(
                result.ExecutionStatus,
                result.Judgement,
                JsonSerializer.Serialize(result.Outputs),
                artifacts,
                result.FailureCode,
                result.FailureReason);
        }
        catch (InvalidDataException exception)
        {
            return Failure(
                ExecutionStatus.Failed,
                "Agent.RuntimeProtocolInvalid",
                exception.Message);
        }
        finally
        {
            DeleteWorkingDirectory(workDirectory);
        }
    }

    private ProcessStartInfo CreateStartInfo(
        StationRuntimeExecutionRequest request,
        string workDirectory,
        string inputsPath,
        string resultPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "WINDIR");
        CopyEnvironment(startInfo, "PATH");
        startInfo.Environment["TEMP"] = workDirectory;
        startInfo.Environment["TMP"] = workDirectory;
        AddArgument(startInfo, "execute-operation");
        AddOption(startInfo, "package", request.PackageContentDirectory);
        AddOption(startInfo, "job-id", request.Job.JobId.Value.ToString("D"));
        AddOption(startInfo, "run-id", request.Job.ProductionRunId.ToString("D"));
        AddOption(startInfo, "operation-run-id", request.Job.OperationRunId.Value);
        AddOption(startInfo, "operation-attempt", request.Job.OperationAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddOption(startInfo, "product-model", request.Job.ProductModelId);
        AddOption(startInfo, "identity-input", request.Job.ProductionUnitIdentityInputKey);
        AddOption(startInfo, "production-unit", request.Job.ProductionUnitIdentityValue);
        AddOption(startInfo, "operation", request.Job.OperationId);
        AddOption(startInfo, "flow", request.Job.FlowDefinitionId);
        AddOption(startInfo, "flow-version", request.Job.FlowVersionId);
        AddOption(startInfo, "configuration", request.Job.ConfigurationSnapshotId);
        AddOption(startInfo, "recipe", request.Job.RecipeSnapshotId);
        AddOption(startInfo, "inputs-file", inputsPath);
        AddOption(startInfo, "result-file", resultPath);
        return startInfo;
    }

    private async ValueTask<IReadOnlyCollection<StationOperationArtifact>> PreserveArtifactsAsync(
        Guid jobId,
        string workDirectory,
        IReadOnlyList<StationRuntimeProcessArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        var jobArtifactDirectory = Path.Combine(_artifactRoot, jobId.ToString("N"));
        Directory.CreateDirectory(jobArtifactDirectory);
        var result = new List<StationOperationArtifact>(artifacts.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            var relativePath = CanonicalRelativePath(artifact.RelativePath);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"Station runtime returned duplicate artifact '{relativePath}'.");
            }

            var sourcePath = ResolveInside(workDirectory, relativePath);
            if (!File.Exists(sourcePath))
            {
                throw new InvalidDataException(
                    $"Station runtime artifact '{relativePath}' does not exist.");
            }

            var targetPath = ResolveInside(jobArtifactDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(targetPath);
            await using var hashSource = new FileStream(
                targetPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(hashSource, cancellationToken).ConfigureAwait(false));
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
            result.Add(new StationOperationArtifact(
                $"{jobId:N}/{relativePath}",
                Required(artifact.MediaType, nameof(artifact.MediaType)),
                info.Length,
                sha256));
        }

        return result;
    }

    private static void ValidateResult(StationRuntimeProcessResult result)
    {
        if (result.ExecutionStatus is ExecutionStatus.Pending or ExecutionStatus.Running)
        {
            throw new InvalidDataException("Station runtime returned a non-terminal execution status.");
        }

        var hasFailure = !string.IsNullOrWhiteSpace(result.FailureCode)
            && !string.IsNullOrWhiteSpace(result.FailureReason);
        if (result.ExecutionStatus == ExecutionStatus.Completed && hasFailure)
        {
            throw new InvalidDataException("Completed station runtime result cannot contain a system failure.");
        }

        if (result.ExecutionStatus != ExecutionStatus.Completed && !hasFailure)
        {
            throw new InvalidDataException("Unsuccessful station runtime result requires failure evidence.");
        }

        if (result.ExecutionStatus != ExecutionStatus.Completed
            && result.Judgement != ResultJudgement.Unknown)
        {
            throw new InvalidDataException("Unsuccessful execution must use Unknown judgement.");
        }
    }

    private static StationOperationExecutionResult Failure(
        ExecutionStatus status,
        string code,
        string reason) => new(
        status,
        ResultJudgement.Unknown,
        "{}",
        [],
        code,
        reason.Length <= 4096 ? reason : reason[..4096]);

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumBytes,
        Action limitExceeded)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var byteCount = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            byteCount = checked(byteCount + Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count)));
            if (byteCount > maximumBytes)
            {
                limitExceeded();
                throw new InvalidDataException(
                    $"Station runtime standard output exceeded {maximumBytes} bytes.");
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static async Task<(string Stdout, string Stderr)> ReadOutputAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task ObserveOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void AddOption(ProcessStartInfo startInfo, string name, string value)
    {
        AddArgument(startInfo, $"--{name}");
        AddArgument(startInfo, value);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value) =>
        startInfo.ArgumentList.Add(value);

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static string CanonicalRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\')
            || path.StartsWith('/')
            || path.EndsWith('/'))
        {
            throw new InvalidDataException("Artifact path must be canonical and relative.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"Artifact path '{path}' contains an unsafe segment.");
        }

        return path;
    }

    private static string ResolveInside(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new InvalidDataException($"Path '{relativePath}' escapes its station runtime root.");
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException($"{parameterName} must be canonical non-empty text.")
            : value;

    private static void DeleteWorkingDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed record StationRuntimeProcessResult(
        ExecutionStatus ExecutionStatus,
        ResultJudgement Judgement,
        JsonElement Outputs,
        IReadOnlyList<StationRuntimeProcessArtifact> Artifacts,
        string? FailureCode,
        string? FailureReason);

    private sealed record StationRuntimeProcessArtifact(string RelativePath, string MediaType);
}
