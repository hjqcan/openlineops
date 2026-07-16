using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Plugins.Application.Trials;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Devices.Api.ExternalPrograms;

public sealed class ExternalProgramResourceTrialExecutor : IExternalProgramTrialExecutor
{
    private readonly IExternalProgramHost _host;
    private readonly IPluginProviderTrialRunner _providerTrialRunner;

    public ExternalProgramResourceTrialExecutor(
        IExternalProgramHost host,
        IPluginProviderTrialRunner providerTrialRunner)
    {
        _host = host;
        _providerTrialRunner = providerTrialRunner;
    }

    public async ValueTask<Result<ExternalProgramProtocolTrialResult>> ExecuteAsync(
        ProjectApplicationWorkspaceScope scope,
        ExternalProgramResource resource,
        ExternalProgramProtocolTrialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var values = ParseInputs(resource, request);
            var arguments = RenderArguments(resource, values);
            var invocation = CreateInvocation(resource, values, arguments);
            RuntimeCommandExecutionResult mapped;
            IReadOnlyCollection<ExternalProgramTrialArtifact> artifacts;
            if (resource.LaunchKind == ExternalProgramLaunchKind.ApplicationExecutable)
            {
                var entryPoint = resource.EntryPoint!;
                var entryPointFile = resource.Files.Single(file => string.Equals(
                    file.RelativePath,
                    entryPoint,
                    StringComparison.Ordinal));
                var completeInventory = await BuildCompleteInventoryAsync(
                        scope,
                        resource,
                        cancellationToken)
                    .ConfigureAwait(false);
                var hostResult = await _host.ExecuteAsync(
                        new ExternalProgramExecutionRequest(
                            resource.ResourceId,
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            scope.ApplicationRootPath,
                            $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resource.ResourceId}",
                            $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resource.ResourceId}/{entryPoint}",
                            entryPointFile.SizeBytes,
                            entryPointFile.Sha256,
                            completeInventory,
                            arguments,
                            invocation,
                            TimeSpan.FromMilliseconds(resource.ExecutionLimits.TimeoutMilliseconds),
                            ToPolicy(resource)),
                        cancellationToken)
                    .ConfigureAwait(false);
                mapped = hostResult.ExecutionStatus == ExecutionStatus.Completed
                    ? ProjectReleaseExternalProgramCommandExecutor.MapCompletedProtocolResult(
                        ToRoute(scope, resource, entryPointFile),
                        hostResult.StandardOutput)
                    : ToRuntimeResult(hostResult);
                artifacts = hostResult.Artifacts.Select(artifact => new ExternalProgramTrialArtifact(
                    artifact.Name,
                    artifact.Kind.ToString(),
                    artifact.MediaType,
                    artifact.SizeBytes,
                    artifact.Sha256)).ToArray();
            }
            else
            {
                var providerResult = await _providerTrialRunner.ExecuteAsync(
                        scope,
                        new PluginProviderTrialRequest(
                            resource.ProviderKind!,
                            resource.ProviderKey!,
                            resource.CapabilityId,
                            resource.CommandName,
                            invocation,
                            checked((int)resource.ExecutionLimits.TimeoutMilliseconds)),
                        cancellationToken)
                    .ConfigureAwait(false);
                mapped = providerResult.Outcome == PluginProviderTrialOutcome.Completed
                    ? ProjectReleaseExternalProgramCommandExecutor.MapCompletedProtocolResult(
                        ToRoute(scope, resource, entryPointFile: null),
                        providerResult.ResultPayload)
                    : ToRuntimeResult(providerResult);
                artifacts = [];
            }

            return Result.Success(new ExternalProgramProtocolTrialResult(
                resource.ResourceId,
                resource.LaunchKind.ToString(),
                resource.ContentSha256,
                ToExecutionStatus(mapped.Outcome).ToString(),
                mapped.ResultJudgement.ToString(),
                mapped.Payload,
                mapped.Reason,
                artifacts));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or JsonException
                                          or OverflowException)
        {
            return Result.Failure<ExternalProgramProtocolTrialResult>(ApplicationError.Validation(
                "Projects.ExternalProgramTrialInvalid",
                exception.Message));
        }
    }

    private static Dictionary<string, TrialValue> ParseInputs(
        ExternalProgramResource resource,
        ExternalProgramProtocolTrialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Inputs);
        var expected = resource.InputMappings.Select(mapping => mapping.Target).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(request.Inputs.Keys))
        {
            throw new InvalidDataException(
                "Protocol trial inputs must exactly match the resource input mapping targets.");
        }

        var values = new Dictionary<string, TrialValue>(StringComparer.Ordinal);
        foreach (var input in request.Inputs)
        {
            if (!ExternalProgramResourceContract.IsCanonical(input.Key)
                || input.Key.Length > 128
                || input.Value is null)
            {
                throw new InvalidDataException("Protocol trial input identity is invalid.");
            }

            values.Add(input.Key, TrialValue.Parse(input.Value));
        }

        return values;
    }

    private static string[] RenderArguments(
        ExternalProgramResource resource,
        Dictionary<string, TrialValue> inputs)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in resource.InputMappings)
        {
            var value = inputs[mapping.Target].ArgumentValue;
            tokens.Add(mapping.Source[1..], value);
            tokens.Add($"input.{mapping.Target}", value);
        }

        var trialId = Guid.NewGuid().ToString("D");
        AddGenerated(tokens, "run.id", trialId);
        AddGenerated(tokens, "line.id", "protocol-trial");
        AddGenerated(tokens, "operation.id", "protocol-trial");
        AddGenerated(tokens, "operation.attempt", "1");
        AddGenerated(tokens, "session.id", trialId);
        AddGenerated(tokens, "station.id", "protocol-trial-station");
        AddGenerated(tokens, "lot.id", "protocol-trial-lot");
        AddGenerated(tokens, "carrier.id", "protocol-trial-carrier");
        AddGenerated(tokens, "fixture.id", "protocol-trial-fixture");
        AddGenerated(tokens, "device.id", "protocol-trial-device");
        AddGenerated(tokens, "configuration.id", "protocol-trial-configuration");
        AddGenerated(tokens, "step.id", trialId);
        AddGenerated(tokens, "command.id", trialId);
        AddGenerated(tokens, "command.name", resource.CommandName);
        AddGenerated(tokens, "node.id", "protocol-trial-node");
        AddGenerated(tokens, "action.id", "protocol-trial-action");
        AddGenerated(tokens, "capability.id", resource.CapabilityId);
        AddGenerated(tokens, "project.id", "protocol-trial-project");
        AddGenerated(tokens, "application.id", "protocol-trial-application");
        AddGenerated(tokens, "snapshot.id", "protocol-trial-snapshot");
        AddGenerated(tokens, "target.kind", "System");
        AddGenerated(tokens, "target.id", "protocol-trial-station");

        return resource.ArgumentTemplates.Select(template => Render(template, tokens)).ToArray();
    }

    private static void AddGenerated(Dictionary<string, string> tokens, string key, string value)
    {
        tokens.TryAdd(key, value);
    }

    private static string Render(string template, Dictionary<string, string> tokens)
    {
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
                throw new InvalidDataException("External program argument template is incomplete.");
            }

            var key = template[(opening + 2)..closing];
            if (!tokens.TryGetValue(key, out var value))
            {
                throw new InvalidDataException($"External program argument token '{key}' is unavailable.");
            }

            builder.Append(value);
            cursor = closing + 2;
        }

        return builder.ToString();
    }

    private static string CreateInvocation(
        ExternalProgramResource resource,
        Dictionary<string, TrialValue> inputs,
        string[] arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", ExternalProgramResourceContract.InvocationSchema);
            writer.WriteBoolean("protocolTrial", true);
            writer.WriteString("resourceId", resource.ResourceId);
            writer.WriteString("capabilityId", resource.CapabilityId);
            writer.WriteString("commandName", resource.CommandName);
            writer.WriteNumber("operationAttempt", 1);
            writer.WriteStartObject("inputs");
            foreach (var input in inputs.OrderBy(item => item.Key, StringComparer.Ordinal))
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ExternalProgramExecutionPolicy ToPolicy(ExternalProgramResource resource) => new(
        resource.PermissionProfile.ProfileName,
        resource.PermissionProfile.NetworkAccessAllowed,
        resource.PermissionProfile.AllowedEnvironmentVariables,
        resource.ExecutionLimits.MaximumProcessCount,
        resource.ExecutionLimits.MaximumWorkingSetBytes,
        resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
        resource.ExecutionLimits.MaximumStandardOutputBytes,
        resource.ExecutionLimits.MaximumStandardErrorBytes,
        resource.ExecutionLimits.MaximumArtifactCount,
        resource.ExecutionLimits.MaximumArtifactBytes,
        resource.ExecutionLimits.MaximumTotalArtifactBytes);

    private static async ValueTask<ExternalProgramExecutionFile[]> BuildCompleteInventoryAsync(
        ProjectApplicationWorkspaceScope scope,
        ExternalProgramResource resource,
        CancellationToken cancellationToken)
    {
        var descriptorPath = Path.Combine(
            scope.ApplicationRootPath,
            ExternalProgramResourceContract.ResourceDirectoryName,
            resource.ResourceId,
            ExternalProgramResourceContract.DescriptorFileName);
        var descriptor = new FileInfo(descriptorPath);
        if (!descriptor.Exists || descriptor.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("External program resource descriptor is missing or redirected.");
        }

        await using var stream = new FileStream(
            descriptorPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var descriptorSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false))
            .ToLowerInvariant();
        return resource.Files
            .Select(file => new ExternalProgramExecutionFile(
                file.RelativePath,
                file.SizeBytes,
                file.Sha256))
            .Append(new ExternalProgramExecutionFile(
                ExternalProgramResourceContract.DescriptorFileName,
                descriptor.Length,
                descriptorSha256))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProjectReleaseExternalProgramCommandRoute ToRoute(
        ProjectApplicationWorkspaceScope scope,
        ExternalProgramResource resource,
        ExternalProgramResourceFile? entryPointFile) => new(
        resource.ProviderKind ?? ProjectReleaseRuntimeProviderKinds.ExternalSystem,
        resource.ProviderKey ?? resource.ResourceId,
        new DeviceCapabilityId(resource.CapabilityId),
        resource.ResourceId,
        resource.LaunchKind.ToString(),
        scope.ApplicationRootPath,
        $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resource.ResourceId}",
        "protocol-trial-model",
        "protocol-trial-model",
        "identity",
        resource.EntryPoint,
        entryPointFile?.SizeBytes,
        entryPointFile?.Sha256,
        resource.Files.Select(file => new ExternalProgramRouteFile(
            file.RelativePath,
            file.SizeBytes,
            file.Sha256)).ToArray(),
        resource.ArgumentTemplates,
        resource.InputMappings.Select(mapping => new ExternalProgramRouteInputMapping(
            mapping.Source,
            mapping.Target)).ToArray(),
        resource.ResultMappings.Select(mapping => new ExternalProgramRouteResultMapping(
            mapping.SourcePath,
            mapping.TargetKey,
            mapping.ValueKind)).ToArray(),
        new ExternalProgramRouteOutcomeMapping(
            resource.OutcomeMapping.SourcePath,
            resource.OutcomeMapping.PassedToken,
            resource.OutcomeMapping.FailedToken,
            resource.OutcomeMapping.AbortedToken),
        new ExternalProgramRoutePermissionProfile(
            resource.PermissionProfile.ProfileName,
            resource.PermissionProfile.NetworkAccessAllowed,
            resource.PermissionProfile.AllowedEnvironmentVariables),
        new ExternalProgramRouteExecutionLimits(
            resource.ExecutionLimits.TimeoutMilliseconds,
            resource.ExecutionLimits.MaximumProcessCount,
            resource.ExecutionLimits.MaximumWorkingSetBytes,
            resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
            resource.ExecutionLimits.MaximumStandardOutputBytes,
            resource.ExecutionLimits.MaximumStandardErrorBytes,
            resource.ExecutionLimits.MaximumArtifactCount,
            resource.ExecutionLimits.MaximumArtifactBytes,
            resource.ExecutionLimits.MaximumTotalArtifactBytes),
        ProviderRoute: null);

    private static RuntimeCommandExecutionResult ToRuntimeResult(ExternalProgramExecutionResult result) =>
        result.ExecutionStatus switch
        {
            ExecutionStatus.Failed => RuntimeCommandExecutionResult.Failed(
                result.FailureReason ?? "External program trial failed."),
            ExecutionStatus.TimedOut => RuntimeCommandExecutionResult.TimedOut(
                result.FailureReason ?? "External program trial timed out."),
            ExecutionStatus.Canceled => RuntimeCommandExecutionResult.Canceled(
                result.FailureReason ?? "External program trial was canceled."),
            ExecutionStatus.Rejected => RuntimeCommandExecutionResult.Rejected(
                result.FailureReason ?? "External program trial was rejected."),
            _ => RuntimeCommandExecutionResult.Failed(
                $"External program trial returned unsupported status {result.ExecutionStatus}.")
        };

    private static RuntimeCommandExecutionResult ToRuntimeResult(
        PluginProviderTrialResult result) => result.Outcome switch
        {
            PluginProviderTrialOutcome.Failed => RuntimeCommandExecutionResult.Failed(
                result.FailureReason ?? "External program Provider trial failed."),
            PluginProviderTrialOutcome.TimedOut => RuntimeCommandExecutionResult.TimedOut(
                result.FailureReason ?? "External program Provider trial timed out."),
            PluginProviderTrialOutcome.Canceled => RuntimeCommandExecutionResult.Canceled(
                result.FailureReason ?? "External program Provider trial was canceled."),
            PluginProviderTrialOutcome.Rejected => RuntimeCommandExecutionResult.Rejected(
                result.FailureReason ?? "External program Provider trial was rejected."),
            _ => RuntimeCommandExecutionResult.Failed(
                $"External program Provider trial returned unsupported status {result.Outcome}.")
        };

    private static ExecutionStatus ToExecutionStatus(RuntimeCommandExecutionOutcome outcome) => outcome switch
    {
        RuntimeCommandExecutionOutcome.Completed => ExecutionStatus.Completed,
        RuntimeCommandExecutionOutcome.Failed => ExecutionStatus.Failed,
        RuntimeCommandExecutionOutcome.TimedOut => ExecutionStatus.TimedOut,
        RuntimeCommandExecutionOutcome.Canceled => ExecutionStatus.Canceled,
        RuntimeCommandExecutionOutcome.Rejected => ExecutionStatus.Rejected,
        _ => ExecutionStatus.Failed
    };

    private sealed record TrialValue(
        ExternalProgramTrialInputKind Kind,
        string ArgumentValue,
        string? StringValue,
        long? IntegerValue,
        decimal? DecimalValue,
        bool? BooleanValue)
    {
        public static TrialValue Parse(ExternalProgramTrialInputValue input)
        {
            var value = input.CanonicalValue;
            if (!ExternalProgramResourceContract.IsCanonical(value)
                || value.Length > 4096
                || value.Any(char.IsControl))
            {
                throw new InvalidDataException("Protocol trial input value is not canonical.");
            }

            return input.Kind switch
            {
                ExternalProgramTrialInputKind.Text => new(input.Kind, value, value, null, null, null),
                ExternalProgramTrialInputKind.IntegralNumber when long.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var integer)
                    && string.Equals(
                        value,
                        integer.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal) =>
                    new(input.Kind, value, null, integer, null, null),
                ExternalProgramTrialInputKind.FractionalNumber when decimal.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var number)
                    && string.Equals(
                        value,
                        number.ToString("0.#############################", CultureInfo.InvariantCulture),
                        StringComparison.Ordinal) =>
                    new(input.Kind, value, null, null, number, null),
                ExternalProgramTrialInputKind.Logical when value is "true" or "false" =>
                    new(input.Kind, value, null, null, null, value == "true"),
                _ => throw new InvalidDataException("Protocol trial input kind or canonical value is invalid.")
            };
        }

        public void Write(Utf8JsonWriter writer, string propertyName)
        {
            switch (Kind)
            {
                case ExternalProgramTrialInputKind.Text:
                    writer.WriteString(propertyName, StringValue);
                    break;
                case ExternalProgramTrialInputKind.IntegralNumber:
                    writer.WriteNumber(propertyName, IntegerValue!.Value);
                    break;
                case ExternalProgramTrialInputKind.FractionalNumber:
                    writer.WriteNumber(propertyName, DecimalValue!.Value);
                    break;
                case ExternalProgramTrialInputKind.Logical:
                    writer.WriteBoolean(propertyName, BooleanValue!.Value);
                    break;
                default:
                    throw new InvalidDataException("Protocol trial input kind is unsupported.");
            }
        }
    }
}
