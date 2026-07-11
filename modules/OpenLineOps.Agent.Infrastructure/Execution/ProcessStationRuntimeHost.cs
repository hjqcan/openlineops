using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed record ProcessStationRuntimeHostOptions(
    string ExecutablePath,
    string WorkingDirectoryRoot,
    string ArtifactRoot,
    TimeSpan Timeout,
    int MaximumStandardOutputBytes = 1024 * 1024,
    int MaximumProcessCount = 64,
    long MaximumProcessMemoryBytes = 1024L * 1024 * 1024,
    long MaximumJobMemoryBytes = 4L * 1024 * 1024 * 1024,
    TimeSpan? MaximumCpuTime = null,
    bool RequireRestrictedExternalProgramHostIdentity = false,
    IReadOnlyCollection<string>? AllowedRestrictedExternalProgramHostAccounts = null,
    IReadOnlyCollection<string>? AllowedRestrictedExternalProgramHostSids = null,
    bool RequireExternalProgramAppContainerIsolation = false,
    string? ExternalProgramAppContainerProfileNamespace = null,
    bool RequireImmutableExternalProgramContent = false);

public sealed class ProcessStationRuntimeHost : IStationRuntimeHost, IStationRuntimeIsolationCleaner
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationOperationDocumentJson.CreateOptions();

    private readonly string _executablePath;
    private readonly string _workingDirectoryRoot;
    private readonly string _artifactRoot;
    private readonly TimeSpan _timeout;
    private readonly int _maximumStandardOutputBytes;
    private readonly IsolatedProcessLauncher _processLauncher;
    private readonly IClock _clock;
    private readonly WindowsProcessLimits _processLimits;
    private readonly bool _requireRestrictedExternalProgramHostIdentity;
    private readonly string[] _allowedRestrictedExternalProgramHostAccounts;
    private readonly string[] _allowedRestrictedExternalProgramHostSids;
    private readonly bool _requireExternalProgramAppContainerIsolation;
    private readonly string? _externalProgramAppContainerProfileNamespace;
    private readonly bool _requireImmutableExternalProgramContent;
    private readonly IStationResourceFenceValidator _resourceFenceValidator;

    public ProcessStationRuntimeHost(
        ProcessStationRuntimeHostOptions options,
        IStationResourceFenceValidator resourceFenceValidator,
        IsolatedProcessLauncher? processLauncher = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _resourceFenceValidator = resourceFenceValidator
            ?? throw new ArgumentNullException(nameof(resourceFenceValidator));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ArtifactRoot);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Station runtime timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumStandardOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumProcessCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumProcessMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumJobMemoryBytes);
        _executablePath = Path.GetFullPath(options.ExecutablePath);
        _workingDirectoryRoot = Path.GetFullPath(options.WorkingDirectoryRoot);
        _artifactRoot = Path.GetFullPath(options.ArtifactRoot);
        _timeout = options.Timeout;
        _maximumStandardOutputBytes = options.MaximumStandardOutputBytes;
        _processLauncher = processLauncher ?? new IsolatedProcessLauncher();
        _clock = clock ?? new SystemUtcClock();
        _processLimits = new WindowsProcessLimits(
            options.MaximumProcessCount,
            options.MaximumProcessMemoryBytes,
            options.MaximumJobMemoryBytes,
            options.MaximumCpuTime ?? options.Timeout);
        _processLimits.Validate();
        _requireRestrictedExternalProgramHostIdentity =
            options.RequireRestrictedExternalProgramHostIdentity;
        _allowedRestrictedExternalProgramHostAccounts =
            options.AllowedRestrictedExternalProgramHostAccounts?.ToArray() ?? [];
        _allowedRestrictedExternalProgramHostSids =
            options.AllowedRestrictedExternalProgramHostSids?.ToArray() ?? [];
        _requireExternalProgramAppContainerIsolation =
            options.RequireExternalProgramAppContainerIsolation;
        _externalProgramAppContainerProfileNamespace =
            options.ExternalProgramAppContainerProfileNamespace;
        _requireImmutableExternalProgramContent = options.RequireImmutableExternalProgramContent;
        if (_requireRestrictedExternalProgramHostIdentity
            && _allowedRestrictedExternalProgramHostAccounts.Length == 0
            && _allowedRestrictedExternalProgramHostSids.Length == 0)
        {
            throw new ArgumentException(
                "Restricted external program hosting requires an allowed service account or SID.",
                nameof(options));
        }

        if (_requireExternalProgramAppContainerIsolation
            && (string.IsNullOrWhiteSpace(_externalProgramAppContainerProfileNamespace)
                || _externalProgramAppContainerProfileNamespace.Length > 128
                || char.IsWhiteSpace(_externalProgramAppContainerProfileNamespace[0])
                || char.IsWhiteSpace(_externalProgramAppContainerProfileNamespace[^1])
                || _externalProgramAppContainerProfileNamespace.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "External program AppContainer isolation requires a canonical profile namespace.",
                nameof(options));
        }

        if (!_requireExternalProgramAppContainerIsolation
            && _externalProgramAppContainerProfileNamespace is not null)
        {
            throw new ArgumentException(
                "An AppContainer profile namespace cannot be configured when AppContainer isolation is disabled.",
                nameof(options));
        }
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("Station runtime executable does not exist.", _executablePath);
        }

        Directory.CreateDirectory(_workingDirectoryRoot);
        Directory.CreateDirectory(_artifactRoot);
        RejectReparsePoint(_workingDirectoryRoot, "Station runtime working directory root");
    }

    public async ValueTask<StationOperationExecutionResult> ExecuteAsync(
        StationRuntimeExecutionRequest request,
        Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reportProgress);
        var nowUtc = _clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Station runtime host clock must use UTC offset zero.");
        }

        var earliestFenceExpiry = request.Job.ResourceFences.Min(static fence => fence.ExpiresAtUtc);
        if (earliestFenceExpiry <= nowUtc + _timeout)
        {
            return Failure(
                ExecutionStatus.Rejected,
                "Agent.ResourceFenceDurationInsufficient",
                $"Station runtime maximum duration {_timeout} exceeds the remaining resource lease window.");
        }

        var workDirectory = Path.Combine(
            _workingDirectoryRoot,
            $"{request.Job.JobId.Value:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var requestPath = Path.Combine(workDirectory, "request.json");
        var resultPath = Path.Combine(workDirectory, "result.json");
        var fenceAuthority = new StationResourceFenceAuthorityServer(
            request.Job,
            _resourceFenceValidator);
        using var fenceAuthorityCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var fenceAuthorityTask = fenceAuthority.RunAsync(fenceAuthorityCancellation.Token);
        try
        {
            var requestDocument = CreateRequestDocument(request, fenceAuthority.Descriptor);
            StationOperationDocumentJson.Validate(requestDocument);
            await using (var requestStream = new FileStream(
                             requestPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        requestStream,
                        requestDocument,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await requestStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await reportProgress(
                    new StationOperationProgress(10, "starting-runtime"),
                    cancellationToken)
                .ConfigureAwait(false);

            using var process = _processLauncher.Launch(
                CreateStartRequest(
                    request,
                    workDirectory,
                    requestPath,
                    resultPath,
                    ResolveAppContainerProfileName(request.Job)));
            process.StandardInput.Dispose();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var standardOutput = new StreamReader(
                process.StandardOutput,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            using var standardError = new StreamReader(
                process.StandardError,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            var stdoutTask = ReadBoundedAsync(
                standardOutput,
                _maximumStandardOutputBytes,
                process.TerminateProcessTree);
            var stderrTask = ReadBoundedAsync(
                standardError,
                _maximumStandardOutputBytes,
                process.TerminateProcessTree);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                process.TerminateProcessTree();
                await WaitForTerminationBoundedAsync(process).ConfigureAwait(false);
                await ObserveOutputBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                process.TerminateProcessTree();
                await WaitForTerminationBoundedAsync(process).ConfigureAwait(false);
                await ObserveOutputBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);
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

            StationOperationResultDocument result;
            try
            {
                var resultJson = await File.ReadAllTextAsync(resultPath, cancellationToken)
                    .ConfigureAwait(false);
                result = JsonSerializer.Deserialize<StationOperationResultDocument>(resultJson, JsonOptions)
                    ?? throw new JsonException("Station runtime result is null.");
            }
            catch (JsonException exception)
            {
                return Failure(
                    ExecutionStatus.Failed,
                    "Agent.RuntimeResultInvalid",
                    exception.Message);
            }

            StationOperationDocumentJson.Validate(result);
            if (result.JobId != request.Job.JobId.Value
                || result.RuntimeSessionId != request.Job.RuntimeSessionId)
            {
                throw new InvalidDataException(
                    "Station runtime result does not match the requested Job and Runtime Session identities.");
            }

            _ = ProductionContextOutputReader.Read(result.Outputs);
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
                result.Steps.Select(static step => new StationJobStepEvidence(
                    step.StepId,
                    step.NodeId,
                    step.ActionId,
                    step.TargetKind,
                    step.TargetId,
                    step.DisplayName,
                    step.Status,
                    step.StartedAtUtc,
                    step.CompletedAtUtc,
                    step.FailureReason)).ToArray(),
                result.Commands.Select(static command => new StationJobCommandEvidence(
                    command.CommandId,
                    command.StepId,
                    command.NodeId,
                    command.ActionId,
                    command.TargetKind,
                    command.TargetId,
                    command.CapabilityId,
                    command.CommandName,
                    command.Status,
                    command.CreatedAtUtc,
                    command.DeadlineAtUtc,
                    command.AcceptedAtUtc,
                    command.StartedAtUtc,
                    command.CompletedAtUtc,
                    command.ResultPayload,
                    command.FailureReason,
                    command.ResultJudgement)).ToArray(),
                result.Incidents.Select(static incident => new StationJobIncidentEvidence(
                    incident.IncidentId,
                    incident.Severity,
                    incident.Code,
                    incident.Message,
                    incident.OccurredAtUtc)).ToArray(),
                result.CompletedStepCount,
                result.CommandCount,
                result.IncidentCount,
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
            fenceAuthorityCancellation.Cancel();
            try
            {
                try
                {
                    await fenceAuthorityTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (fenceAuthorityCancellation.IsCancellationRequested)
                {
                }
            }
            finally
            {
                await CleanupAsync(request.Job, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public ValueTask CleanupAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<Exception>();
        try
        {
            foreach (var workDirectory in ResolveJobWorkingDirectories(job.JobId))
            {
                DeleteWorkingDirectory(workDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException)
        {
            failures.Add(exception);
        }

        var profileName = ResolveAppContainerProfileName(job);
        if (profileName is not null)
        {
            try
            {
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            }
            catch (Exception exception) when (exception is Win32Exception
                                              or ArgumentException
                                              or InvalidOperationException)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new StationRuntimeIsolationCleanupException(
                $"Could not clean Station runtime isolation for Job {job.JobId}.",
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }

        return ValueTask.CompletedTask;
    }

    private string[] ResolveJobWorkingDirectories(StationJobId jobId)
    {
        if (!Directory.Exists(_workingDirectoryRoot))
        {
            return [];
        }

        RejectReparsePoint(_workingDirectoryRoot, "Station runtime working directory root");
        var prefix = $"{jobId.Value:N}-";
        return Directory.GetDirectories(
                _workingDirectoryRoot,
                prefix + "*",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var leaf = Path.GetFileName(path);
                return leaf.Length == prefix.Length + 32
                       && leaf.StartsWith(prefix, StringComparison.Ordinal)
                       && Guid.TryParseExact(leaf[prefix.Length..], "N", out _);
            })
            .ToArray();
    }

    private static StationOperationRequestDocument CreateRequestDocument(
        StationRuntimeExecutionRequest request,
        StationResourceFenceAuthorityDescriptor resourceFenceAuthority)
    {
        using var inputs = JsonDocument.Parse(request.Job.InputsJson);
        return new StationOperationRequestDocument(
            StationOperationDocumentContract.RequestSchema,
            request.Job.JobId.Value,
            request.Job.IdempotencyKey,
            request.Job.AgentId,
            request.Job.StationId,
            request.Job.StationSystemId,
            request.Job.ProductionRunId,
            request.Job.ProductionUnitId,
            request.Job.RuntimeSessionId,
            request.Job.ProductionLineDefinitionId,
            request.Job.TopologyId,
            request.Job.ActorId,
            request.Job.OperationRunId.Value,
            request.Job.OperationAttempt,
            request.Job.ProductModelId,
            request.Job.ProductionUnitIdentityInputKey,
            request.Job.ProductionUnitIdentityValue,
            request.Job.LotId,
            request.Job.CarrierId,
            request.Job.ProjectId,
            request.Job.ApplicationId,
            request.Job.ProjectSnapshotId,
            request.Job.PackageContentSha256,
            Path.GetFullPath(request.PackageContentDirectory),
            request.Job.OperationId,
            request.Job.FlowDefinitionId,
            request.Job.FlowVersionId,
            request.Job.ConfigurationSnapshotId,
            request.Job.RecipeSnapshotId,
            resourceFenceAuthority,
            request.Job.ResourceFences.Select(static fence => new StationOperationResourceFence(
                fence.ResourceKind,
                fence.ResourceId,
                fence.FencingToken,
                fence.ExpiresAtUtc)).ToArray(),
            inputs.RootElement.Clone(),
            request.Job.RequestedAtUtc);
    }

    private IsolatedProcessStartRequest CreateStartRequest(
        StationRuntimeExecutionRequest request,
        string workDirectory,
        string requestPath,
        string resultPath,
        string? appContainerProfileName)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyEnvironment(environment, "SystemRoot");
        CopyEnvironment(environment, "WINDIR");
        CopyEnvironment(environment, "PATH");
        environment["TEMP"] = workDirectory;
        environment["TMP"] = workDirectory;
        environment["OpenLineOps__Devices__ExternalProgramHost__RequireRestrictedHostIdentity"] =
            _requireRestrictedExternalProgramHostIdentity ? "true" : "false";
        environment["OpenLineOps__Devices__ExternalProgramHost__RequireImmutableContentProtection"] =
            _requireImmutableExternalProgramContent ? "true" : "false";
        environment["OpenLineOps__Devices__ExternalProgramHost__RequireAppContainerIsolation"] =
            _requireExternalProgramAppContainerIsolation ? "true" : "false";
        if (appContainerProfileName is not null)
        {
            environment["OpenLineOps__Devices__ExternalProgramHost__AppContainerProfileName"] =
                appContainerProfileName;
            environment["OpenLineOps__Devices__ExternalProgramHost__AppContainerProfileExternallyOwned"] =
                "true";
        }

        var accountIndex = 0;
        foreach (var account in _allowedRestrictedExternalProgramHostAccounts)
        {
            environment[$"OpenLineOps__Devices__ExternalProgramHost__AllowedRestrictedHostAccounts__{accountIndex}"] =
                account;
            accountIndex++;
        }

        var sidIndex = 0;
        foreach (var sid in _allowedRestrictedExternalProgramHostSids)
        {
            environment[$"OpenLineOps__Devices__ExternalProgramHost__AllowedRestrictedHostSids__{sidIndex}"] = sid;
            sidIndex++;
        }
        var arguments = new List<string>();
        AddArgument(arguments, "execute-operation");
        AddOption(arguments, "request-file", requestPath);
        AddOption(arguments, "result-file", resultPath);
        return new IsolatedProcessStartRequest(
            _executablePath,
            arguments,
            workDirectory,
            environment,
            _processLimits);
    }

    private string? ResolveAppContainerProfileName(StationJobSnapshot job) =>
        _requireExternalProgramAppContainerIsolation
            ? StationRuntimeIsolationProfile.CreateName(
                _externalProgramAppContainerProfileNamespace!,
                job.AgentId,
                job.StationId,
                job.JobId)
            : null;

    private async ValueTask<IReadOnlyCollection<StationOperationArtifact>> PreserveArtifactsAsync(
        Guid jobId,
        string workDirectory,
        IReadOnlyList<StationOperationArtifactEvidence> artifacts,
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
            if (info.Length != artifact.SizeBytes
                || !string.Equals(sha256, artifact.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Station runtime artifact '{relativePath}' differs from its declared evidence.");
            }

            File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
            result.Add(new StationOperationArtifact(
                artifact.Name,
                artifact.Kind,
                $"{jobId:N}/{relativePath}",
                artifact.MediaType,
                info.Length,
                sha256));
        }

        return result;
    }

    private static StationOperationExecutionResult Failure(
        ExecutionStatus status,
        string code,
        string reason)
    {
        var canonicalReason = CanonicalFailureReason(reason);
        return new StationOperationExecutionResult(
            status,
            ResultJudgement.Unknown,
            "{}",
            [],
            [],
            [],
            [new StationJobIncidentEvidence(
                Guid.NewGuid(),
                "Error",
                code,
                canonicalReason,
                DateTimeOffset.UtcNow)],
            0,
            0,
            1,
            code,
            canonicalReason);
    }

    private static string CanonicalFailureReason(string reason)
    {
        var normalized = new string(reason
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        if (normalized.Length == 0)
        {
            normalized = "Station runtime failed without a diagnostic message.";
        }

        return normalized.Length <= 4096 ? normalized : normalized[..4096];
    }

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

    private static async Task ObserveOutputBoundedAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or TimeoutException)
        {
            _ = exception;
        }
    }

    private static async Task WaitForTerminationBoundedAsync(IIsolatedProcess process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void AddOption(ICollection<string> arguments, string name, string value)
    {
        AddArgument(arguments, $"--{name}");
        AddArgument(arguments, value);
    }

    private static void AddArgument(ICollection<string> arguments, string value) =>
        arguments.Add(value);

    private static void CopyEnvironment(Dictionary<string, string> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment[name] = value;
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

        RejectReparsePoint(path, "Station runtime Job working directory");
        DeleteDirectoryContents(path);
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteDirectoryContents(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteDirectoryContents(child);
            }

            File.SetAttributes(child, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(child, recursive: false);
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} cannot be a reparse point.");
        }
    }

    private sealed class SystemUtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

public static class StationRuntimeIsolationProfile
{
    public static string CreateName(
        string profileNamespace,
        string agentId,
        string stationId,
        StationJobId jobId)
    {
        ValidateIdentity(profileNamespace, nameof(profileNamespace), 128);
        ValidateIdentity(agentId, nameof(agentId), 512);
        ValidateIdentity(stationId, nameof(stationId), 512);
        var input = Encoding.UTF8.GetBytes(
            string.Join('\0', profileNamespace, agentId, stationId, jobId.Value.ToString("D")));
        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        return "OpenLineOps.Job." + digest[..40];
    }

    private static void ValidateIdentity(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Station runtime isolation identity components must be canonical text.",
                parameterName);
        }
    }
}
