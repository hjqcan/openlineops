using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Trials;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Infrastructure.Trials;

public sealed class ExternalProcessPluginProviderTrialRunner : IPluginProviderTrialRunner
{
    public const string DeviceProviderKind = "PluginCommand";
    public const string ProcessProviderKind = "ProcessCommandProvider";

    private readonly IPluginPackageCatalog _packageCatalog;
    private readonly IPluginManifestValidator _manifestValidator;
    private readonly IExternalPluginProcessRunner _processRunner;
    private readonly IExternalPluginProcessRegistry _processRegistry;
    private readonly ExternalProcessPluginHostOptions _hostOptions;
    private readonly IExternalPluginProcessEventSink _eventSink;

    public ExternalProcessPluginProviderTrialRunner(
        IPluginPackageCatalog packageCatalog,
        IPluginManifestValidator manifestValidator,
        IExternalPluginProcessRunner processRunner,
        IExternalPluginProcessRegistry processRegistry,
        ExternalProcessPluginHostOptions hostOptions,
        IExternalPluginProcessEventSink eventSink)
    {
        _packageCatalog = packageCatalog;
        _manifestValidator = manifestValidator;
        _processRunner = processRunner;
        _processRegistry = processRegistry;
        _hostOptions = hostOptions;
        _eventSink = eventSink;
    }

    public async ValueTask<PluginProviderTrialResult> ExecuteAsync(
        ProjectApplicationWorkspaceScope scope,
        PluginProviderTrialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProviderKind is not (DeviceProviderKind or ProcessProviderKind)
            || !IsCanonical(request.ProviderKey)
            || !IsCanonical(request.Capability)
            || !IsCanonical(request.CommandName)
            || request.TimeoutMilliseconds <= 0)
        {
            return Rejected("Plugin provider trial request is invalid.");
        }

        IReadOnlyCollection<PluginPackageDescriptor> packages;
        try
        {
            packages = await _packageCatalog.DiscoverAsync(scope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return Rejected(exception.Message);
        }

        var candidates = request.ProviderKind == DeviceProviderKind
            ? ResolveDeviceCandidates(packages, request)
            : ResolveProcessCandidates(packages, request);
        if (candidates.Length != 1)
        {
            return Rejected(
                $"Provider {request.ProviderKind}/{request.ProviderKey} must resolve to exactly one valid command in Project '{scope.ProjectId}', Application '{scope.ApplicationId}'.");
        }

        try
        {
            var candidate = candidates[0];
            var activator = new ExternalProcessPluginInstanceActivator(
                _processRunner,
                _processRegistry,
                _hostOptions,
                _eventSink);
            var activation = await activator.ActivateAsync(scope, candidate.Package, cancellationToken)
                .ConfigureAwait(false);
            if (!activation.Succeeded)
            {
                return Rejected(activation.FailureReason ?? "External plugin activation failed.");
            }

            await using var plugin = activation.Plugin!;
            var initialization = await plugin.InitializeAsync(
                    EmptyServiceProvider.Instance,
                    cancellationToken)
                .ConfigureAwait(false);
            if (initialization != PluginInitializationStatus.Initialized)
            {
                return Failed("External plugin process failed to initialize.");
            }

            var identity = new PluginPackageExecutionIdentity(
                scope.ProjectId,
                scope.ApplicationId,
                candidate.Package.RuntimeIdentity);
            return candidate.DeviceCommand is not null
                ? await ExecuteDeviceAsync(request, candidate, identity, cancellationToken)
                    .ConfigureAwait(false)
                : await ExecuteProcessAsync(request, candidate, identity, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            return Failed(exception.Message);
        }
    }

    private PluginCommandCandidate[] ResolveDeviceCandidates(
        IReadOnlyCollection<PluginPackageDescriptor> packages,
        PluginProviderTrialRequest request)
    {
        return packages
            .Where(PackageIsValid)
            .SelectMany(package => (package.Manifest.DeviceCommands ?? [])
                .Where(command => CommandMatches(
                    package.Manifest.Id,
                    command.Id,
                    command.Capability,
                    command.CommandName,
                    request))
                .Select(command => new PluginCommandCandidate(package, command, null)))
            .Take(2)
            .ToArray();
    }

    private PluginCommandCandidate[] ResolveProcessCandidates(
        IReadOnlyCollection<PluginPackageDescriptor> packages,
        PluginProviderTrialRequest request)
    {
        return packages
            .Where(PackageIsValid)
            .SelectMany(package => (package.Manifest.ProcessCommands ?? [])
                .Where(command => CommandMatches(
                    package.Manifest.Id,
                    command.Id,
                    command.Capability,
                    command.CommandName,
                    request))
                .Select(command => new PluginCommandCandidate(package, null, command)))
            .Take(2)
            .ToArray();
    }

    private bool PackageIsValid(PluginPackageDescriptor package)
    {
        return package.RuntimeIdentity.IsComplete
               && _manifestValidator.Validate(package.Manifest).IsValid;
    }

    private static bool CommandMatches(
        string pluginId,
        string commandDefinitionId,
        string capability,
        string commandName,
        PluginProviderTrialRequest request)
    {
        return (string.Equals(pluginId, request.ProviderKey, StringComparison.Ordinal)
                || string.Equals(commandDefinitionId, request.ProviderKey, StringComparison.Ordinal))
               && string.Equals(capability, request.Capability, StringComparison.Ordinal)
               && string.Equals(commandName, request.CommandName, StringComparison.Ordinal);
    }

    private async ValueTask<PluginProviderTrialResult> ExecuteDeviceAsync(
        PluginProviderTrialRequest request,
        PluginCommandCandidate candidate,
        PluginPackageExecutionIdentity identity,
        CancellationToken cancellationToken)
    {
        var command = candidate.DeviceCommand!;
        var result = await new ExternalProcessPluginDeviceCommandInvoker(_processRegistry)
            .ExecuteAsync(
                new PluginDeviceCommandInvocationRequest(
                    candidate.Package.Manifest.Id,
                    "protocol-trial-device",
                    command.Id,
                    command.Capability,
                    command.CommandName,
                    request.InputPayload,
                    EffectiveTimeout(request.TimeoutMilliseconds, command.TimeoutMilliseconds),
                    identity),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            PluginDeviceCommandInvocationOutcome.Completed => Completed(result.ResultPayload),
            PluginDeviceCommandInvocationOutcome.TimedOut => TimedOut(result.FailureReason),
            PluginDeviceCommandInvocationOutcome.Rejected => Rejected(result.FailureReason),
            _ => Failed(result.FailureReason)
        };
    }

    private async ValueTask<PluginProviderTrialResult> ExecuteProcessAsync(
        PluginProviderTrialRequest request,
        PluginCommandCandidate candidate,
        PluginPackageExecutionIdentity identity,
        CancellationToken cancellationToken)
    {
        var command = candidate.ProcessCommand!;
        var trialId = Guid.NewGuid().ToString("D");
        var result = await new ExternalProcessPluginProcessCommandInvoker(_processRegistry)
            .ExecuteAsync(
                new PluginProcessCommandInvocationRequest(
                    candidate.Package.Manifest.Id,
                    trialId,
                    "protocol-trial-station",
                    "protocol-trial-configuration",
                    trialId,
                    trialId,
                    "protocol-trial-node",
                    command.Id,
                    command.Capability,
                    command.CommandName,
                    request.InputPayload,
                    EffectiveTimeout(request.TimeoutMilliseconds, command.TimeoutMilliseconds),
                    identity,
                    "System",
                    "protocol-trial-station"),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            PluginProcessCommandInvocationOutcome.Completed => Completed(result.ResultPayload),
            PluginProcessCommandInvocationOutcome.TimedOut => TimedOut(result.FailureReason),
            PluginProcessCommandInvocationOutcome.Canceled => new PluginProviderTrialResult(
                PluginProviderTrialOutcome.Canceled,
                null,
                result.FailureReason),
            PluginProcessCommandInvocationOutcome.Rejected => Rejected(result.FailureReason),
            _ => Failed(result.FailureReason)
        };
    }

    private static int EffectiveTimeout(int resourceTimeout, int commandTimeout) =>
        Math.Min(resourceTimeout, commandTimeout);

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static PluginProviderTrialResult Completed(string? payload) => new(
        PluginProviderTrialOutcome.Completed,
        payload,
        null);

    private static PluginProviderTrialResult Failed(string? reason) => new(
        PluginProviderTrialOutcome.Failed,
        null,
        NormalizeReason(reason, "External plugin command failed."));

    private static PluginProviderTrialResult TimedOut(string? reason) => new(
        PluginProviderTrialOutcome.TimedOut,
        null,
        NormalizeReason(reason, "External plugin command timed out."));

    private static PluginProviderTrialResult Rejected(string? reason) => new(
        PluginProviderTrialOutcome.Rejected,
        null,
        NormalizeReason(reason, "External plugin command was rejected."));

    private static string NormalizeReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();

    private sealed record PluginCommandCandidate(
        PluginPackageDescriptor Package,
        PluginDeviceCommandDefinition? DeviceCommand,
        PluginProcessCommandDefinition? ProcessCommand);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
