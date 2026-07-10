using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public static class ProjectReleaseExternalTestProgramCommandExecutor
{
    private const int MaximumCapturedOutputCharacters = 4 * 1024 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseExternalTestProgramCommandRoute route,
        Func<
            RuntimeCommandExecutionContext,
            ProjectReleaseRuntimeCommandRoute,
            CancellationToken,
            ValueTask<RuntimeCommandExecutionResult>> providerExecutor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(providerExecutor);

        if (cancellationToken.IsCancellationRequested)
        {
            return RuntimeCommandExecutionResult.Canceled(
                $"External test program '{route.AdapterId}' was canceled before launch.");
        }

        var invocation = CreateInvocation(context, route);
        if (invocation.Error is not null)
        {
            return RuntimeCommandExecutionResult.Rejected(invocation.Error);
        }

        RuntimeCommandExecutionResult executionResult;
        if (string.Equals(
                route.LaunchKind,
                ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
                StringComparison.Ordinal))
        {
            if (route.ProviderRoute is not null
                || route.Executable is null
                || route.ExecutableSizeBytes is null
                || route.ExecutableSha256 is null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"External test program '{route.AdapterId}' has an invalid executable route.");
            }

            executionResult = await ExecuteApplicationAsync(
                    route,
                    invocation.Payload!,
                    invocation.Arguments!,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(
                     route.LaunchKind,
                     ProjectReleaseExternalTestProgramLaunchKinds.Provider,
                     StringComparison.Ordinal))
        {
            if (route.ProviderRoute is null
                || route.Executable is not null
                || route.ExecutableSizeBytes is not null
                || route.ExecutableSha256 is not null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"External test program '{route.AdapterId}' has an invalid provider route.");
            }

            var providerContext = WithInputPayload(
                context,
                invocation.Payload!,
                route.TimeoutMilliseconds);
            executionResult = await providerExecutor(
                    providerContext,
                    route.ProviderRoute,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"External test program '{route.AdapterId}' has unsupported launch kind '{route.LaunchKind}'.");
        }

        if (executionResult.SemanticOutcome is not null)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' provider returned a semantic outcome before the frozen adapter mapping was applied.");
        }

        return executionResult.Outcome == RuntimeCommandExecutionOutcome.Completed
            ? MapCompletedResult(route, executionResult.Payload)
            : executionResult;
    }

    private static async ValueTask<RuntimeCommandExecutionResult> ExecuteApplicationAsync(
        ProjectReleaseExternalTestProgramCommandRoute route,
        string invocationPayload,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        string executablePath;
        try
        {
            executablePath = ResolveExecutablePath(
                route.ReleaseApplicationRootPath,
                route.Executable!);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"External test program '{route.AdapterId}' executable route is invalid: {exception.Message}");
        }

        if (route.TimeoutMilliseconds <= 0 || route.TimeoutMilliseconds > int.MaxValue)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"External test program '{route.AdapterId}' timeout is outside the supported range.");
        }

        try
        {
            var verificationError = await VerifyFrozenExecutableAsync(
                    executablePath,
                    route.ExecutableSizeBytes!.Value,
                    route.ExecutableSha256!,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verificationError is not null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"External test program '{route.AdapterId}' frozen executable is invalid: "
                    + verificationError);
            }
        }
        catch (OperationCanceledException)
        {
            return RuntimeCommandExecutionResult.Canceled(
                $"External test program '{route.AdapterId}' was canceled before launch.");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"External test program '{route.AdapterId}' frozen executable could not be verified: "
                + exception.Message);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetFullPath(route.ReleaseApplicationRootPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External test program '{route.AdapterId}' could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' could not be started: {exception.Message}");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(route.TimeoutMilliseconds));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            await process.StandardInput
                .WriteAsync(invocationPayload.AsMemory(), executionCancellation.Token)
                .ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(executionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            await WaitForTerminationAsync(process).ConfigureAwait(false);
            var canceledOutput = await standardOutputTask.ConfigureAwait(false);
            var canceledError = await standardErrorTask.ConfigureAwait(false);
            _ = canceledOutput;

            if (cancellationToken.IsCancellationRequested)
            {
                return RuntimeCommandExecutionResult.Canceled(
                    $"External test program '{route.AdapterId}' was canceled."
                    + FormatStandardError(canceledError));
            }

            return RuntimeCommandExecutionResult.TimedOut(
                $"External test program '{route.AdapterId}' exceeded its frozen timeout of "
                + $"{route.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)} ms."
                + FormatStandardError(canceledError));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TryTerminate(process);
            await WaitForTerminationAsync(process).ConfigureAwait(false);
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' execution failed: {exception.Message}");
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (standardOutput.Length > MaximumCapturedOutputCharacters
            || standardError.Length > MaximumCapturedOutputCharacters)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' exceeded the captured output limit.");
        }

        if (process.ExitCode != 0)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' exited with code "
                + $"{process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                + FormatStandardError(standardError));
        }

        return RuntimeCommandExecutionResult.Completed(standardOutput);
    }

    private static ExternalTestInvocation CreateInvocation(
        RuntimeCommandExecutionContext context,
        ProjectReleaseExternalTestProgramCommandRoute route)
    {
        if (!IsCanonical(route.AdapterId)
            || !IsCanonical(route.LaunchKind)
            || !IsCanonical(route.ProviderKind)
            || !IsCanonical(route.ProviderKey)
            || !IsCanonical(route.DutModelId)
            || !IsCanonical(route.DutModelCode)
            || !IsCanonical(route.DutIdentityInputKey)
            || !string.Equals(
                context.DutIdentity.ModelId,
                route.DutModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                context.DutIdentity.InputKey,
                route.DutIdentityInputKey,
                StringComparison.Ordinal)
            || route.ArgumentTemplates is null
            || route.InputMappings is null
            || route.ResultMappings is null
            || route.OutcomeMapping is null
            || route.ArgumentTemplates.Any(static argument => argument is null)
            || route.InputMappings.Any(static mapping => mapping is null)
            || route.ResultMappings.Any(static mapping => mapping is null)
            || route.TimeoutMilliseconds <= 0
            || context.Timeout.Ticks % TimeSpan.TicksPerMillisecond != 0
            || context.Timeout.Ticks / TimeSpan.TicksPerMillisecond != route.TimeoutMilliseconds)
        {
            return ExternalTestInvocation.Failure(
                $"External test program '{route.AdapterId}' route is invalid or does not match the Runtime command.");
        }

        if (!IsValidOutcomeMapping(route.OutcomeMapping))
        {
            return ExternalTestInvocation.Failure(
                $"External test program '{route.AdapterId}' outcome mapping is invalid.");
        }

        var tokenValues = CreateTokenValues(context, route);
        var mappedInputs = new Dictionary<string, TokenValue>(StringComparer.Ordinal);
        foreach (var mapping in route.InputMappings)
        {
            if (!IsCanonical(mapping.Source)
                || !ProjectReleaseExternalTestProgramContract.IsSupportedInputSource(mapping.Source)
                || !IsCanonical(mapping.Target)
                || !tokenValues.TryGetValue(mapping.Source[1..], out var value)
                || !mappedInputs.TryAdd(mapping.Target, value))
            {
                return ExternalTestInvocation.Failure(
                    $"External test program '{route.AdapterId}' contains an unsupported or duplicated input mapping.");
            }
        }

        if (!route.InputMappings.Any(mapping => string.Equals(
                mapping.Source,
                "$dut.identity",
                StringComparison.Ordinal))
            || !route.InputMappings.Any(mapping => string.Equals(
                mapping.Source,
                "$dut.model",
                StringComparison.Ordinal)))
        {
            return ExternalTestInvocation.Failure(
                $"External test program '{route.AdapterId}' must map DUT identity and model.");
        }

        foreach (var input in mappedInputs)
        {
            tokenValues.Add($"input.{input.Key}", input.Value);
        }

        var arguments = new List<string>(route.ArgumentTemplates.Count);
        foreach (var template in route.ArgumentTemplates)
        {
            var rendered = RenderArgument(template, tokenValues);
            if (rendered.Error is not null)
            {
                return ExternalTestInvocation.Failure(
                    $"External test program '{route.AdapterId}' argument template is invalid: {rendered.Error}");
            }

            arguments.Add(rendered.Value!);
        }

        JsonElement commandInput;
        try
        {
            using var commandInputDocument = JsonDocument.Parse(context.InputPayload ?? "null");
            commandInput = commandInputDocument.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return ExternalTestInvocation.Failure(
                $"External test program '{route.AdapterId}' command input is invalid JSON: {exception.Message}");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                ProjectReleaseExternalTestProgramContract.InvocationSchema);
            writer.WriteString("productionRunId", context.ProductionRunId.ToString());
            writer.WriteString("productionLineDefinitionId", context.ProductionLineDefinitionId);
            writer.WriteString("productionStageId", context.ProductionStageId);
            writer.WriteNumber("stageSequence", context.StageSequence);
            writer.WriteString("workstationId", context.WorkstationId);
            writer.WriteString("runtimeSessionId", context.SessionId.ToString());
            writer.WriteString("projectId", context.ProjectId);
            writer.WriteString("applicationId", context.ApplicationId);
            writer.WriteString("projectSnapshotId", context.ProjectSnapshotId);
            writer.WriteString("stationId", context.StationId.Value);
            writer.WriteString("configurationSnapshotId", context.ConfigurationSnapshotId.Value);
            writer.WriteString("runtimeStepId", context.StepId.ToString());
            writer.WriteString("runtimeCommandId", context.CommandId.ToString());
            writer.WriteString("runtimeNodeId", context.NodeId.Value);
            writer.WriteString("actionId", context.ActionId.Value);
            writer.WriteString("capabilityId", context.TargetCapability.Value);
            writer.WriteString("commandName", context.CommandName);
            writer.WriteStartObject("target");
            writer.WriteString("kind", context.TargetKind);
            writer.WriteString("id", context.TargetId);
            writer.WriteEndObject();
            writer.WriteStartObject("dut");
            writer.WriteString("modelId", context.DutIdentity.ModelId);
            writer.WriteString("modelCode", route.DutModelCode);
            writer.WriteString("identityInputKey", context.DutIdentity.InputKey);
            writer.WriteString("identityValue", context.DutIdentity.Value);
            writer.WriteEndObject();
            writer.WriteStartObject("inputs");
            foreach (var input in mappedInputs.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                input.Value.Write(writer, input.Key);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("arguments");
            foreach (var argument in arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("commandInput");
            commandInput.WriteTo(writer);
            writer.WriteEndObject();
        }

        return ExternalTestInvocation.Success(Encoding.UTF8.GetString(stream.ToArray()), arguments);
    }

    private static Dictionary<string, TokenValue> CreateTokenValues(
        RuntimeCommandExecutionContext context,
        ProjectReleaseExternalTestProgramCommandRoute route)
    {
        return new Dictionary<string, TokenValue>(StringComparer.Ordinal)
        {
            ["dut.identity"] = TokenValue.Text(context.DutIdentity.Value),
            ["dut.model"] = TokenValue.Text(route.DutModelCode),
            ["dut.inputKey"] = TokenValue.Text(context.DutIdentity.InputKey),
            ["run.id"] = TokenValue.Text(context.ProductionRunId.ToString()),
            ["line.id"] = TokenValue.Text(context.ProductionLineDefinitionId),
            ["stage.id"] = TokenValue.Text(context.ProductionStageId),
            ["stage.sequence"] = TokenValue.Number(context.StageSequence),
            ["workstation.id"] = TokenValue.Text(context.WorkstationId),
            ["session.id"] = TokenValue.Text(context.SessionId.ToString()),
            ["station.id"] = TokenValue.Text(context.StationId.Value),
            ["configuration.id"] = TokenValue.Text(context.ConfigurationSnapshotId.Value),
            ["step.id"] = TokenValue.Text(context.StepId.ToString()),
            ["command.id"] = TokenValue.Text(context.CommandId.ToString()),
            ["command.name"] = TokenValue.Text(context.CommandName),
            ["node.id"] = TokenValue.Text(context.NodeId.Value),
            ["action.id"] = TokenValue.Text(context.ActionId.Value),
            ["capability.id"] = TokenValue.Text(context.TargetCapability.Value),
            ["project.id"] = TokenValue.Text(context.ProjectId),
            ["application.id"] = TokenValue.Text(context.ApplicationId),
            ["snapshot.id"] = TokenValue.Text(context.ProjectSnapshotId),
            ["target.kind"] = TokenValue.Text(context.TargetKind),
            ["target.id"] = TokenValue.Text(context.TargetId)
        };
    }

    private static RenderedArgument RenderArgument(
        string template,
        IReadOnlyDictionary<string, TokenValue> values)
    {
        if (!IsCanonical(template))
        {
            return RenderedArgument.Failure("template must be non-empty canonical text");
        }

        var builder = new StringBuilder(template.Length);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var opening = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (opening < 0)
            {
                builder.Append(template, cursor, template.Length - cursor);
                break;
            }

            builder.Append(template, cursor, opening - cursor);
            var closing = template.IndexOf("}}", opening + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                return RenderedArgument.Failure("template contains an unmatched opening delimiter");
            }

            var token = template[(opening + 2)..closing];
            if (!IsCanonical(token) || !values.TryGetValue(token, out var value))
            {
                return RenderedArgument.Failure($"placeholder '{{{{{token}}}}}' is not supported");
            }

            builder.Append(value.ArgumentValue);
            cursor = closing + 2;
        }

        return RenderedArgument.Success(builder.ToString());
    }

    private static RuntimeCommandExecutionResult MapCompletedResult(
        ProjectReleaseExternalTestProgramCommandRoute route,
        string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' returned no JSON result object.");
        }

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || HasDuplicateObjectProperties(document.RootElement))
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External test program '{route.AdapterId}' must return one JSON object without duplicate properties.");
            }

            if (!TryResolveResultPath(
                    document.RootElement,
                    route.OutcomeMapping.SourcePath,
                    out var outcomeValue)
                || outcomeValue.ValueKind != JsonValueKind.String)
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External test program '{route.AdapterId}' outcome path "
                    + $"'{route.OutcomeMapping.SourcePath}' must resolve exactly to one JSON string.");
            }

            var outcomeToken = outcomeValue.GetString()!;

            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            var mappedValues = new List<(string TargetKey, JsonElement Value)>();
            foreach (var mapping in route.ResultMappings)
            {
                if (!IsCanonical(mapping.TargetKey)
                    || !targetKeys.Add(mapping.TargetKey)
                    || !TryResolveResultPath(
                        document.RootElement,
                        mapping.SourcePath,
                        out var value))
                {
                    return RuntimeCommandExecutionResult.Failed(
                        $"External test program '{route.AdapterId}' result mapping "
                        + $"'{mapping.SourcePath}' -> '{mapping.TargetKey}' could not be resolved exactly.");
                }

                mappedValues.Add((mapping.TargetKey, value));
            }

            if (mappedValues.Count == 0)
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External test program '{route.AdapterId}' has no result mappings.");
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var mappedValue in mappedValues.OrderBy(
                             item => item.TargetKey,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(mappedValue.TargetKey);
                    mappedValue.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            var mappedPayload = Encoding.UTF8.GetString(stream.ToArray());
            if (string.Equals(
                    outcomeToken,
                    route.OutcomeMapping.PassedToken,
                    StringComparison.Ordinal))
            {
                return RuntimeCommandExecutionResult.SemanticPassed(mappedPayload);
            }

            if (string.Equals(
                    outcomeToken,
                    route.OutcomeMapping.FailedToken,
                    StringComparison.Ordinal))
            {
                return RuntimeCommandExecutionResult.SemanticFailed(
                    $"External test program '{route.AdapterId}' reported a failed judgement.",
                    mappedPayload);
            }

            if (string.Equals(
                    outcomeToken,
                    route.OutcomeMapping.AbortedToken,
                    StringComparison.Ordinal))
            {
                return RuntimeCommandExecutionResult.SemanticAborted(
                    $"External test program '{route.AdapterId}' reported an aborted judgement.",
                    mappedPayload);
            }

            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' returned unknown exact outcome token '{outcomeToken}'.");
        }
        catch (JsonException exception)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External test program '{route.AdapterId}' returned invalid JSON: {exception.Message}");
        }
    }

    private static bool TryResolveResultPath(
        JsonElement root,
        string sourcePath,
        out JsonElement value)
    {
        value = root;
        if (!ProjectReleaseExternalTestProgramContract.IsSupportedResultPath(sourcePath))
        {
            return false;
        }

        var segments = sourcePath[2..].Split('.');
        if (segments.Length == 0 || segments.Any(segment => !IsCanonical(segment)))
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var matches = value.EnumerateObject()
                .Where(property => string.Equals(property.Name, segment, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            value = matches[0].Value;
        }

        return true;
    }

    private static bool HasDuplicateObjectProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateObjectProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateObjectProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static RuntimeCommandExecutionContext WithInputPayload(
        RuntimeCommandExecutionContext context,
        string inputPayload,
        long timeoutMilliseconds)
    {
        return new RuntimeCommandExecutionContext(
            context.SessionId,
            context.ProductionRunId,
            context.ProductionLineDefinitionId,
            context.ProductionStageId,
            context.StageSequence,
            context.WorkstationId,
            context.DutIdentity,
            context.StationId,
            context.ConfigurationSnapshotId,
            context.StepId,
            context.CommandId,
            context.NodeId,
            context.TargetCapability,
            context.CommandName,
            inputPayload,
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            context.ActionId,
            context.TargetKind,
            context.TargetId,
            context.ProjectId,
            context.ApplicationId,
            context.ProjectSnapshotId);
    }

    private static string ResolveExecutablePath(string applicationRootPath, string executable)
    {
        if (!IsCanonical(applicationRootPath)
            || !Path.IsPathFullyQualified(applicationRootPath)
            || !IsCanonical(executable)
            || Path.IsPathRooted(executable)
            || executable.Contains('\\')
            || executable.Split('/').Length < 2
            || !string.Equals(executable.Split('/')[0], "programs", StringComparison.Ordinal)
            || executable.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                "External test executable must be a canonical programs/ Application-relative path.");
        }

        var root = Path.GetFullPath(applicationRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Frozen Application directory '{root}' does not exist.");
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        var executablePath = Path.GetFullPath(Path.Combine(
            root,
            executable.Replace('/', Path.DirectorySeparatorChar)));
        if (!executablePath.StartsWith(rootPrefix, PathComparison)
            || !File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "Frozen external test executable does not exist inside the release Application.",
                executablePath);
        }

        RejectReparsePoints(root, executablePath);
        return executablePath;
    }

    private static async ValueTask<string?> VerifyFrozenExecutableAsync(
        string executablePath,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedSizeBytes < 0 || !IsLowercaseSha256(expectedSha256))
        {
            return "release file identity is not canonical";
        }

        var fileInfo = new FileInfo(executablePath);
        if (fileInfo.Length != expectedSizeBytes)
        {
            return $"size is {fileInfo.Length.ToString(CultureInfo.InvariantCulture)} bytes, expected "
                   + $"{expectedSizeBytes.ToString(CultureInfo.InvariantCulture)} bytes";
        }

        await using var stream = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)
            ? null
            : "SHA-256 does not match the immutable release manifest";
    }

    private static bool IsLowercaseSha256(string value)
    {
        return value.Length == 64
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void RejectReparsePoints(string root, string filePath)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Frozen Application root cannot be a reparse point.");
        }

        var currentPath = root;
        foreach (var segment in Path.GetRelativePath(root, filePath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Frozen external test executable cannot traverse a reparse point.");
            }
        }
    }

    private static bool IsCanonical(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !char.IsWhiteSpace(value[0])
               && !char.IsWhiteSpace(value[^1]);
    }

    private static bool IsValidOutcomeMapping(ExternalTestProgramRouteOutcomeMapping mapping)
    {
        return ProjectReleaseExternalTestProgramContract.IsSupportedResultPath(mapping.SourcePath)
            && IsCanonical(mapping.PassedToken)
            && IsCanonical(mapping.FailedToken)
            && IsCanonical(mapping.AbortedToken)
            && !string.Equals(mapping.PassedToken, mapping.FailedToken, StringComparison.Ordinal)
            && !string.Equals(mapping.PassedToken, mapping.AbortedToken, StringComparison.Ordinal)
            && !string.Equals(mapping.FailedToken, mapping.AbortedToken, StringComparison.Ordinal);
    }

    private static string FormatStandardError(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return string.Empty;
        }

        var captured = standardError.Length <= 2048
            ? standardError
            : standardError[..2048];
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
        catch (Exception exception) when (exception is InvalidOperationException
                                          or Win32Exception)
        {
            _ = exception;
        }
    }

    private sealed record ExternalTestInvocation(
        string? Payload,
        IReadOnlyCollection<string>? Arguments,
        string? Error)
    {
        public static ExternalTestInvocation Success(
            string payload,
            IReadOnlyCollection<string> arguments) => new(payload, arguments, null);

        public static ExternalTestInvocation Failure(string error) => new(null, null, error);
    }

    private readonly record struct RenderedArgument(string? Value, string? Error)
    {
        public static RenderedArgument Success(string value) => new(value, null);

        public static RenderedArgument Failure(string error) => new(null, error);
    }

    private readonly record struct TokenValue(string ArgumentValue, string? TextValue, int? NumberValue)
    {
        public static TokenValue Text(string value) => new(value, value, null);

        public static TokenValue Number(int value) => new(
            value.ToString(CultureInfo.InvariantCulture),
            null,
            value);

        public void Write(Utf8JsonWriter writer, string propertyName)
        {
            if (NumberValue is not null)
            {
                writer.WriteNumber(propertyName, NumberValue.Value);
                return;
            }

            writer.WriteString(propertyName, TextValue);
        }
    }
}
