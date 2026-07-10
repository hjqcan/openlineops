using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalProcessPluginInstanceActivator : IPluginInstanceActivator
{
    private readonly IExternalPluginProcessRunner _processRunner;
    private readonly IExternalPluginProcessRegistry _processRegistry;
    private readonly ExternalProcessPluginHostOptions _options;
    private readonly IExternalPluginProcessEventSink _eventSink;

    public ExternalProcessPluginInstanceActivator(
        IExternalPluginProcessRunner processRunner,
        IExternalPluginProcessRegistry processRegistry,
        ExternalProcessPluginHostOptions options,
        IExternalPluginProcessEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(processRegistry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);
        _processRunner = processRunner;
        _processRegistry = processRegistry;
        _options = options;
        _eventSink = eventSink;
    }

    public ValueTask<PluginActivationResult> ActivateAsync(
        PluginPackageDescriptor package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        var entryAssemblyPath = ResolveEntryAssemblyPath(package);
        if (entryAssemblyPath is null)
        {
            return ValueTask.FromResult(PluginActivationResult.Failure(
                $"Plugin entry assembly '{package.Manifest.EntryAssembly}' is outside package directory '{package.PackagePath}'."));
        }

        if (!File.Exists(entryAssemblyPath))
        {
            return ValueTask.FromResult(PluginActivationResult.Failure(
                $"Plugin entry assembly '{entryAssemblyPath}' was not found."));
        }

        var trustResult = new ExternalPluginPackageTrustPolicy(_options.Sandbox)
            .Evaluate(package, entryAssemblyPath);
        if (!trustResult.Succeeded)
        {
            Record(
                ExternalPluginProcessEventKind.TrustRejected,
                package.Manifest.Id,
                trustResult.FailureReason ?? "Plugin package trust policy rejected activation.");

            return ValueTask.FromResult(PluginActivationResult.Failure(
                trustResult.FailureReason ?? "Plugin package trust policy rejected activation."));
        }

        var sandboxPolicyFailure = ValidateSandboxPolicy(package.Manifest.Id);
        if (sandboxPolicyFailure is not null)
        {
            Record(
                ExternalPluginProcessEventKind.SandboxRejected,
                package.Manifest.Id,
                sandboxPolicyFailure);

            return ValueTask.FromResult(PluginActivationResult.Failure(sandboxPolicyFailure));
        }

        var request = new ExternalPluginProcessStartRequest(
            package.Manifest,
            Path.GetFullPath(package.PackagePath),
            Path.GetFullPath(package.ManifestPath),
            entryAssemblyPath,
            package.Manifest.EntryType,
            BuildEnvironmentVariables(package, entryAssemblyPath, trustResult.EntryAssemblySha256, _options.Sandbox));

        return ValueTask.FromResult(PluginActivationResult.Success(
            new ExternalProcessOpenLineOpsPlugin(
                package.Manifest,
                package.RuntimeIdentity,
                request,
                _processRunner,
                _processRegistry,
                _eventSink)));
    }

    private string? ValidateSandboxPolicy(string pluginId)
    {
        _ = ExternalPluginIsolationModes.Parse(_options.Sandbox.IsolationMode);

        if (ExternalPluginIsolationModes.IsLeastPrivilegeIdentity(_options.Sandbox.IsolationMode)
            && string.IsNullOrWhiteSpace(_options.Sandbox.LeastPrivilegeIdentity))
        {
            return $"Plugin '{pluginId}' requires least-privilege identity sandbox execution, but no least-privilege identity is configured.";
        }

        if (ExternalPluginIsolationModes.IsContainer(_options.Sandbox.IsolationMode)
            && string.IsNullOrWhiteSpace(_options.Sandbox.ContainerImage))
        {
            return $"Plugin '{pluginId}' requires container sandbox execution, but no container image is configured.";
        }

        if (!_options.Sandbox.RequireLeastPrivilegeExecution)
        {
            return null;
        }

        if (_options.Sandbox.HasLeastPrivilegeExecution)
        {
            return null;
        }

        return $"Plugin '{pluginId}' requires least-privilege sandbox execution, but no least-privilege identity or container image is configured.";
    }

    private static Dictionary<string, string> BuildEnvironmentVariables(
        PluginPackageDescriptor package,
        string entryAssemblyPath,
        string? entryAssemblySha256,
        ExternalPluginSandboxOptions sandboxOptions)
    {
        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENLINEOPS_PLUGIN_ID"] = package.Manifest.Id,
            ["OPENLINEOPS_PLUGIN_PACKAGE_PATH"] = Path.GetFullPath(package.PackagePath),
            ["OPENLINEOPS_PLUGIN_MANIFEST_PATH"] = Path.GetFullPath(package.ManifestPath),
            ["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY"] = entryAssemblyPath,
            ["OPENLINEOPS_PLUGIN_ENTRY_TYPE"] = package.Manifest.EntryType,
            ["OPENLINEOPS_PLUGIN_SANDBOX_ISOLATION_MODE"] = sandboxOptions.IsolationMode,
            ["OPENLINEOPS_PLUGIN_SANDBOX_MAX_COMMAND_TIMEOUT_MS"] = ToTimeoutMilliseconds(
                sandboxOptions.MaxCommandTimeout).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        AddOptional(environmentVariables, "OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY_SHA256", entryAssemblySha256);
        AddOptional(environmentVariables, "OPENLINEOPS_PLUGIN_SANDBOX_IDENTITY", sandboxOptions.LeastPrivilegeIdentity);
        AddOptional(environmentVariables, "OPENLINEOPS_PLUGIN_SANDBOX_CONTAINER_IMAGE", sandboxOptions.ContainerImage);

        return environmentVariables;
    }

    private static string? ResolveEntryAssemblyPath(PluginPackageDescriptor package)
    {
        var packagePath = Path.GetFullPath(package.PackagePath);
        var assemblyPath = Path.GetFullPath(Path.Combine(packagePath, package.Manifest.EntryAssembly));

        return IsPathInsideDirectory(assemblyPath, packagePath)
            ? assemblyPath
            : null;
    }

    private static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, candidatePath);

        return !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private void Record(
        ExternalPluginProcessEventKind kind,
        string pluginId,
        string message,
        string? detail = null)
    {
        _eventSink.Record(new ExternalPluginProcessEvent(
            kind,
            pluginId,
            message,
            DateTimeOffset.UtcNow,
            detail));
    }

    private static void AddOptional(
        Dictionary<string, string> environmentVariables,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            environmentVariables[name] = value.Trim();
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        if (timeout.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private sealed class ExternalProcessOpenLineOpsPlugin(
        PluginManifest manifest,
        PluginPackageRuntimeIdentity packageIdentity,
        ExternalPluginProcessStartRequest processStartRequest,
        IExternalPluginProcessRunner processRunner,
        IExternalPluginProcessRegistry processRegistry,
        IExternalPluginProcessEventSink eventSink) : IOpenLineOpsPlugin
    {
        private IExternalPluginProcess? _process;

        public PluginManifest Manifest { get; } = manifest;

        public async ValueTask<PluginInitializationStatus> InitializeAsync(
            IServiceProvider services,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is not null)
            {
                return _process.HasExited
                    ? PluginInitializationStatus.Failed
                    : PluginInitializationStatus.Initialized;
            }

            eventSink.Record(new ExternalPluginProcessEvent(
                ExternalPluginProcessEventKind.Starting,
                Manifest.Id,
                $"Starting external plugin process '{Manifest.Id}'.",
                DateTimeOffset.UtcNow));

            _process = await processRunner
                .StartAsync(processStartRequest, cancellationToken)
                .ConfigureAwait(false);

            if (!_process.HasExited)
            {
                if (packageIdentity.IsComplete)
                {
                    processRegistry.Register(packageIdentity, _process);
                }
                else
                {
                    processRegistry.Register(Manifest.Id, _process);
                }

                eventSink.Record(new ExternalPluginProcessEvent(
                    ExternalPluginProcessEventKind.Started,
                    Manifest.Id,
                    $"External plugin process '{Manifest.Id}' started.",
                    DateTimeOffset.UtcNow));
            }
            else
            {
                eventSink.Record(new ExternalPluginProcessEvent(
                    ExternalPluginProcessEventKind.StartupExited,
                    Manifest.Id,
                    $"External plugin process '{Manifest.Id}' exited during startup.",
                    DateTimeOffset.UtcNow));
            }

            return _process.HasExited
                ? PluginInitializationStatus.Failed
                : PluginInitializationStatus.Initialized;
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                await _process.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (packageIdentity.IsComplete)
                {
                    processRegistry.Unregister(packageIdentity, _process);
                }
                else
                {
                    processRegistry.Unregister(Manifest.Id, _process);
                }
                _process = null;
            }
        }
    }
}
