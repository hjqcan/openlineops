using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public static class ProjectReleaseExternalProgramCommandExecutor
{
    public static async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeCommandExecutionContext context,
        ProjectReleaseExternalProgramCommandRoute route,
        Func<
            RuntimeCommandExecutionContext,
            ProjectReleaseRuntimeCommandRoute,
            CancellationToken,
            ValueTask<RuntimeCommandExecutionResult>> providerExecutor,
        Func<
            RuntimeCommandExecutionContext,
            CancellationToken,
            ValueTask<RuntimeCommandExecutionResult?>> resourceFenceGuard,
        IExternalProgramHost externalProgramHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(providerExecutor);
        ArgumentNullException.ThrowIfNull(resourceFenceGuard);
        ArgumentNullException.ThrowIfNull(externalProgramHost);

        if (cancellationToken.IsCancellationRequested)
        {
            return RuntimeCommandExecutionResult.Canceled(
                $"External program '{route.ResourceId}' was canceled before launch.");
        }

        var invocation = CreateInvocation(context, route);
        if (invocation.Error is not null)
        {
            return RuntimeCommandExecutionResult.Rejected(invocation.Error);
        }

        RuntimeCommandExecutionResult executionResult;
        IReadOnlyCollection<ExternalProgramArtifact>? externalArtifacts = null;
        if (string.Equals(
                route.LaunchKind,
                ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
                StringComparison.Ordinal))
        {
            if (route.ProviderRoute is not null
                || route.EntryPoint is null
                || route.EntryPointSizeBytes is null
                || route.EntryPointSha256 is null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"External program '{route.ResourceId}' has an invalid entry point route.");
            }

            var fenceRejection = await resourceFenceGuard(context, cancellationToken)
                .ConfigureAwait(false);
            if (fenceRejection is not null)
            {
                return fenceRejection;
            }

            var hostResult = await externalProgramHost.ExecuteAsync(
                    new ExternalProgramExecutionRequest(
                        route.ResourceId,
                        context.ProductionRunId.Value,
                        context.CommandId.Value,
                        route.ReleaseApplicationRootPath,
                        route.ResourceRelativePath,
                        $"{route.ResourceRelativePath}/{route.EntryPoint}",
                        route.EntryPointSizeBytes.Value,
                        route.EntryPointSha256,
                        route.Files.Select(file => new ExternalProgramExecutionFile(
                            file.RelativePath,
                            file.SizeBytes,
                            file.Sha256)).ToArray(),
                        invocation.Arguments!,
                        invocation.Payload!,
                        TimeSpan.FromMilliseconds(route.ExecutionLimits.TimeoutMilliseconds),
                        new ExternalProgramExecutionPolicy(
                            route.PermissionProfile.ProfileName,
                            route.PermissionProfile.NetworkAccessAllowed,
                            route.PermissionProfile.AllowedEnvironmentVariables,
                            route.ExecutionLimits.MaximumProcessCount,
                            route.ExecutionLimits.MaximumWorkingSetBytes,
                            route.ExecutionLimits.MaximumCpuTimeMilliseconds,
                            route.ExecutionLimits.MaximumStandardOutputBytes,
                            route.ExecutionLimits.MaximumStandardErrorBytes,
                            route.ExecutionLimits.MaximumArtifactCount,
                            route.ExecutionLimits.MaximumArtifactBytes,
                            route.ExecutionLimits.MaximumTotalArtifactBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            externalArtifacts = hostResult.Artifacts;
            executionResult = ToRuntimeResult(hostResult);
        }
        else if (string.Equals(
                     route.LaunchKind,
                     ProjectReleaseExternalProgramLaunchKinds.Provider,
                     StringComparison.Ordinal))
        {
            if (route.ProviderRoute is null
                || route.EntryPoint is not null
                || route.EntryPointSizeBytes is not null
                || route.EntryPointSha256 is not null)
            {
                return RuntimeCommandExecutionResult.Rejected(
                    $"External program '{route.ResourceId}' has an invalid provider route.");
            }

            var providerContext = WithInputPayload(
                context,
                invocation.Payload!,
                route.ExecutionLimits.TimeoutMilliseconds);
            var fenceRejection = await resourceFenceGuard(providerContext, cancellationToken)
                .ConfigureAwait(false);
            if (fenceRejection is not null)
            {
                return fenceRejection;
            }

            executionResult = await providerExecutor(
                    providerContext,
                    route.ProviderRoute,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"External program '{route.ResourceId}' has unsupported launch kind '{route.LaunchKind}'.");
        }

        var resultAxisIsValid = executionResult.Outcome == RuntimeCommandExecutionOutcome.Completed
            ? executionResult.ResultJudgement == ResultJudgement.NotApplicable
            : executionResult.ResultJudgement == ResultJudgement.Unknown;
        if (!resultAxisIsValid)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External program '{route.ResourceId}' provider returned a result judgement before the frozen resource mapping was applied.");
        }

        if (executionResult.Outcome == RuntimeCommandExecutionOutcome.Completed)
        {
            executionResult = MapCompletedProtocolResult(route, executionResult.Payload);
        }

        return externalArtifacts is null
            ? executionResult
            : AttachEvidence(executionResult, externalArtifacts);
    }

    private static ExternalProgramInvocation CreateInvocation(
        RuntimeCommandExecutionContext context,
        ProjectReleaseExternalProgramCommandRoute route)
    {
        if (!IsCanonical(route.ResourceId)
            || !IsCanonical(route.LaunchKind)
            || !IsCanonical(route.ProviderKind)
            || !IsCanonical(route.ProviderKey)
            || !IsCanonical(route.ProductModelId)
            || !IsCanonical(route.ProductModelCode)
            || !IsCanonical(route.ProductionUnitIdentityInputKey)
            || !string.Equals(
                context.ProductionUnitIdentity.ModelId,
                route.ProductModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                context.ProductionUnitIdentity.InputKey,
                route.ProductionUnitIdentityInputKey,
                StringComparison.Ordinal)
            || route.ArgumentTemplates is null
            || route.InputMappings is null
            || route.ResultMappings is null
            || route.OutcomeMapping is null
            || route.ArgumentTemplates.Any(static argument => argument is null)
            || route.InputMappings.Any(static mapping => mapping is null)
            || route.ResultMappings.Any(static mapping => mapping is null)
            || route.ResultMappings.Any(static mapping => !Enum.IsDefined(mapping.ValueKind))
            || route.ExecutionLimits.TimeoutMilliseconds <= 0
            || context.Timeout.Ticks % TimeSpan.TicksPerMillisecond != 0
            || context.Timeout.Ticks / TimeSpan.TicksPerMillisecond != route.ExecutionLimits.TimeoutMilliseconds)
        {
            return ExternalProgramInvocation.Failure(
                $"External program '{route.ResourceId}' route is invalid or does not match the Runtime command.");
        }

        if (!IsValidOutcomeMapping(route.OutcomeMapping))
        {
            return ExternalProgramInvocation.Failure(
                $"External program '{route.ResourceId}' outcome mapping is invalid.");
        }

        var tokenValues = CreateTokenValues(context, route);
        var mappedInputs = new Dictionary<string, TokenValue>(StringComparer.Ordinal);
        foreach (var mapping in route.InputMappings)
        {
            if (!IsCanonical(mapping.Source)
                || !ExternalProgramResourceContract.IsSupportedInputSource(mapping.Source)
                || !IsCanonical(mapping.Target)
                || !tokenValues.TryGetValue(mapping.Source[1..], out var value)
                || !mappedInputs.TryAdd(mapping.Target, value))
            {
                return ExternalProgramInvocation.Failure(
                    $"External program '{route.ResourceId}' contains an unsupported or duplicated input mapping.");
            }
        }

        if (!route.InputMappings.Any(mapping => string.Equals(
                mapping.Source,
                "$product.identity",
                StringComparison.Ordinal))
            || !route.InputMappings.Any(mapping => string.Equals(
                mapping.Source,
                "$product.model",
                StringComparison.Ordinal)))
        {
            return ExternalProgramInvocation.Failure(
                $"External program '{route.ResourceId}' must map Production Unit identity and product model.");
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
                return ExternalProgramInvocation.Failure(
                    $"External program '{route.ResourceId}' argument template is invalid: {rendered.Error}");
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
            return ExternalProgramInvocation.Failure(
                $"External program '{route.ResourceId}' command input is invalid JSON: {exception.Message}");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                ExternalProgramResourceContract.InvocationSchema);
            writer.WriteString("productionRunId", context.ProductionRunId.ToString());
            writer.WriteString("productionLineDefinitionId", context.ProductionLineDefinitionId);
            writer.WriteString("operationId", context.OperationId);
            writer.WriteNumber("operationAttempt", context.OperationAttempt);
            writer.WriteString("stationSystemId", context.StationSystemId);
            writer.WriteString("runtimeSessionId", context.SessionId.ToString());
            writer.WriteString("projectId", context.ProjectId);
            writer.WriteString("applicationId", context.ApplicationId);
            writer.WriteString("projectSnapshotId", context.ProjectSnapshotId);
            writer.WriteString("lotId", context.LotId);
            writer.WriteString("carrierId", context.CarrierId);
            writer.WriteString("fixtureId", context.FixtureId);
            writer.WriteString("deviceId", context.DeviceId);
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
            writer.WriteStartObject("productionUnit");
            writer.WriteString("id", context.ProductionUnitId.ToString());
            writer.WriteString("modelId", context.ProductionUnitIdentity.ModelId);
            writer.WriteString("modelCode", route.ProductModelCode);
            writer.WriteString("identityInputKey", context.ProductionUnitIdentity.InputKey);
            writer.WriteString("identityValue", context.ProductionUnitIdentity.Value);
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

        return ExternalProgramInvocation.Success(Encoding.UTF8.GetString(stream.ToArray()), arguments);
    }

    private static Dictionary<string, TokenValue> CreateTokenValues(
        RuntimeCommandExecutionContext context,
        ProjectReleaseExternalProgramCommandRoute route)
    {
        var values = new Dictionary<string, TokenValue>(StringComparer.Ordinal)
        {
            ["product.identity"] = TokenValue.Text(context.ProductionUnitIdentity.Value),
            ["product.model"] = TokenValue.Text(route.ProductModelCode),
            ["product.inputKey"] = TokenValue.Text(context.ProductionUnitIdentity.InputKey),
            ["run.id"] = TokenValue.Text(context.ProductionRunId.ToString()),
            ["line.id"] = TokenValue.Text(context.ProductionLineDefinitionId),
            ["operation.id"] = TokenValue.Text(context.OperationId),
            ["operation.attempt"] = TokenValue.Number(context.OperationAttempt),
            ["session.id"] = TokenValue.Text(context.SessionId.ToString()),
            ["station.id"] = TokenValue.Text(context.StationSystemId),
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
        AddOptional(values, "lot.id", context.LotId);
        AddOptional(values, "carrier.id", context.CarrierId);
        AddOptional(values, "fixture.id", context.FixtureId);
        AddOptional(values, "device.id", context.DeviceId);
        return values;
    }

    private static void AddOptional(
        IDictionary<string, TokenValue> values,
        string key,
        string? value)
    {
        if (value is not null)
        {
            values.Add(key, TokenValue.Text(value));
        }
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
            if (template[cursor] == '}')
            {
                return RenderedArgument.Failure("template contains an unmatched closing delimiter");
            }

            if (template[cursor] != '{')
            {
                builder.Append(template[cursor]);
                cursor++;
                continue;
            }

            if (cursor + 1 >= template.Length || template[cursor + 1] != '{')
            {
                return RenderedArgument.Failure("template contains an unmatched opening delimiter");
            }

            var closing = template.IndexOf("}}", cursor + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                return RenderedArgument.Failure("template contains an unmatched opening delimiter");
            }

            var token = template[(cursor + 2)..closing];
            if (!IsCanonical(token) || !values.TryGetValue(token, out var value))
            {
                return RenderedArgument.Failure($"placeholder '{{{{{token}}}}}' is not supported");
            }

            builder.Append(value.ArgumentValue);
            cursor = closing + 2;
        }

        return RenderedArgument.Success(builder.ToString());
    }

    public static RuntimeCommandExecutionResult MapCompletedProtocolResult(
        ProjectReleaseExternalProgramCommandRoute route,
        string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External program '{route.ResourceId}' returned no JSON result object.");
        }

        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || HasDuplicateObjectProperties(document.RootElement))
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External program '{route.ResourceId}' must return one JSON object without duplicate properties.");
            }

            if (!TryResolveResultPath(
                    document.RootElement,
                    route.OutcomeMapping.SourcePath,
                    out var outcomeValue)
                || outcomeValue.ValueKind != JsonValueKind.String)
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External program '{route.ResourceId}' outcome path "
                    + $"'{route.OutcomeMapping.SourcePath}' must resolve exactly to one JSON string.");
            }

            var outcomeToken = outcomeValue.GetString()!;

            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            var mappedValues = new List<(string TargetKey, ProductionContextValue Value)>();
            foreach (var mapping in route.ResultMappings)
            {
                if (!IsCanonical(mapping.TargetKey)
                    || string.Equals(
                        mapping.TargetKey,
                        RuntimeCommandEvidencePayload.PropertyName,
                        StringComparison.Ordinal)
                    || !targetKeys.Add(mapping.TargetKey)
                    || !TryResolveResultPath(
                        document.RootElement,
                        mapping.SourcePath,
                        out var value)
                    || !TryCreateContextValue(value, mapping.ValueKind, out var contextValue))
                {
                    return RuntimeCommandExecutionResult.Failed(
                        $"External program '{route.ResourceId}' result mapping "
                        + $"'{mapping.SourcePath}' -> '{mapping.TargetKey}' could not be resolved exactly.");
                }

                mappedValues.Add((mapping.TargetKey, contextValue!));
            }

            if (mappedValues.Count == 0)
            {
                return RuntimeCommandExecutionResult.Failed(
                    $"External program '{route.ResourceId}' has no result mappings.");
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
                    writer.WriteStartObject();
                    writer.WriteString("kind", mappedValue.Value.Kind.ToString());
                    writer.WriteString("value", mappedValue.Value.CanonicalValue);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            var mappedPayload = Encoding.UTF8.GetString(stream.ToArray());
            if (string.Equals(
                    outcomeToken,
                    route.OutcomeMapping.PassedToken,
                    StringComparison.Ordinal))
            {
                return RuntimeCommandExecutionResult.Completed(mappedPayload, ResultJudgement.Passed);
            }

            if (string.Equals(
                    outcomeToken,
                    route.OutcomeMapping.FailedToken,
                    StringComparison.Ordinal))
            {
                return RuntimeCommandExecutionResult.Completed(mappedPayload, ResultJudgement.Failed);
            }

            if (string.Equals(
                    outcomeToken,
                    route.OutcomeMapping.AbortedToken,
                    StringComparison.Ordinal))
            {
                return RuntimeCommandExecutionResult.Completed(mappedPayload, ResultJudgement.Aborted);
            }

            return RuntimeCommandExecutionResult.Failed(
                $"External program '{route.ResourceId}' returned unknown exact outcome token '{outcomeToken}'.");
        }
        catch (JsonException exception)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External program '{route.ResourceId}' returned invalid JSON: {exception.Message}");
        }
    }

    private static bool TryCreateContextValue(
        JsonElement value,
        ProductionContextValueKind expectedKind,
        out ProductionContextValue? contextValue)
    {
        contextValue = null;
        string? canonicalValue = expectedKind switch
        {
            ProductionContextValueKind.Text when value.ValueKind == JsonValueKind.String =>
                value.GetString(),
            ProductionContextValueKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                value.GetBoolean() ? "true" : "false",
            ProductionContextValueKind.WholeNumber when value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
            ProductionContextValueKind.FixedPoint when value.ValueKind == JsonValueKind.Number =>
                value.GetRawText(),
            ProductionContextValueKind.DateTimeUtc when value.ValueKind == JsonValueKind.String =>
                value.GetString(),
            _ => null
        };
        if (canonicalValue is null)
        {
            return false;
        }

        try
        {
            contextValue = new ProductionContextValue(expectedKind, canonicalValue);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryResolveResultPath(
        JsonElement root,
        string sourcePath,
        out JsonElement value)
    {
        value = root;
        if (!ExternalProgramResourceContract.IsSupportedResultPath(sourcePath))
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
            context.ProductionUnitId,
            context.ProductionLineDefinitionId,
            context.OperationId,
            context.OperationRunId,
            context.OperationAttempt,
            context.StationSystemId,
            context.ProductionUnitIdentity,
            context.LotId,
            context.CarrierId,
            context.FixtureId,
            context.DeviceId,
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
            context.ProjectSnapshotId,
            context.ResourceLeaseFences);
    }

    private static RuntimeCommandExecutionResult ToRuntimeResult(
        ExternalProgramExecutionResult result)
    {
        return result.ExecutionStatus switch
        {
            ExecutionStatus.Completed =>
                RuntimeCommandExecutionResult.Completed(result.StandardOutput),
            ExecutionStatus.Failed =>
                RuntimeCommandExecutionResult.Failed(
                    result.FailureReason ?? "External program execution failed."),
            ExecutionStatus.TimedOut =>
                RuntimeCommandExecutionResult.TimedOut(
                    result.FailureReason ?? "External program execution timed out."),
            ExecutionStatus.Canceled =>
                RuntimeCommandExecutionResult.Canceled(
                    result.FailureReason ?? "External program execution was canceled."),
            ExecutionStatus.Rejected =>
                RuntimeCommandExecutionResult.Rejected(
                    result.FailureReason ?? "External program execution was rejected."),
            _ => RuntimeCommandExecutionResult.Failed(
                $"External program host returned non-terminal status '{result.ExecutionStatus}'.")
        };
    }

    private static RuntimeCommandExecutionResult AttachEvidence(
        RuntimeCommandExecutionResult result,
        IReadOnlyCollection<ExternalProgramArtifact> artifacts)
    {
        if (result.Outcome == RuntimeCommandExecutionOutcome.Rejected && artifacts.Count == 0)
        {
            return result;
        }

        string payload;
        try
        {
            payload = RuntimeCommandEvidencePayload.Attach(
                result.Payload,
                ToExecutionStatus(result.Outcome),
                result.ResultJudgement,
                artifacts.Select(artifact => new RuntimeCommandArtifactEvidence(
                        artifact.Name,
                        artifact.Kind.ToString(),
                        artifact.StorageKey,
                        artifact.MediaType,
                        artifact.SizeBytes,
                        artifact.Sha256))
                    .ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or JsonException)
        {
            return RuntimeCommandExecutionResult.Failed(
                $"External program evidence is invalid: {exception.Message}");
        }

        return result.Outcome switch
        {
            RuntimeCommandExecutionOutcome.Completed => result.ResultJudgement switch
            {
                ResultJudgement.Passed =>
                    RuntimeCommandExecutionResult.Completed(payload, ResultJudgement.Passed),
                ResultJudgement.Failed =>
                    RuntimeCommandExecutionResult.Completed(payload, ResultJudgement.Failed),
                ResultJudgement.Aborted =>
                    RuntimeCommandExecutionResult.Completed(payload, ResultJudgement.Aborted),
                ResultJudgement.NotApplicable => RuntimeCommandExecutionResult.Completed(payload),
                _ => RuntimeCommandExecutionResult.Failed("External program judgement is unsupported.", payload)
            },
            RuntimeCommandExecutionOutcome.Failed => RuntimeCommandExecutionResult.Failed(
                result.Reason ?? "External program execution failed.",
                payload),
            RuntimeCommandExecutionOutcome.TimedOut => RuntimeCommandExecutionResult.TimedOut(
                result.Reason ?? "External program execution timed out.",
                payload),
            RuntimeCommandExecutionOutcome.Canceled => RuntimeCommandExecutionResult.Canceled(
                result.Reason ?? "External program execution was canceled.",
                payload),
            _ => result
        };
    }

    private static ExecutionStatus ToExecutionStatus(RuntimeCommandExecutionOutcome outcome)
    {
        return outcome switch
        {
            RuntimeCommandExecutionOutcome.Completed => ExecutionStatus.Completed,
            RuntimeCommandExecutionOutcome.Failed => ExecutionStatus.Failed,
            RuntimeCommandExecutionOutcome.Rejected => ExecutionStatus.Rejected,
            RuntimeCommandExecutionOutcome.TimedOut => ExecutionStatus.TimedOut,
            RuntimeCommandExecutionOutcome.Canceled => ExecutionStatus.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported execution status.")
        };
    }

    private static bool IsCanonical(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !char.IsWhiteSpace(value[0])
               && !char.IsWhiteSpace(value[^1]);
    }

    private static bool IsValidOutcomeMapping(ExternalProgramRouteOutcomeMapping mapping)
    {
        return ExternalProgramResourceContract.IsSupportedResultPath(mapping.SourcePath)
            && IsCanonical(mapping.PassedToken)
            && IsCanonical(mapping.FailedToken)
            && IsCanonical(mapping.AbortedToken)
            && !string.Equals(mapping.PassedToken, mapping.FailedToken, StringComparison.Ordinal)
            && !string.Equals(mapping.PassedToken, mapping.AbortedToken, StringComparison.Ordinal)
            && !string.Equals(mapping.FailedToken, mapping.AbortedToken, StringComparison.Ordinal);
    }

    private sealed record ExternalProgramInvocation(
        string? Payload,
        IReadOnlyCollection<string>? Arguments,
        string? Error)
    {
        public static ExternalProgramInvocation Success(
            string payload,
            IReadOnlyCollection<string> arguments) => new(payload, arguments, null);

        public static ExternalProgramInvocation Failure(string error) => new(null, null, error);
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
