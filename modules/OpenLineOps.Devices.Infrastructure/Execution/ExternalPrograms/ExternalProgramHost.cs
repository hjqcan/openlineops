using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.ContentProtection;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

public sealed class ExternalProgramHost : IExternalProgramHost
{
    private const int BufferSize = 64 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly ExternalProgramHostOptions _options;
    private readonly string _workspaceRootPath;
    private readonly string _evidenceRootPath;
    private readonly IsolatedProcessLauncher _processLauncher;
    private readonly IImmutableContentProtector _contentProtector;
    private readonly ExternalProgramHostPolicyEnforcer _policyEnforcer;

    public ExternalProgramHost(ExternalProgramHostOptions options)
        : this(options, null, null, null)
    {
    }

    internal ExternalProgramHost(
        ExternalProgramHostOptions options,
        IsolatedProcessLauncher? processLauncher,
        IImmutableContentProtector? contentProtector,
        ExternalProgramHostPolicyEnforcer? policyEnforcer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _workspaceRootPath = _options.ResolveWorkspaceRootPath();
        _evidenceRootPath = _options.ResolveEvidenceRootPath();
        _processLauncher = processLauncher ?? new IsolatedProcessLauncher();
        _contentProtector = contentProtector ?? new ImmutableContentProtector();
        _policyEnforcer = policyEnforcer ?? new ExternalProgramHostPolicyEnforcer(_options);
    }

    public async ValueTask<ExternalProgramExecutionResult> ExecuteAsync(
        ExternalProgramExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return ExternalProgramExecutionResult.Rejected(validationError);
        }

        EffectiveExternalProgramPolicy effectivePolicy;
        ExternalProgramHostIdentity hostIdentity;
        WindowsAppContainerPolicy? appContainerPolicy;
        string contentReaderSid;
        string resourceRootPath;
        string executablePath;
        ImmutableContentFile[] immutableInventory;
        try
        {
            effectivePolicy = EffectiveExternalProgramPolicy.Create(_options, request);
            hostIdentity = _policyEnforcer.Enforce(request.Policy);
            appContainerPolicy = _options.RequireAppContainerIsolation
                ? new WindowsAppContainerPolicy(
                    _options.AppContainerProfileExternallyOwned
                        ? _options.AppContainerProfileName!
                        : CreateInvocationProfileName(_options.AppContainerProfileName!),
                    request.Policy.NetworkAccessAllowed,
                    [WindowsAppContainerIdentity.ExternalProgramContentCapabilityName])
                : null;
            contentReaderSid = appContainerPolicy is null
                ? hostIdentity.ServiceSid
                : WindowsAppContainerIdentity.EnsureCapabilitySid(
                    WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
            resourceRootPath = ResolveFrozenPath(
                request.ReleaseApplicationRootPath,
                request.ResourceRootRelativePath,
                requireFile: false);
            if (!Directory.Exists(resourceRootPath))
            {
                throw new DirectoryNotFoundException(
                    $"Frozen external program resource '{request.ResourceRootRelativePath}' does not exist.");
            }

            immutableInventory = request.Files
                .Select(file => new ImmutableContentFile(
                    file.RelativePath,
                    file.SizeBytes,
                    file.Sha256))
                .ToArray();
            var entryPointRelativeToResource = RelativeEntryPoint(request);
            var entryPointInventory = request.Files
                .Where(file => string.Equals(
                    file.RelativePath,
                    entryPointRelativeToResource,
                    PathComparison))
                .Take(2)
                .ToArray();
            if (entryPointInventory.Length != 1
                || entryPointInventory[0].SizeBytes != request.EntryPointSizeBytes
                || !string.Equals(
                    entryPointInventory[0].Sha256,
                    request.EntryPointSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Frozen external program entry point is absent or differs from the complete resource inventory.");
            }

            await VerifyFrozenResourceAsync(
                    resourceRootPath,
                    immutableInventory,
                    contentReaderSid,
                    hostIdentity.ServiceSid,
                    cancellationToken)
                .ConfigureAwait(false);
            executablePath = ResolveFrozenPath(
                request.ReleaseApplicationRootPath,
                request.EntryPointRelativePath,
                requireFile: true);
        }
        catch (OperationCanceledException)
        {
            return ExternalProgramExecutionResult.Canceled(
                $"External program '{request.ResourceId}' was canceled before launch.",
                []);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or InvalidOperationException
                                          or Win32Exception)
        {
            return ExternalProgramExecutionResult.Rejected(
                $"External program '{request.ResourceId}' frozen executable is invalid: {exception.Message}");
        }

        var workspacePath = CreateWorkspace();
        var outputDirectory = Path.Combine(workspacePath, "output");
        var temporaryDirectory = Path.Combine(workspacePath, "temp");
        var standardOutputPath = Path.Combine(workspacePath, "stdout.log");
        var standardErrorPath = Path.Combine(workspacePath, "stderr.log");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(temporaryDirectory);

        ExternalProgramExecutionResult result;
        try
        {
            var invocationPath = Path.Combine(workspacePath, "invocation.json");
            await File.WriteAllTextAsync(
                    invocationPath,
                    request.InvocationPayload,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            if (appContainerPolicy is not null)
            {
                var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(
                    appContainerPolicy.ProfileName);
                WindowsContentAccessAuthorizer.GrantWorkspaceModify(
                    workspacePath,
                    appContainerSid);
            }

            var startInfo = CreateStartInfo(
                request,
                effectivePolicy,
                resourceRootPath,
                executablePath,
                workspacePath,
                outputDirectory,
                temporaryDirectory,
                invocationPath,
                appContainerPolicy);
            result = await ExecuteProcessAsync(
                    request,
                    effectivePolicy,
                    startInfo,
                    workspacePath,
                    outputDirectory,
                    standardOutputPath,
                    standardErrorPath,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await VerifyFrozenResourceAsync(
                        resourceRootPath,
                        immutableInventory,
                        contentReaderSid,
                        hostIdentity.ServiceSid,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                result = ExternalProgramExecutionResult.Failed(
                    $"External program '{request.ResourceId}' modified its frozen resource: "
                    + exception.Message,
                    result.Artifacts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = ExternalProgramExecutionResult.Canceled(
                $"External program '{request.ResourceId}' was canceled.",
                []);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or NotSupportedException
                                          or InvalidOperationException
                                          or Win32Exception)
        {
            result = ExternalProgramExecutionResult.Failed(
                $"External program '{request.ResourceId}' host failed: {exception.Message}",
                []);
        }

        var workspaceCleanupError = TryDeleteWorkspace(workspacePath);
        var profileCleanupError = appContainerPolicy is null
                                  || _options.AppContainerProfileExternallyOwned
            ? null
            : TryDeleteAppContainerProfile(appContainerPolicy.ProfileName);
        var cleanupError = workspaceCleanupError ?? profileCleanupError;
        return cleanupError is null
            ? result
            : ExternalProgramExecutionResult.Failed(
                $"External program '{request.ResourceId}' isolation cleanup failed: {cleanupError}",
                result.Artifacts);
    }

    private async ValueTask<ExternalProgramExecutionResult> ExecuteProcessAsync(
        ExternalProgramExecutionRequest request,
        EffectiveExternalProgramPolicy policy,
        IsolatedProcessStartRequest startRequest,
        string workspacePath,
        string outputDirectory,
        string standardOutputPath,
        string standardErrorPath,
        CancellationToken cancellationToken)
    {
        using var process = _processLauncher.Launch(startRequest);
        using var timeoutCancellation = new CancellationTokenSource(policy.ExecutionTimeout);
        using var outputLimitCancellation = new CancellationTokenSource();
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token,
            outputLimitCancellation.Token);
        using var monitorStop = new CancellationTokenSource();
        var standardOutputTask = CaptureStreamAsync(
            process.StandardOutput,
            standardOutputPath,
            policy.MaximumStandardOutputBytes,
            outputLimitCancellation);
        var standardErrorTask = CaptureStreamAsync(
            process.StandardError,
            standardErrorPath,
            policy.MaximumStandardErrorBytes,
            outputLimitCancellation);
        var outputMonitorTask = MonitorOutputDirectoryAsync(
            outputDirectory,
            policy,
            outputLimitCancellation,
            monitorStop.Token);
        string? executionFailure = null;
        var processExited = false;
        try
        {
            var inputBytes = Encoding.UTF8.GetBytes(request.InvocationPayload);
            try
            {
                await process.StandardInput
                    .WriteAsync(inputBytes, executionCancellation.Token)
                    .ConfigureAwait(false);
                await process.StandardInput
                    .FlushAsync(executionCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                process.StandardInput.Dispose();
            }

            await process.WaitForExitAsync(executionCancellation.Token).ConfigureAwait(false);
            processExited = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            executionFailure = exception.Message;
        }

        process.TerminateProcessTree();
        processExited = processExited
                        || await WaitForTerminationBoundedAsync(process).ConfigureAwait(false);
        monitorStop.Cancel();
        var outputQuotaFailure = await outputMonitorTask.ConfigureAwait(false);
        var captures = await AwaitCapturesBoundedAsync(
                process,
                standardOutputTask,
                standardErrorTask)
            .ConfigureAwait(false);
        if (captures is null)
        {
            return ExternalProgramExecutionResult.Failed(
                $"External program '{request.ResourceId}' standard stream shutdown exceeded the host bound.",
                []);
        }

        var excludeIncompleteAtomicWriteResidue = executionFailure is not null
                                                  || outputLimitCancellation.IsCancellationRequested
                                                  || cancellationToken.IsCancellationRequested
                                                  || timeoutCancellation.IsCancellationRequested
                                                  || !processExited
                                                  || process.ExitCode != 0;
        var artifacts = await PersistEvidenceAsync(
                request,
                policy,
                workspacePath,
                outputDirectory,
                standardOutputPath,
                standardErrorPath,
                excludeIncompleteAtomicWriteResidue,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (executionFailure is not null)
        {
            return ExternalProgramExecutionResult.Failed(
                $"External program '{request.ResourceId}' execution failed: {executionFailure}"
                + FormatStandardError(captures.StandardError.Text),
                artifacts);
        }

        if (outputLimitCancellation.IsCancellationRequested)
        {
            return ExternalProgramExecutionResult.Failed(
                outputQuotaFailure is null
                    ? $"External program '{request.ResourceId}' exceeded the streaming output limit."
                    : $"External program '{request.ResourceId}' output workspace exceeded its quota: "
                      + outputQuotaFailure,
                artifacts);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ExternalProgramExecutionResult.Canceled(
                $"External program '{request.ResourceId}' was canceled."
                + FormatStandardError(captures.StandardError.Text),
                artifacts);
        }

        if (timeoutCancellation.IsCancellationRequested)
        {
            return ExternalProgramExecutionResult.TimedOut(
                $"External program '{request.ResourceId}' exceeded its timeout of "
                + $"{policy.ExecutionTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms."
                + FormatStandardError(captures.StandardError.Text),
                artifacts);
        }

        if (!processExited)
        {
            return ExternalProgramExecutionResult.Failed(
                $"External program '{request.ResourceId}' did not terminate within the bounded shutdown interval.",
                artifacts);
        }

        if (process.ExitCode != 0)
        {
            return ExternalProgramExecutionResult.Failed(
                $"External program '{request.ResourceId}' exited with code "
                + $"{process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                + FormatStandardError(captures.StandardError.Text),
                artifacts);
        }

        return ExternalProgramExecutionResult.Completed(
            captures.StandardOutput.Text,
            artifacts);
    }

    private static IsolatedProcessStartRequest CreateStartInfo(
        ExternalProgramExecutionRequest request,
        EffectiveExternalProgramPolicy policy,
        string resourceRootPath,
        string executablePath,
        string workspacePath,
        string outputDirectory,
        string temporaryDirectory,
        string invocationPath,
        WindowsAppContainerPolicy? appContainerPolicy)
    {
        var arguments = new List<string>(request.Arguments.Count);
        foreach (var argument in request.Arguments)
        {
            arguments.Add(ResolveFrozenArgument(
                resourceRootPath,
                request.ResourceRootRelativePath,
                request.Files,
                argument));
        }

        var inherited = policy.InheritedEnvironmentVariables
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => item.Value is not null)
            .ToArray();
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var item in inherited)
        {
            environment[item.Name] = item.Value!;
        }

        environment["TEMP"] = temporaryDirectory;
        environment["TMP"] = temporaryDirectory;
        environment["OPENLINEOPS_WORKSPACE"] = workspacePath;
        environment["OPENLINEOPS_OUTPUT_DIRECTORY"] = outputDirectory;
        environment["OPENLINEOPS_INVOCATION_FILE"] = invocationPath;
        environment["OPENLINEOPS_PRODUCTION_RUN_ID"] = request.ProductionRunId.ToString("D");
        environment["OPENLINEOPS_RUNTIME_COMMAND_ID"] = request.RuntimeCommandId.ToString("D");
        if (appContainerPolicy is not null)
        {
            environment["LOCALAPPDATA"] = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
        }

        return new IsolatedProcessStartRequest(
            executablePath,
            arguments,
            workspacePath,
            environment,
            policy.ProcessLimits,
            appContainerPolicy);
    }

    private async ValueTask<IReadOnlyCollection<ExternalProgramArtifact>> PersistEvidenceAsync(
        ExternalProgramExecutionRequest request,
        EffectiveExternalProgramPolicy policy,
        string workspacePath,
        string outputDirectory,
        string standardOutputPath,
        string standardErrorPath,
        bool excludeIncompleteAtomicWriteResidue,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string SourcePath, string RelativePath)>
        {
            (standardOutputPath, "stdout.log"),
            (standardErrorPath, "stderr.log")
        };
        foreach (var path in EnumerateOutputFiles(outputDirectory))
        {
            var outputRelativePath = Path.GetRelativePath(outputDirectory, path).Replace('\\', '/');
            if (excludeIncompleteAtomicWriteResidue
                && IsIncompleteAtomicWriteResidue(outputRelativePath))
            {
                continue;
            }

            if (candidates.Count == policy.MaximumArtifactCount)
            {
                throw new InvalidDataException(
                    $"External program produced more than {policy.MaximumArtifactCount} artifacts.");
            }

            candidates.Add((
                path,
                "output/" + outputRelativePath));
        }

        if (candidates.Count > policy.MaximumArtifactCount)
        {
            throw new InvalidDataException(
                $"External program produced {candidates.Count} artifacts; limit is {policy.MaximumArtifactCount}.");
        }

        long totalSize = 0;
        var artifacts = new List<ExternalProgramArtifact>(candidates.Count);
        foreach (var candidate in candidates.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            RejectInvalidEvidencePath(workspacePath, candidate.SourcePath, candidate.RelativePath);
            var file = new FileInfo(candidate.SourcePath);
            if (file.Length > policy.MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"External program artifact '{candidate.RelativePath}' exceeds the per-artifact limit.");
            }

            totalSize = checked(totalSize + file.Length);
            if (totalSize > policy.MaximumTotalArtifactBytes)
            {
                throw new InvalidDataException("External program artifacts exceed the total evidence limit.");
            }

            var storageKey = "external-programs/"
                             + request.ProductionRunId.ToString("N")
                             + "/"
                             + request.RuntimeCommandId.ToString("N")
                             + "/"
                             + candidate.RelativePath;
            var stored = await StoreEvidenceFileAsync(
                    candidate.SourcePath,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            artifacts.Add(new ExternalProgramArtifact(
                Path.GetFileName(candidate.RelativePath),
                InferArtifactKind(candidate.RelativePath),
                storageKey,
                InferMediaType(candidate.RelativePath),
                stored.SizeBytes,
                stored.Sha256));
        }

        return artifacts;
    }

    private async ValueTask<StoredEvidence> StoreEvidenceFileAsync(
        string sourcePath,
        string storageKey,
        CancellationToken cancellationToken)
    {
        var destinationPath = ResolveContainedPath(_evidenceRootPath, storageKey);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            var buffer = new byte[BufferSize];
            long sizeBytes = 0;
            while (true)
            {
                var bytesRead = await source
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                sizeBytes += bytesRead;
            }

            sha256.TransformFinalBlock([], 0, 0);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Close();
            var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                var existing = await ReadEvidenceIdentityAsync(destinationPath, cancellationToken)
                    .ConfigureAwait(false);
                if (existing.SizeBytes != sizeBytes
                    || !string.Equals(existing.Sha256, hash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Evidence storage key '{storageKey}' already contains different content.");
                }

                File.Delete(temporaryPath);
            }

            return new StoredEvidence(sizeBytes, hash);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async ValueTask<StoredEvidence> ReadEvidenceIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new StoredEvidence(stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static async Task<CapturedStreams?> AwaitCapturesBoundedAsync(
        IIsolatedProcess process,
        Task<CapturedStream> standardOutputTask,
        Task<CapturedStream> standardErrorTask)
    {
        var combined = Task.WhenAll(standardOutputTask, standardErrorTask);
        try
        {
            await combined.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new CapturedStreams(
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            _ = combined.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return null;
        }
    }

    private static async Task<bool> WaitForTerminationBoundedAsync(IIsolatedProcess process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string?> MonitorOutputDirectoryAsync(
        string outputDirectory,
        EffectiveExternalProgramPolicy policy,
        CancellationTokenSource limitCancellation,
        CancellationToken stopToken)
    {
        while (true)
        {
            var violation = InspectOutputDirectorySafely(outputDirectory, policy);
            if (violation is not null)
            {
                limitCancellation.Cancel();
                return violation;
            }

            try
            {
                await Task.Delay(policy.OutputDirectoryScanInterval, stopToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return InspectOutputDirectorySafely(outputDirectory, policy);
            }
        }
    }

    private static string? InspectOutputDirectorySafely(
        string outputDirectory,
        EffectiveExternalProgramPolicy policy)
    {
        return InspectOutputDirectoryWithBoundedSnapshotRetries(
            () => InspectOutputDirectory(outputDirectory, policy));
    }

    internal static string? InspectOutputDirectoryWithBoundedSnapshotRetries(
        Func<string?> inspectSnapshot)
    {
        ArgumentNullException.ThrowIfNull(inspectSnapshot);

        const int maximumSnapshotAttempts = 4;
        for (var attempt = 1; attempt <= maximumSnapshotAttempts; attempt++)
        {
            try
            {
                return inspectSnapshot();
            }
            catch (Exception exception) when (
                attempt < maximumSnapshotAttempts
                && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                Thread.Yield();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"output workspace could not be inspected: {exception.Message}";
            }
        }

        throw new InvalidOperationException("The bounded output workspace inspection did not terminate.");
    }

    internal static bool IsIncompleteAtomicWriteResidue(string relativePath)
    {
        if (!IsCanonical(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split('/');
        if (segments.Length == 0
            || segments[..^1].Any(segment => !IsPortableArtifactPathSegment(segment)))
        {
            return false;
        }

        var fileName = segments[^1];
        const int identifierLength = 32;
        const string suffix = ".tmp";
        var identifierSeparator = fileName.Length - suffix.Length - identifierLength - 1;
        if (identifierSeparator <= 1
            || fileName[0] != '.'
            || fileName[identifierSeparator] != '.'
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var destinationName = fileName[1..identifierSeparator];
        var identifier = fileName.AsSpan(identifierSeparator + 1, identifierLength);
        return IsPortableArtifactPathSegment(destinationName)
               && Guid.TryParseExact(identifier, "N", out _);
    }

    private static string? InspectOutputDirectory(
        string outputDirectory,
        EffectiveExternalProgramPolicy policy)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((outputDirectory, 0));
        var entries = 0;
        var fileCount = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return "reparse points are forbidden";
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                entries++;
                var childDepth = depth + 1;
                if (entries > policy.MaximumOutputDirectoryEntries)
                {
                    return $"entry count exceeds {policy.MaximumOutputDirectoryEntries}";
                }

                if (childDepth > policy.MaximumOutputDirectoryDepth)
                {
                    return $"directory depth exceeds {policy.MaximumOutputDirectoryDepth}";
                }

                pending.Push((childDirectory, childDepth));
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                entries++;
                fileCount++;
                if (entries > policy.MaximumOutputDirectoryEntries)
                {
                    return $"entry count exceeds {policy.MaximumOutputDirectoryEntries}";
                }

                if (fileCount > policy.MaximumOutputFileCount)
                {
                    return $"file count exceeds {policy.MaximumOutputFileCount}";
                }

                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    return "reparse points are forbidden";
                }

                var length = new FileInfo(file).Length;
                if (length > policy.MaximumArtifactBytes)
                {
                    return $"one artifact exceeds {policy.MaximumArtifactBytes} bytes";
                }

                totalBytes = length > long.MaxValue - totalBytes
                    ? long.MaxValue
                    : totalBytes + length;
                if (totalBytes > policy.MaximumTotalArtifactBytes)
                {
                    return $"total artifact bytes exceed {policy.MaximumTotalArtifactBytes}";
                }
            }
        }

        return null;
    }

    private static async Task<CapturedStream> CaptureStreamAsync(
        Stream source,
        string destinationPath,
        int maximumBytes,
        CancellationTokenSource limitCancellation)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var captured = new MemoryStream(capacity: Math.Min(maximumBytes, BufferSize));
        var buffer = new byte[BufferSize];
        var remaining = maximumBytes;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            var acceptedBytes = Math.Min(bytesRead, remaining);
            if (acceptedBytes > 0)
            {
                await destination.WriteAsync(
                        buffer.AsMemory(0, acceptedBytes),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                captured.Write(buffer, 0, acceptedBytes);
                remaining -= acceptedBytes;
            }

            if (acceptedBytes != bytesRead)
            {
                limitCancellation.Cancel();
            }
        }

        await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return new CapturedStream(Encoding.UTF8.GetString(captured.ToArray()));
    }

    private static IEnumerable<string> EnumerateOutputFiles(string outputDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(outputDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("External program output cannot contain reparse points.");
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                pending.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("External program output cannot contain reparse points.");
                }

                yield return file;
            }
        }
    }

    private string CreateWorkspace()
    {
        Directory.CreateDirectory(_workspaceRootPath);
        RejectReparsePoints(_workspaceRootPath, _workspaceRootPath);
        var relativePath = Guid.NewGuid().ToString("N");
        var workspacePath = ResolveContainedPath(_workspaceRootPath, relativePath);
        Directory.CreateDirectory(workspacePath);
        RejectReparsePoints(_workspaceRootPath, workspacePath);
        return workspacePath;
    }

    private static string CreateInvocationProfileName(string profileNamespace)
    {
        var input = Encoding.UTF8.GetBytes(
            profileNamespace + "\0" + Guid.NewGuid().ToString("N"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        return "OpenLineOps.External." + digest[..40];
    }

    private static string? TryDeleteAppContainerProfile(string profileName)
    {
        try
        {
            WindowsAppContainerIdentity.DeleteProfile(profileName);
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return exception.Message;
        }
    }

    private async ValueTask VerifyFrozenResourceAsync(
        string resourceRootPath,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        string contentReaderSid,
        string stationServiceSid,
        CancellationToken cancellationToken)
    {
        if (_options.RequireImmutableContentProtection)
        {
            await _contentProtector.VerifyAsync(
                resourceRootPath,
                inventory,
                new ImmutableContentProtectionPolicy(contentReaderSid, stationServiceSid),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _contentProtector.VerifyInventoryAsync(
                resourceRootPath,
                inventory,
                cancellationToken)
            .ConfigureAwait(false);
        if (_options.RequireAppContainerIsolation)
        {
            WindowsContentAccessAuthorizer.GrantReadExecute(
                resourceRootPath,
                contentReaderSid);
        }
    }

    private static string RelativeEntryPoint(ExternalProgramExecutionRequest request)
    {
        var prefix = request.ResourceRootRelativePath + "/";
        if (!request.EntryPointRelativePath.StartsWith(prefix, PathComparison)
            || request.EntryPointRelativePath.Length == prefix.Length)
        {
            throw new InvalidDataException(
                "External program entry point must be contained by its frozen resource root.");
        }

        return request.EntryPointRelativePath[prefix.Length..];
    }

    private static string ResolveFrozenArgument(
        string resourceRootPath,
        string resourceRootRelativePath,
        IReadOnlyCollection<ExternalProgramExecutionFile> inventory,
        string argument)
    {
        if (string.IsNullOrEmpty(argument)
            || Path.IsPathRooted(argument)
            || argument.StartsWith('-')
            || argument.StartsWith('/')
            || argument.Contains('\\', StringComparison.Ordinal)
            || argument.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return argument;
        }

        var inventoryRelativeArgument = argument.StartsWith(
            resourceRootRelativePath + "/",
            PathComparison)
            ? argument[(resourceRootRelativePath.Length + 1)..]
            : argument;
        var match = inventory.Count(file => string.Equals(
            file.RelativePath,
            inventoryRelativeArgument,
            PathComparison));
        return match == 1
            ? ResolveContainedPath(resourceRootPath, inventoryRelativeArgument)
            : argument;
    }

    private static string ResolveFrozenPath(
        string applicationRootPath,
        string relativePath,
        bool requireFile)
    {
        if (!IsCanonical(applicationRootPath)
            || !Path.IsPathFullyQualified(applicationRootPath)
            || !IsCanonical(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Frozen Application path must be canonical and relative.");
        }

        var root = Path.GetFullPath(applicationRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Frozen Application directory '{root}' does not exist.");
        }

        var resolved = ResolveContainedPath(root, relativePath);
        if (requireFile && !File.Exists(resolved))
        {
            throw new FileNotFoundException("Frozen Application file does not exist.", resolved);
        }

        RejectReparsePoints(root, resolved);
        return resolved;
    }

    private static string ResolveContainedPath(string rootPath, string relativePath)
    {
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException("Relative path resolves outside its configured root.");
        }

        return resolved;
    }

    private static void RejectInvalidEvidencePath(
        string workspacePath,
        string sourcePath,
        string relativePath)
    {
        _ = ResolveContainedPath(workspacePath, Path.GetRelativePath(workspacePath, sourcePath));
        if (!IsCanonical(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."
                || !IsPortableArtifactPathSegment(segment)))
        {
            throw new InvalidDataException("External program artifact path is not canonical.");
        }
    }

    private static void RejectReparsePoints(string root, string path)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Frozen Application root cannot be a reparse point.");
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Frozen Application path cannot traverse a reparse point.");
            }
        }
    }

    private static string? ValidateRequest(ExternalProgramExecutionRequest request)
    {
        if (!IsCanonical(request.ResourceId)
            || request.ProductionRunId == Guid.Empty
            || request.RuntimeCommandId == Guid.Empty
            || !IsCanonical(request.ReleaseApplicationRootPath)
            || !IsCanonical(request.ResourceRootRelativePath)
            || Path.IsPathRooted(request.ResourceRootRelativePath)
            || request.ResourceRootRelativePath.Contains('\\')
            || request.ResourceRootRelativePath.Split('/').Any(segment => segment is "" or "." or "..")
            || !IsCanonical(request.EntryPointRelativePath)
            || !request.EntryPointRelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || request.EntryPointSizeBytes < 0
            || !IsLowercaseSha256(request.EntryPointSha256)
            || request.Files is null
            || request.Files.Count == 0
            || request.Arguments is null
            || request.Arguments.Any(argument => argument is null || argument.Contains('\0'))
            || request.InvocationPayload is null
            || request.Timeout <= TimeSpan.Zero
            || request.Policy is null)
        {
            return "External program execution request is invalid.";
        }

        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var file in request.Files)
        {
            if (file is null
                || !IsCanonical(file.RelativePath)
                || Path.IsPathRooted(file.RelativePath)
                || file.RelativePath.Contains('\\')
                || file.RelativePath.Split('/').Any(segment => segment is "" or "." or "..")
                || file.SizeBytes < 0
                || !IsLowercaseSha256(file.Sha256)
                || !paths.Add(file.RelativePath))
            {
                return "External program frozen file inventory is invalid.";
            }
        }

        return null;
    }

    private static ExternalProgramArtifactKind InferArtifactKind(string path)
    {
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".CSV" => ExternalProgramArtifactKind.Csv,
            ".JPG" or ".JPEG" or ".PNG" => ExternalProgramArtifactKind.Image,
            ".PDF" => ExternalProgramArtifactKind.Report,
            ".LOG" or ".TXT" => ExternalProgramArtifactKind.Log,
            _ => ExternalProgramArtifactKind.Binary
        };
    }

    private static string? InferMediaType(string path)
    {
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".CSV" => "text/csv",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".JSON" => "application/json",
            ".LOG" or ".TXT" => "text/plain",
            ".PDF" => "application/pdf",
            ".PNG" => "image/png",
            ".ZIP" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static string FormatStandardError(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return string.Empty;
        }

        var canonicalError = standardError.Trim();
        var captured = canonicalError.Length <= 2048
            ? canonicalError
            : canonicalError[..2048].TrimEnd();
        return $" Standard error: {captured}";
    }

    private string? TryDeleteWorkspace(string workspacePath)
    {
        try
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }

            var parent = Directory.GetParent(workspacePath);
            while (parent is not null
                   && !string.Equals(parent.FullName, _workspaceRootPath, PathComparison)
                   && parent.FullName.StartsWith(
                       _workspaceRootPath + Path.DirectorySeparatorChar,
                       PathComparison)
                   && !Directory.EnumerateFileSystemEntries(parent.FullName).Any())
            {
                var current = parent;
                parent = current.Parent;
                current.Delete();
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    private static bool IsLowercaseSha256(string value)
    {
        return value.Length == 64
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsCanonical(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !char.IsWhiteSpace(value[0])
               && !char.IsWhiteSpace(value[^1]);
    }

    private static bool IsPortableArtifactPathSegment(string value)
    {
        return IsCanonical(value)
               && value.Length <= 128
               && value[0] != '.'
               && value[^1] != '.'
               && value.All(character => char.IsAsciiLetterOrDigit(character)
                   || character is '-' or '_' or '.');
    }

    private sealed record CapturedStream(string Text);

    private sealed record CapturedStreams(
        CapturedStream StandardOutput,
        CapturedStream StandardError);

    private readonly record struct StoredEvidence(long SizeBytes, string Sha256);
}
