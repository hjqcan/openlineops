using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class ExternalPluginHostProtocolLoopTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task RunAsyncExecutesDeviceCommandAndWritesProtocolResponse()
    {
        var plugin = new HostLoopDeviceCommandPlugin();
        var request = CreateProtocolRequest();
        using var input = new StringReader(JsonSerializer.Serialize(request, JsonOptions));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = ReadSingleResponse(output);

        Assert.Equal("device-command-result", response.MessageType);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Null(response.Error);
        Assert.NotNull(response.Payload);
        Assert.Equal(PluginDeviceCommandInvocationOutcome.Completed, response.Payload.Outcome);
        Assert.Equal("{\"barcode\":\"ABC-123\"}", response.Payload.ResultPayload);
        Assert.NotNull(plugin.CommandRequest);
        Assert.Equal("scanner-01", plugin.CommandRequest.DeviceInstanceId);
        Assert.Equal("device.scanner:scan", plugin.CommandRequest.CommandDefinitionId);
        Assert.Equal("device.scanner", plugin.CommandRequest.Capability);
        Assert.Equal("Scan", plugin.CommandRequest.CommandName);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.CommandRequest.Timeout);
    }

    [Fact]
    public async Task RunAsyncRejectsDeviceCommandWhenPluginDoesNotImplementDeviceCommandContract()
    {
        var plugin = new HostLoopLifecycleOnlyPlugin();
        var request = CreateProtocolRequest();
        using var input = new StringReader(JsonSerializer.Serialize(request, JsonOptions));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = ReadSingleResponse(output);

        Assert.Null(response.Error);
        Assert.NotNull(response.Payload);
        Assert.Equal(PluginDeviceCommandInvocationOutcome.Rejected, response.Payload.Outcome);
        Assert.Contains(nameof(IOpenLineOpsDeviceCommandPlugin), response.Payload.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncExecutesProcessCommandAndWritesProtocolResponse()
    {
        var plugin = new HostLoopProcessNodePlugin();
        var request = CreateProcessProtocolRequest();
        using var input = new StringReader(JsonSerializer.Serialize(request, JsonOptions));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = ReadSingleProcessResponse(output);

        Assert.Equal("process-command-result", response.MessageType);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Null(response.Error);
        Assert.NotNull(response.Payload);
        Assert.Equal(PluginProcessCommandInvocationOutcome.Completed, response.Payload.Outcome);
        Assert.Equal("{\"inspection\":\"pass\"}", response.Payload.ResultPayload);
        Assert.NotNull(plugin.CommandRequest);
        Assert.Equal("station-a", plugin.CommandRequest.StationId);
        Assert.Equal("snapshot-20260629-001", plugin.CommandRequest.ConfigurationSnapshotId);
        Assert.Equal("node-inspect", plugin.CommandRequest.NodeId);
        Assert.Equal("process.vision:inspect", plugin.CommandRequest.CommandDefinitionId);
        Assert.Equal("process.vision", plugin.CommandRequest.Capability);
        Assert.Equal("Inspect", plugin.CommandRequest.CommandName);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.CommandRequest.Timeout);
    }

    [Fact]
    public async Task RunAsyncRejectsProcessCommandWhenPluginDoesNotImplementProcessNodeContract()
    {
        var plugin = new HostLoopLifecycleOnlyPlugin();
        var request = CreateProcessProtocolRequest();
        using var input = new StringReader(JsonSerializer.Serialize(request, JsonOptions));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = ReadSingleProcessResponse(output);

        Assert.Null(response.Error);
        Assert.NotNull(response.Payload);
        Assert.Equal(PluginProcessCommandInvocationOutcome.Rejected, response.Payload.Outcome);
        Assert.Contains(nameof(IOpenLineOpsProcessNodePlugin), response.Payload.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncReturnsProtocolErrorForUnsupportedMessageType()
    {
        var plugin = new HostLoopDeviceCommandPlugin();
        var request = new ProtocolRequest(
            "unknown",
            "request-001",
            CreateInvocationRequest());
        using var input = new StringReader(JsonSerializer.Serialize(request, JsonOptions));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = ReadSingleResponse(output);

        Assert.Equal("device-command-result", response.MessageType);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.NotNull(response.Error);
        Assert.Contains("not supported", response.Error, StringComparison.Ordinal);
        Assert.Null(response.Payload);
    }

    [Fact]
    public async Task LoaderConstructsPluginFromManifestAndEntryAssembly()
    {
        using var package = HostPluginTestPackage.Create<HostLoopDeviceCommandPlugin>(
            HostLoopDeviceCommandPlugin.ManifestId);

        var plugin = await new ExternalPluginHostPluginLoader()
            .LoadAsync(new ExternalPluginHostLoadRequest(package.ManifestPath));

        await using (plugin.ConfigureAwait(false))
        {
            Assert.Equal(HostLoopDeviceCommandPlugin.ManifestId, plugin.Manifest.Id);
            Assert.IsAssignableFrom<IOpenLineOpsDeviceCommandPlugin>(plugin);
        }
    }

    [Fact]
    public async Task LoaderRejectsEntryAssemblyOutsidePackageDirectory()
    {
        using var package = HostPluginTestPackage.Create<HostLoopDeviceCommandPlugin>(
            HostLoopDeviceCommandPlugin.ManifestId);
        var outsideAssemblyPath = Path.Combine(
            Directory.GetParent(package.PackagePath)?.FullName
                ?? throw new InvalidOperationException("Package path has no parent."),
            Path.GetFileName(package.EntryAssemblyPath));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new ExternalPluginHostPluginLoader()
                .LoadAsync(new ExternalPluginHostLoadRequest(package.ManifestPath, outsideAssemblyPath));
        });

        Assert.Contains("outside package directory", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("property-case")]
    [InlineData("unknown-property")]
    public async Task LoaderRejectsNonCanonicalManifestJson(string scenario)
    {
        using var package = HostPluginTestPackage.Create<HostLoopDeviceCommandPlugin>(
            HostLoopDeviceCommandPlugin.ManifestId);
        package.RewriteManifest(json => scenario switch
        {
            "property-case" => json.Replace("\"id\":", "\"Id\":", StringComparison.Ordinal),
            "unknown-property" => json.Replace(
                "\"capabilities\":[\"device.scanner\"]",
                "\"capabilities\":[\"device.scanner\"],\"legacy\":true",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Unknown scenario {scenario}.")
        });

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            _ = await new ExternalPluginHostPluginLoader()
                .LoadAsync(new ExternalPluginHostLoadRequest(package.ManifestPath));
        });
    }

    [Fact]
    public async Task LoaderRejectsBackslashEntryAssemblyAlias()
    {
        using var package = HostPluginTestPackage.Create<HostLoopDeviceCommandPlugin>(
            HostLoopDeviceCommandPlugin.ManifestId);
        var entryAssembly = Path.GetFileName(package.EntryAssemblyPath);
        package.RewriteManifest(json => json.Replace(
            $"\"entryAssembly\":\"{entryAssembly}\"",
            $"\"entryAssembly\":\"bin\\\\{entryAssembly}\"",
            StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await new ExternalPluginHostPluginLoader()
                .LoadAsync(new ExternalPluginHostLoadRequest(package.ManifestPath));
        });
    }

    private static ProtocolRequest CreateProtocolRequest()
    {
        return new ProtocolRequest(
            "device-command",
            "request-001",
            CreateInvocationRequest());
    }

    private static ProcessProtocolRequest CreateProcessProtocolRequest()
    {
        return new ProcessProtocolRequest(
            "process-command",
            "request-002",
            CreateProcessInvocationRequest());
    }

    private static PluginDeviceCommandInvocationRequest CreateInvocationRequest()
    {
        return new PluginDeviceCommandInvocationRequest(
            HostLoopDeviceCommandPlugin.ManifestId,
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
            HostLoopProcessNodePlugin.ManifestId,
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

    private static ProtocolResponse ReadSingleResponse(StringWriter output)
    {
        var line = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single();

        return JsonSerializer.Deserialize<ProtocolResponse>(line, JsonOptions)
            ?? throw new InvalidOperationException("Protocol response was empty.");
    }

    private static ProcessProtocolResponse ReadSingleProcessResponse(StringWriter output)
    {
        var line = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single();

        return JsonSerializer.Deserialize<ProcessProtocolResponse>(line, JsonOptions)
            ?? throw new InvalidOperationException("Protocol response was empty.");
    }

    private sealed record ProtocolRequest(
        string MessageType,
        string RequestId,
        PluginDeviceCommandInvocationRequest Payload);

    private sealed record ProtocolResponse(
        string MessageType,
        string RequestId,
        PluginDeviceCommandInvocationResult? Payload,
        string? Error);

    private sealed record ProcessProtocolRequest(
        string MessageType,
        string RequestId,
        PluginProcessCommandInvocationRequest Payload);

    private sealed record ProcessProtocolResponse(
        string MessageType,
        string RequestId,
        PluginProcessCommandInvocationResult? Payload,
        string? Error);

    private sealed class HostPluginTestPackage : IDisposable
    {
        private HostPluginTestPackage(
            string packagePath,
            string manifestPath,
            string entryAssemblyPath)
        {
            PackagePath = packagePath;
            ManifestPath = manifestPath;
            EntryAssemblyPath = entryAssemblyPath;
        }

        public string PackagePath { get; }

        public string ManifestPath { get; }

        public string EntryAssemblyPath { get; }

        public void RewriteManifest(Func<string, string> rewrite)
        {
            File.WriteAllText(ManifestPath, rewrite(File.ReadAllText(ManifestPath)));
        }

        public static HostPluginTestPackage Create<TPlugin>(string manifestId)
            where TPlugin : IOpenLineOpsPlugin
        {
            var packagePath = Path.Combine(
                Path.GetTempPath(),
                "openlineops-plugin-host-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packagePath);

            var entryAssemblyPath = Path.Combine(packagePath, Path.GetFileName(TestAssemblyPath));
            File.Copy(TestAssemblyPath, entryAssemblyPath);

            var manifestPath = Path.Combine(packagePath, "manifest.json");
            var manifest = new PluginManifest(
                manifestId,
                "Host Loop Test Plugin",
                "1.0.0",
                PluginKind.DeviceDriver,
                Path.GetFileName(entryAssemblyPath),
                typeof(TPlugin).FullName ?? throw new InvalidOperationException("Plugin type has no full name."),
                ["device.scanner"],
                DeviceCommands:
                [
                    new PluginDeviceCommandDefinition(
                        "device.scanner:scan",
                        "device.scanner",
                        "Scan")
                ]);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));

            return new HostPluginTestPackage(
                Path.GetFullPath(packagePath),
                Path.GetFullPath(manifestPath),
                Path.GetFullPath(entryAssemblyPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(PackagePath))
            {
                Directory.Delete(PackagePath, recursive: true);
            }
        }

        private static string TestAssemblyPath => typeof(ExternalPluginHostProtocolLoopTests).Assembly.Location;
    }
}

public sealed class HostLoopDeviceCommandPlugin : IOpenLineOpsDeviceCommandPlugin
{
    public const string ManifestId = "openlineops.host-loop-device-plugin";

    public PluginManifest Manifest { get; } = new(
        ManifestId,
        "Host Loop Device Plugin",
        "1.0.0",
        PluginKind.DeviceDriver,
        "OpenLineOps.Plugins.Tests.dll",
        typeof(HostLoopDeviceCommandPlugin).FullName!,
        ["device.scanner"]);

    public PluginDeviceCommandExecutionRequest? CommandRequest { get; private set; }

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask<PluginDeviceCommandExecutionResult> ExecuteDeviceCommandAsync(
        PluginDeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandRequest = request;

        return ValueTask.FromResult(PluginDeviceCommandExecutionResult.Completed("{\"barcode\":\"ABC-123\"}"));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public sealed class HostLoopLifecycleOnlyPlugin : IOpenLineOpsPlugin
{
    public PluginManifest Manifest { get; } = new(
        "openlineops.host-loop-lifecycle-only-plugin",
        "Host Loop Lifecycle Only Plugin",
        "1.0.0",
        PluginKind.DeviceDriver,
        "OpenLineOps.Plugins.Tests.dll",
        typeof(HostLoopLifecycleOnlyPlugin).FullName!,
        ["device.scanner"]);

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public sealed class HostLoopProcessNodePlugin : IOpenLineOpsProcessNodePlugin
{
    public const string ManifestId = "openlineops.host-loop-process-plugin";

    public PluginManifest Manifest { get; } = new(
        ManifestId,
        "Host Loop Process Plugin",
        "1.0.0",
        PluginKind.ProcessNode,
        "OpenLineOps.Plugins.Tests.dll",
        typeof(HostLoopProcessNodePlugin).FullName!,
        ["process.vision"]);

    public PluginProcessCommandExecutionRequest? CommandRequest { get; private set; }

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask<PluginProcessCommandExecutionResult> ExecuteProcessCommandAsync(
        PluginProcessCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandRequest = request;

        return ValueTask.FromResult(PluginProcessCommandExecutionResult.Completed("{\"inspection\":\"pass\"}"));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
