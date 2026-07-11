using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;

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

    public ExternalProgramHost(ExternalProgramHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _workspaceRootPath = _options.ResolveWorkspaceRootPath();
        _evidenceRootPath = _options.ResolveEvidenceRootPath();
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

        string executablePath;
        try
        {
            executablePath = ResolveFrozenPath(
                request.ReleaseApplicationRootPath,
                request.ExecutableRelativePath,
                requireFile: true);
            var verificationError = await VerifyExecutableAsync(
                    executablePath,
                    request.ExecutableSizeBytes,
                    request.ExecutableSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verificationError is not null)
            {
                return ExternalProgramExecutionResult.Rejected(
                    $"External program '{request.AdapterId}' frozen executable is invalid: {verificationError}");
            }
        }
        catch (OperationCanceledException)
        {
            return ExternalProgramExecutionResult.Canceled(
                $"External program '{request.AdapterId}' was canceled before launch.",
                []);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return ExternalProgramExecutionResult.Rejected(
                $"External program '{request.AdapterId}' frozen executable is invalid: {exception.Message}");
        }

        var workspacePath = CreateWorkspace(request);
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

            var startInfo = CreateStartInfo(
                request,
                executablePath,
                workspacePath,
                outputDirectory,
                temporaryDirectory,
                invocationPath);
            result = await ExecuteProcessAsync(
                    request,
                    startInfo,
                    workspacePath,
                    outputDirectory,
                    standardOutputPath,
                    standardErrorPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var postExecutionVerificationError = await VerifyExecutableAsync(
                    executablePath,
                    request.ExecutableSizeBytes,
                    request.ExecutableSha256,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (postExecutionVerificationError is not null)
            {
                result = ExternalProgramExecutionResult.Failed(
                    $"External program '{request.AdapterId}' modified its frozen executable: "
                    + postExecutionVerificationError,
                    result.Artifacts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = ExternalProgramExecutionResult.Canceled(
                $"External program '{request.AdapterId}' was canceled.",
                []);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or NotSupportedException
                                          or Win32Exception)
        {
            result = ExternalProgramExecutionResult.Failed(
                $"External program '{request.AdapterId}' host failed: {exception.Message}",
                []);
        }

        var cleanupError = TryDeleteWorkspace(workspacePath);
        return cleanupError is null
            ? result
            : ExternalProgramExecutionResult.Failed(
                $"External program '{request.AdapterId}' workspace cleanup failed: {cleanupError}",
                result.Artifacts);
    }

    private async ValueTask<ExternalProgramExecutionResult> ExecuteProcessAsync(
        ExternalProgramExecutionRequest request,
        ProcessStartInfo startInfo,
        string workspacePath,
        string outputDirectory,
        string standardOutputPath,
        string standardErrorPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        WindowsProcessJob? job = null;
        try
        {
            if (!process.Start())
            {
                return ExternalProgramExecutionResult.Failed(
                    $"External program '{request.AdapterId}' could not be started.",
                    []);
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    job = WindowsProcessJob.Create(_options);
                    job.Assign(process);
                }
                catch when (!_options.RequireWindowsJobObject)
                {
                    job?.Dispose();
                    job = null;
                }
                catch
                {
                    TryTerminate(process);
                    throw;
                }
            }

            using var timeoutCancellation = new CancellationTokenSource(request.Timeout);
            using var outputLimitCancellation = new CancellationTokenSource();
            using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token,
                outputLimitCancellation.Token);

            var standardOutputTask = CaptureStreamAsync(
                process.StandardOutput.BaseStream,
                standardOutputPath,
                _options.MaximumStandardOutputBytes,
                outputLimitCancellation);
            var standardErrorTask = CaptureStreamAsync(
                process.StandardError.BaseStream,
                standardErrorPath,
                _options.MaximumStandardErrorBytes,
                outputLimitCancellation);

            try
            {
                await process.StandardInput
                    .WriteAsync(request.InvocationPayload.AsMemory(), executionCancellation.Token)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(executionCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                await WaitForTerminationAsync(process).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                TryTerminate(process);
                await WaitForTerminationAsync(process).ConfigureAwait(false);
                var failedCaptures = await AwaitCapturesAsync(
                        standardOutputTask,
                        standardErrorTask)
                    .ConfigureAwait(false);
                var failedArtifacts = await PersistEvidenceAsync(
                        request,
                        workspacePath,
                        outputDirectory,
                        standardOutputPath,
                        standardErrorPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return ExternalProgramExecutionResult.Failed(
                    $"External program '{request.AdapterId}' execution failed: {exception.Message}"
                    + FormatStandardError(failedCaptures.StandardError.Text),
                    failedArtifacts);
            }

            job?.Dispose();
            job = null;

            var captures = await AwaitCapturesAsync(standardOutputTask, standardErrorTask)
                .ConfigureAwait(false);
            var artifacts = await PersistEvidenceAsync(
                    request,
                    workspacePath,
                    outputDirectory,
                    standardOutputPath,
                    standardErrorPath,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (outputLimitCancellation.IsCancellationRequested)
            {
                return ExternalProgramExecutionResult.Failed(
                    $"External program '{request.AdapterId}' exceeded the streaming output limit.",
                    artifacts);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ExternalProgramExecutionResult.Canceled(
                    $"External program '{request.AdapterId}' was canceled."
                    + FormatStandardError(captures.StandardError.Text),
                    artifacts);
            }

            if (timeoutCancellation.IsCancellationRequested)
            {
                return ExternalProgramExecutionResult.TimedOut(
                    $"External program '{request.AdapterId}' exceeded its timeout of "
                    + $"{request.Timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms."
                    + FormatStandardError(captures.StandardError.Text),
                    artifacts);
            }

            if (process.ExitCode != 0)
            {
                return ExternalProgramExecutionResult.Failed(
                    $"External program '{request.AdapterId}' exited with code "
                    + $"{process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                    + FormatStandardError(captures.StandardError.Text),
                    artifacts);
            }

            return ExternalProgramExecutionResult.Completed(
                captures.StandardOutput.Text,
                artifacts);
        }
        finally
        {
            job?.Dispose();
        }
    }

    private ProcessStartInfo CreateStartInfo(
        ExternalProgramExecutionRequest request,
        string executablePath,
        string workspacePath,
        string outputDirectory,
        string temporaryDirectory,
        string invocationPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(ResolveFrozenArgument(
                request.ReleaseApplicationRootPath,
                argument));
        }

        var inherited = _options.AllowedInheritedEnvironmentVariables
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => item.Value is not null)
            .ToArray();
        startInfo.Environment.Clear();
        foreach (var item in inherited)
        {
            startInfo.Environment[item.Name] = item.Value!;
        }

        startInfo.Environment["TEMP"] = temporaryDirectory;
        startInfo.Environment["TMP"] = temporaryDirectory;
        startInfo.Environment["OPENLINEOPS_WORKSPACE"] = workspacePath;
        startInfo.Environment["OPENLINEOPS_OUTPUT_DIRECTORY"] = outputDirectory;
        startInfo.Environment["OPENLINEOPS_INVOCATION_FILE"] = invocationPath;
        startInfo.Environment["OPENLINEOPS_PRODUCTION_RUN_ID"] = request.ProductionRunId.ToString("D");
        startInfo.Environment["OPENLINEOPS_RUNTIME_COMMAND_ID"] = request.RuntimeCommandId.ToString("D");
        return startInfo;
    }

    private async ValueTask<IReadOnlyCollection<ExternalProgramArtifact>> PersistEvidenceAsync(
        ExternalProgramExecutionRequest request,
        string workspacePath,
        string outputDirectory,
        string standardOutputPath,
        string standardErrorPath,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string SourcePath, string RelativePath)>
        {
            (standardOutputPath, "stdout.log"),
            (standardErrorPath, "stderr.log")
        };
        foreach (var path in EnumerateOutputFiles(outputDirectory))
        {
            if (candidates.Count == _options.MaximumArtifactCount)
            {
                throw new InvalidDataException(
                    $"External program produced more than {_options.MaximumArtifactCount} artifacts.");
            }

            candidates.Add((
                path,
                "output/" + Path.GetRelativePath(outputDirectory, path).Replace('\\', '/')));
        }

        if (candidates.Count > _options.MaximumArtifactCount)
        {
            throw new InvalidDataException(
                $"External program produced {candidates.Count} artifacts; limit is {_options.MaximumArtifactCount}.");
        }

        long totalSize = 0;
        var artifacts = new List<ExternalProgramArtifact>(candidates.Count);
        foreach (var candidate in candidates.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            RejectInvalidEvidencePath(workspacePath, candidate.SourcePath, candidate.RelativePath);
            var file = new FileInfo(candidate.SourcePath);
            if (file.Length > _options.MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"External program artifact '{candidate.RelativePath}' exceeds the per-artifact limit.");
            }

            totalSize = checked(totalSize + file.Length);
            if (totalSize > _options.MaximumTotalArtifactBytes)
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

    private static async Task<CapturedStreams> AwaitCapturesAsync(
        Task<CapturedStream> standardOutputTask,
        Task<CapturedStream> standardErrorTask)
    {
        await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        return new CapturedStreams(
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false));
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

    private string CreateWorkspace(ExternalProgramExecutionRequest request)
    {
        Directory.CreateDirectory(_workspaceRootPath);
        var relativePath = request.ProductionRunId.ToString("N")
                           + "/"
                           + request.RuntimeCommandId.ToString("N")
                           + "/"
                           + Guid.NewGuid().ToString("N");
        var workspacePath = ResolveContainedPath(_workspaceRootPath, relativePath);
        Directory.CreateDirectory(workspacePath);
        return workspacePath;
    }

    private static string ResolveFrozenArgument(string applicationRootPath, string argument)
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

        try
        {
            var resolved = ResolveFrozenPath(applicationRootPath, argument, requireFile: true);
            return File.Exists(resolved) ? resolved : argument;
        }
        catch (FileNotFoundException)
        {
            return argument;
        }
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

    private static async ValueTask<string?> VerifyExecutableAsync(
        string executablePath,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(executablePath);
        if (file.Length != expectedSizeBytes)
        {
            return $"size is {file.Length.ToString(CultureInfo.InvariantCulture)} bytes, expected "
                   + $"{expectedSizeBytes.ToString(CultureInfo.InvariantCulture)} bytes";
        }

        await using var stream = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return string.Equals(hash, expectedSha256, StringComparison.Ordinal)
            ? null
            : "SHA-256 does not match the immutable release manifest";
    }

    private static string? ValidateRequest(ExternalProgramExecutionRequest request)
    {
        if (!IsCanonical(request.AdapterId)
            || request.ProductionRunId == Guid.Empty
            || request.RuntimeCommandId == Guid.Empty
            || !IsCanonical(request.ReleaseApplicationRootPath)
            || !IsCanonical(request.ExecutableRelativePath)
            || request.ExecutableSizeBytes < 0
            || !IsLowercaseSha256(request.ExecutableSha256)
            || request.Arguments is null
            || request.Arguments.Any(argument => argument is null)
            || request.InvocationPayload is null
            || request.Timeout <= TimeSpan.Zero)
        {
            return "External program execution request is invalid.";
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

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or Win32Exception
                                          or NotSupportedException)
        {
            _ = exception;
        }
    }

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            _ = exception;
        }
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
