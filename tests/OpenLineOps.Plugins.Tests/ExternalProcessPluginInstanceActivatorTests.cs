using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class ExternalProcessPluginInstanceActivatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ActivateAsyncReturnsProxyAndStartsProcessOnlyDuringInitialization()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var registry = new ExternalPluginProcessRegistry();
        var activator = new ExternalProcessPluginInstanceActivator(runner, registry);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(result.Plugin);
        Assert.Equal(0, runner.StartCount);
        Assert.False(registry.TryGet(manifest.Id, out _));

        var status = await result.Plugin.InitializeAsync(new EmptyServiceProvider());

        Assert.Equal(PluginInitializationStatus.Initialized, status);
        Assert.Equal(1, runner.StartCount);
        Assert.True(registry.TryGet(manifest.Id, out var registeredProcess));
        Assert.Same(runner.Process, registeredProcess);
        Assert.NotNull(runner.Request);
        Assert.Equal(manifest.Id, runner.Request.Manifest.Id);
        Assert.Equal(packageDirectory.PackagePath, runner.Request.PackagePath);
        Assert.Equal(packageDirectory.ManifestPath, runner.Request.ManifestPath);
        Assert.Equal(packageDirectory.EntryAssemblyPath, runner.Request.EntryAssemblyPath);
        Assert.Equal(manifest.EntryType, runner.Request.EntryType);
        Assert.Equal(manifest.Id, runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_ID"]);
        Assert.Equal(packageDirectory.PackagePath, runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_PACKAGE_PATH"]);
        Assert.Equal(packageDirectory.ManifestPath, runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_MANIFEST_PATH"]);
        Assert.Equal(packageDirectory.EntryAssemblyPath, runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY"]);
        Assert.Equal(manifest.EntryType, runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_ENTRY_TYPE"]);

        await result.Plugin.DisposeAsync();

        Assert.True(runner.Process.Disposed);
        Assert.False(registry.TryGet(manifest.Id, out _));
    }

    [Fact]
    public async Task ActivateAsyncRejectsEntryAssemblyOutsidePackageDirectory()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest(entryAssembly: Path.Combine("..", "Plugin.dll"));
        var runner = new CapturingExternalPluginProcessRunner();
        var activator = new ExternalProcessPluginInstanceActivator(runner);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("outside package directory", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public async Task ActivateAsyncRejectsMissingEntryAssembly()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest(entryAssembly: "Missing.Plugin.dll");
        var runner = new CapturingExternalPluginProcessRunner();
        var activator = new ExternalProcessPluginInstanceActivator(runner);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("was not found", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public async Task ActivateAsyncRejectsUntrustedPackageWhenTrustPolicyIsEnforced()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var events = new CapturingExternalPluginProcessEventSink();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                RequireTrustedPackage = true
            }
        };
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            new ExternalPluginProcessRegistry(),
            options,
            events);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("no entry assembly SHA-256 hash is configured", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
        Assert.Contains(events.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.TrustRejected
            && processEvent.PluginId == manifest.Id);
    }

    [Fact]
    public async Task ActivateAsyncAllowsTrustedPackageAndPassesSandboxEnvironment()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var registry = new ExternalPluginProcessRegistry();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                RequireTrustedPackage = true,
                RequireLeastPrivilegeExecution = true,
                IsolationMode = ExternalPluginIsolationModes.LeastPrivilegeIdentity,
                LeastPrivilegeIdentity = "openlineops-plugin",
                MaxCommandTimeout = TimeSpan.FromSeconds(10)
            }
        };
        var expectedHash = ComputeSha256(packageDirectory.EntryAssemblyPath);
        options.Sandbox.TrustedEntryAssemblySha256[manifest.Id] = expectedHash;
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            registry,
            options);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.True(result.Succeeded, result.FailureReason);

        var status = await result.Plugin!.InitializeAsync(new EmptyServiceProvider());

        Assert.Equal(PluginInitializationStatus.Initialized, status);
        Assert.NotNull(runner.Request);
        Assert.Equal(expectedHash, runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY_SHA256"]);
        Assert.Equal(
            ExternalPluginIsolationModes.LeastPrivilegeIdentity,
            runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_SANDBOX_ISOLATION_MODE"]);
        Assert.Equal("openlineops-plugin", runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_SANDBOX_IDENTITY"]);
        Assert.Equal("10000", runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_SANDBOX_MAX_COMMAND_TIMEOUT_MS"]);

        await result.Plugin.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAsyncRejectsUnsignedPackageWhenSignaturePolicyIsEnforced()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        using var rsa = RSA.Create(2048);
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var events = new CapturingExternalPluginProcessEventSink();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                RequireSignedPackage = true
            }
        };
        options.Sandbox.TrustedPackageSigningPublicKeys["*"] = rsa.ExportSubjectPublicKeyInfoPem();
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            new ExternalPluginProcessRegistry(),
            options,
            events);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("signature file", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("was not found", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
        Assert.Contains(events.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.TrustRejected
            && processEvent.PluginId == manifest.Id);
    }

    [Fact]
    public async Task ActivateAsyncAllowsSignedPackageWithoutConfiguredHash()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        using var rsa = RSA.Create(2048);
        var manifest = CreateManifest();
        var package = packageDirectory.Package(manifest);
        WritePackageSignature(packageDirectory, package, rsa);
        var runner = new CapturingExternalPluginProcessRunner();
        var registry = new ExternalPluginProcessRegistry();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                RequireTrustedPackage = true,
                RequireSignedPackage = true
            }
        };
        options.Sandbox.TrustedPackageSigningPublicKeys[manifest.Id] = rsa.ExportSubjectPublicKeyInfoPem();
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            registry,
            options);

        var result = await activator.ActivateAsync(package);

        Assert.True(result.Succeeded, result.FailureReason);

        var status = await result.Plugin!.InitializeAsync(new EmptyServiceProvider());

        Assert.Equal(PluginInitializationStatus.Initialized, status);
        Assert.NotNull(runner.Request);
        Assert.Equal(
            ComputeSha256(packageDirectory.EntryAssemblyPath),
            runner.Request.EnvironmentVariables["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY_SHA256"]);

        await result.Plugin.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAsyncRejectsSignedPackageWhenEntryAssemblyWasTampered()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        using var rsa = RSA.Create(2048);
        var manifest = CreateManifest();
        var package = packageDirectory.Package(manifest);
        WritePackageSignature(packageDirectory, package, rsa);
        await File.WriteAllTextAsync(packageDirectory.EntryAssemblyPath, "tampered-plugin-assembly");
        var runner = new CapturingExternalPluginProcessRunner();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                RequireSignedPackage = true
            }
        };
        options.Sandbox.TrustedPackageSigningPublicKeys["*"] = rsa.ExportSubjectPublicKeyInfoPem();
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            new ExternalPluginProcessRegistry(),
            options);

        var result = await activator.ActivateAsync(package);

        Assert.False(result.Succeeded);
        Assert.Contains("signature verification failed", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public async Task ActivateAsyncRejectsWhenLeastPrivilegePolicyIsRequiredButNotConfigured()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var events = new CapturingExternalPluginProcessEventSink();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                RequireLeastPrivilegeExecution = true
            }
        };
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            new ExternalPluginProcessRegistry(),
            options,
            events);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("requires least-privilege sandbox execution", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
        Assert.Contains(events.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.SandboxRejected
            && processEvent.PluginId == manifest.Id);
    }

    [Fact]
    public async Task ActivateAsyncRejectsContainerIsolationWithoutContainerImage()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var events = new CapturingExternalPluginProcessEventSink();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.Container
            }
        };
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            new ExternalPluginProcessRegistry(),
            options,
            events);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("no container image is configured", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
        Assert.Contains(events.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.SandboxRejected
            && processEvent.PluginId == manifest.Id);
    }

    [Fact]
    public async Task ActivateAsyncRejectsLeastPrivilegeIdentityIsolationWithoutIdentity()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var events = new CapturingExternalPluginProcessEventSink();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.LeastPrivilegeIdentity,
                LeastPrivilegeLauncherExecutable = "sudo"
            }
        };
        var activator = new ExternalProcessPluginInstanceActivator(
            runner,
            new ExternalPluginProcessRegistry(),
            options,
            events);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));

        Assert.False(result.Succeeded);
        Assert.Contains("no least-privilege identity is configured", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.StartCount);
        Assert.Contains(events.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.SandboxRejected
            && processEvent.PluginId == manifest.Id);
    }

    [Fact]
    public async Task InitializeAsyncReturnsFailedWhenExternalProcessHasExited()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner(
            new FakeExternalPluginProcess(hasExited: true));
        var activator = new ExternalProcessPluginInstanceActivator(runner);

        var result = await activator.ActivateAsync(packageDirectory.Package(manifest));
        var status = await result.Plugin!.InitializeAsync(new EmptyServiceProvider());

        Assert.Equal(PluginInitializationStatus.Failed, status);

        await result.Plugin.DisposeAsync();

        Assert.True(runner.Process.Disposed);
    }

    [Fact]
    public async Task LifecycleManagerCanStartAndStopPluginThroughExternalProcessBoundary()
    {
        using var packageDirectory = TestPluginPackageDirectory.Create();
        var manifest = CreateManifest();
        var runner = new CapturingExternalPluginProcessRunner();
        var registry = new ExternalPluginProcessRegistry();
        var manager = new PluginLifecycleManager(
            new InMemoryPluginPackageCatalog(packageDirectory.Package(manifest)),
            new PluginManifestValidator(),
            new ExternalProcessPluginInstanceActivator(runner, registry));

        var startRecords = await manager.StartAsync(new EmptyServiceProvider());
        var stopRecords = await manager.StopAsync();

        var startRecord = Assert.Single(startRecords);
        Assert.Equal(PluginLifecycleState.Initialized, startRecord.State);
        Assert.Equal(1, runner.StartCount);

        var stopRecord = Assert.Single(stopRecords);
        Assert.Equal(PluginLifecycleState.Stopped, stopRecord.State);
        Assert.True(runner.Process.Disposed);
        Assert.False(registry.TryGet(manifest.Id, out _));
    }

    [Fact]
    public async Task CommandInvokerSendsDeviceCommandToRegisteredExternalProcess()
    {
        var registry = new ExternalPluginProcessRegistry();
        var process = new FakeExternalPluginProcess(
            result: PluginDeviceCommandInvocationResult.Completed("{\"barcode\":\"ABC-123\"}"));
        registry.Register("openlineops.external-process-test-plugin", process);
        var invoker = new ExternalProcessPluginDeviceCommandInvoker(registry);
        var request = CreateInvocationRequest();

        var result = await invoker.ExecuteAsync(request);

        Assert.Equal(PluginDeviceCommandInvocationOutcome.Completed, result.Outcome);
        Assert.Equal("{\"barcode\":\"ABC-123\"}", result.ResultPayload);
        Assert.Same(request, process.CommandRequest);
    }

    [Fact]
    public async Task CommandInvokerRejectsWhenExternalProcessIsNotRegistered()
    {
        var invoker = new ExternalProcessPluginDeviceCommandInvoker(new ExternalPluginProcessRegistry());

        var result = await invoker.ExecuteAsync(CreateInvocationRequest());

        Assert.Equal(PluginDeviceCommandInvocationOutcome.Rejected, result.Outcome);
        Assert.Contains("is not running", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandInvokerFailsWhenExternalProcessHasExited()
    {
        var registry = new ExternalPluginProcessRegistry();
        registry.Register(
            "openlineops.external-process-test-plugin",
            new FakeExternalPluginProcess(hasExited: true));
        var invoker = new ExternalProcessPluginDeviceCommandInvoker(registry);

        var result = await invoker.ExecuteAsync(CreateInvocationRequest());

        Assert.Equal(PluginDeviceCommandInvocationOutcome.Failed, result.Outcome);
        Assert.Contains("has exited", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessCommandInvokerSendsCommandToRegisteredExternalProcess()
    {
        var registry = new ExternalPluginProcessRegistry();
        var process = new FakeExternalPluginProcess(
            processResult: PluginProcessCommandInvocationResult.Completed("{\"inspection\":\"pass\"}"));
        registry.Register("openlineops.external-process-test-plugin", process);
        var invoker = new ExternalProcessPluginProcessCommandInvoker(registry);
        var request = CreateProcessInvocationRequest();

        var result = await invoker.ExecuteAsync(request);

        Assert.Equal(PluginProcessCommandInvocationOutcome.Completed, result.Outcome);
        Assert.Equal("{\"inspection\":\"pass\"}", result.ResultPayload);
        Assert.Same(request, process.ProcessCommandRequest);
    }

    [Fact]
    public async Task ProcessCommandInvokerRejectsWhenExternalProcessIsNotRegistered()
    {
        var invoker = new ExternalProcessPluginProcessCommandInvoker(new ExternalPluginProcessRegistry());

        var result = await invoker.ExecuteAsync(CreateProcessInvocationRequest());

        Assert.Equal(PluginProcessCommandInvocationOutcome.Rejected, result.Outcome);
        Assert.Contains("is not running", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessCommandInvokerFailsWhenExternalProcessHasExited()
    {
        var registry = new ExternalPluginProcessRegistry();
        registry.Register(
            "openlineops.external-process-test-plugin",
            new FakeExternalPluginProcess(hasExited: true));
        var invoker = new ExternalProcessPluginProcessCommandInvoker(registry);

        var result = await invoker.ExecuteAsync(CreateProcessInvocationRequest());

        Assert.Equal(PluginProcessCommandInvocationOutcome.Failed, result.Outcome);
        Assert.Contains("has exited", result.FailureReason, StringComparison.Ordinal);
    }

    private static PluginManifest CreateManifest(string entryAssembly = "Plugin.dll")
    {
        return new PluginManifest(
            "openlineops.external-process-test-plugin",
            "External Process Test Plugin",
            "1.0.0",
            PluginKind.DeviceDriver,
            entryAssembly,
            "External.Process.Test.Plugin",
            ["device.external-process"]);
    }

    private static PluginDeviceCommandInvocationRequest CreateInvocationRequest()
    {
        return new PluginDeviceCommandInvocationRequest(
            "openlineops.external-process-test-plugin",
            "scanner-01",
            "device.scanner:scan",
            "device.scanner",
            "Scan",
            "{\"serial\":\"ABC\"}",
            30000);
    }

    private static PluginProcessCommandInvocationRequest CreateProcessInvocationRequest()
    {
        return new PluginProcessCommandInvocationRequest(
            "openlineops.external-process-test-plugin",
            "00000000-0000-0000-0000-000000000001",
            "station-a",
            "snapshot-20260629-001",
            "00000000-0000-0000-0000-000000000002",
            "00000000-0000-0000-0000-000000000003",
            "node-inspect",
            "process.vision:inspect",
            "process.vision",
            "Inspect",
            "{\"serial\":\"ABC\"}",
            30000);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WritePackageSignature(
        TestPluginPackageDirectory packageDirectory,
        PluginPackageDescriptor package,
        RSA rsa)
    {
        var entryAssemblySha256 = ComputeSha256(packageDirectory.EntryAssemblyPath);
        var manifestSha256 = ComputeSha256(packageDirectory.ManifestPath);
        var payload = ExternalPluginPackageSignaturePayload.Create(
            package,
            packageDirectory.EntryAssemblyPath,
            entryAssemblySha256,
            manifestSha256);
        var signatureBytes = rsa.SignData(
            Encoding.UTF8.GetBytes(payload),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var signature = new ExternalPluginPackageSignature(
            ExternalPluginPackageSignatureAlgorithms.RsaSha256,
            Convert.ToBase64String(signatureBytes),
            "test-key");

        File.WriteAllText(
            packageDirectory.SignaturePath,
            JsonSerializer.Serialize(signature, JsonOptions));
    }

    private sealed class CapturingExternalPluginProcessRunner : IExternalPluginProcessRunner
    {
        public CapturingExternalPluginProcessRunner()
            : this(new FakeExternalPluginProcess())
        {
        }

        public CapturingExternalPluginProcessRunner(FakeExternalPluginProcess process)
        {
            Process = process;
        }

        public FakeExternalPluginProcess Process { get; }

        public ExternalPluginProcessStartRequest? Request { get; private set; }

        public int StartCount { get; private set; }

        public ValueTask<IExternalPluginProcess> StartAsync(
            ExternalPluginProcessStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            Request = request;

            return ValueTask.FromResult<IExternalPluginProcess>(Process);
        }
    }

    private sealed class CapturingExternalPluginProcessEventSink : IExternalPluginProcessEventSink
    {
        public List<ExternalPluginProcessEvent> Events { get; } = [];

        public void Record(ExternalPluginProcessEvent processEvent)
        {
            Events.Add(processEvent);
        }
    }

    private sealed class FakeExternalPluginProcess(
        bool hasExited = false,
        PluginDeviceCommandInvocationResult? result = null,
        PluginProcessCommandInvocationResult? processResult = null) : IExternalPluginProcess
    {
        public bool HasExited { get; } = hasExited;

        public bool Disposed { get; private set; }

        public PluginDeviceCommandInvocationRequest? CommandRequest { get; private set; }

        public PluginProcessCommandInvocationRequest? ProcessCommandRequest { get; private set; }

        public ValueTask<PluginDeviceCommandInvocationResult> ExecuteDeviceCommandAsync(
            PluginDeviceCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandRequest = request;

            return ValueTask.FromResult(
                result ?? PluginDeviceCommandInvocationResult.Completed("{\"ok\":true}"));
        }

        public ValueTask<PluginProcessCommandInvocationResult> ExecuteProcessCommandAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessCommandRequest = request;

            return ValueTask.FromResult(
                processResult ?? PluginProcessCommandInvocationResult.Completed("{\"ok\":true}"));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    private sealed class InMemoryPluginPackageCatalog(
        params PluginPackageDescriptor[] packages) : IPluginPackageCatalog
    {
        public ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginPackageDescriptor>>(packages);
        }
    }

    private sealed class TestPluginPackageDirectory : IDisposable
    {
        private TestPluginPackageDirectory(string packagePath)
        {
            PackagePath = packagePath;
            ManifestPath = Path.Combine(packagePath, "openlineops-plugin.json");
            EntryAssemblyPath = Path.Combine(packagePath, "Plugin.dll");
            SignaturePath = Path.Combine(packagePath, "openlineops-plugin.signature.json");
        }

        public string PackagePath { get; }

        public string ManifestPath { get; }

        public string EntryAssemblyPath { get; }

        public string SignaturePath { get; }

        public static TestPluginPackageDirectory Create()
        {
            var packagePath = Path.Combine(
                Path.GetTempPath(),
                "openlineops-plugin-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packagePath);

            var directory = new TestPluginPackageDirectory(Path.GetFullPath(packagePath));
            File.WriteAllText(directory.EntryAssemblyPath, "not-a-real-assembly");
            File.WriteAllText(directory.ManifestPath, "{}");

            return directory;
        }

        public PluginPackageDescriptor Package(PluginManifest manifest)
        {
            return new PluginPackageDescriptor(
                manifest,
                PackagePath,
                ManifestPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(PackagePath))
            {
                Directory.Delete(PackagePath, recursive: true);
            }
        }
    }
}
