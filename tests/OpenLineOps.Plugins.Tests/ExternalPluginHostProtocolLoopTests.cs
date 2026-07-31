using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class ExternalPluginHostProtocolLoopTests
{
    private static readonly DateTimeOffset SessionTimestamp =
        new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

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

        AssertLegacyResponseFrameShape(output);
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
    public async Task RunAsyncReusesOneLegacyCommandSessionAdapterForTheLoopLifetime()
    {
        var plugin = new HostLoopDeviceCommandPlugin();
        using var output = new StringWriter();
        using var input = new LegacySessionProtocolReader(output);

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var responses = ReadSessionResponses(output);

        Assert.Equal(4, responses.Length);
        Assert.Equal([1L, 2L, 3L, 4L], responses.Select(static response => response.Sequence));
        Assert.Equal("device-session-open-result", responses[0].MessageType);
        Assert.Equal("device-session-invoke-result", responses[1].MessageType);
        Assert.Equal("device-session-invoke-result", responses[2].MessageType);
        Assert.Equal("device-session-close-result", responses[3].MessageType);
        Assert.True(ReadSessionPayload<PluginDeviceOperationResult>(responses[1]).Succeeded);
        Assert.True(ReadSessionPayload<PluginDeviceOperationResult>(responses[2]).Succeeded);
        Assert.Equal(1, plugin.CommandExecutionCount);
        Assert.Equal(0, plugin.DisposeCount);
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

        AssertLegacyResponseFrameShape(output);
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
    public async Task RunAsyncExecutesDeviceSessionOperationsWithMonotonicResponseFrames()
    {
        var plugin = new HostLoopDeviceSessionPlugin();
        var requests = new[]
        {
            SerializeSessionRequest(
                "device-session-open",
                sessionId: null,
                "session-request-01",
                new PluginDeviceSessionOpenRequest("scanner-01", """{"endpoint":"simulator"}""")),
            SerializeSessionRequest(
                "device-signal-read",
                "session-01",
                "session-request-02",
                new PluginDeviceSignalReadRequest("session-01", ["temperature"])),
            SerializeSessionRequest(
                "device-signal-write",
                "session-01",
                "session-request-03",
                new PluginDeviceSignalWriteRequest(
                    "session-01",
                    CreateSessionCommand("write-01"),
                    [
                        new PluginDeviceSignalWrite(
                            "setpoint",
                            PluginDeviceValue.FromDouble(24.5),
                            "degC")
                    ])),
            SerializeSessionRequest(
                "device-session-invoke",
                "session-01",
                "session-request-04",
                new PluginDeviceInvocationRequest(
                    "session-01",
                    CreateSessionCommand("invoke-01"),
                    "Calibrate",
                    """{"channel":1}""")),
            SerializeSessionRequest(
                "device-session-health",
                "session-01",
                "session-request-05",
                new PluginDeviceSessionRequest("session-01")),
            SerializeSessionRequest(
                "device-session-diagnostics",
                "session-01",
                "session-request-06",
                new PluginDeviceSessionRequest("session-01")),
            SerializeSessionRequest<object?>(
                "device-session-heartbeat",
                "session-01",
                "session-request-07",
                payload: null),
            SerializeSessionRequest(
                "device-session-close",
                "session-01",
                "session-request-08",
                new PluginDeviceSessionCloseRequest("session-01", "Completed."))
        };
        using var input = new StringReader(string.Join(Environment.NewLine, requests));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var responses = ReadSessionResponses(output);

        Assert.Equal(8, responses.Length);
        Assert.Equal(
            Enumerable.Range(1, 8).Select(static value => (long)value),
            responses.Select(static response => response.Sequence));
        Assert.Equal(
            [
                "device-session-open-result",
                "device-signal-read-result",
                "device-signal-write-result",
                "device-session-invoke-result",
                "device-session-health-result",
                "device-session-diagnostics-result",
                "device-session-heartbeat-result",
                "device-session-close-result"
            ],
            responses.Select(static response => response.MessageType));
        Assert.All(responses, response => Assert.Null(response.Error));
        Assert.All(responses, response => Assert.Equal("session-01", response.SessionId));
        Assert.Equal(
            Enumerable.Range(1, 8).Select(static value => $"session-request-{value:D2}"),
            responses.Select(static response => response.RequestId));

        var opened = ReadSessionPayload<PluginDeviceSession>(responses[0]);
        Assert.Equal("scanner-01", opened.DeviceInstanceId);
        var samples = ReadSessionPayload<PluginDeviceSignalSample[]>(responses[1]);
        Assert.Equal("temperature", Assert.Single(samples).SignalId);
        Assert.True(ReadSessionPayload<PluginDeviceOperationResult>(responses[2]).Succeeded);
        Assert.True(ReadSessionPayload<PluginDeviceOperationResult>(responses[3]).Succeeded);
        Assert.Equal(
            PluginDeviceHealthStatus.Healthy,
            ReadSessionPayload<PluginDeviceHealthSnapshot>(responses[4]).Status);
        Assert.Equal(
            "Transport.Ready",
            Assert.Single(ReadSessionPayload<DiagnosticsPayload>(responses[5]).Entries).Code);
        Assert.Equal(
            TimeSpan.Zero,
            ReadSessionPayload<HeartbeatPayload>(responses[6]).ObservedAtUtc.Offset);
        Assert.True(ReadSessionPayload<AcknowledgementPayload>(responses[7]).Accepted);

        Assert.NotNull(plugin.OpenRequest);
        Assert.NotNull(plugin.ReadRequest);
        Assert.NotNull(plugin.WriteRequest);
        Assert.NotNull(plugin.InvocationRequest);
        Assert.NotNull(plugin.HealthRequest);
        Assert.NotNull(plugin.DiagnosticsRequest);
        Assert.NotNull(plugin.CloseRequest);
    }

    [Fact]
    public async Task RunAsyncStreamsSubscriptionEventsAndCompletionWithMonotonicFrames()
    {
        var plugin = new HostLoopDeviceSessionPlugin();
        var request = SerializeSessionRequest(
            "device-signal-subscribe",
            "session-01",
            "subscription-request-01",
            new PluginDeviceSignalSubscriptionRequest(
                "session-01",
                "subscription-01",
                ["temperature"],
                resumeAfterSequence: 40));
        using var input = new StringReader(request);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var responses = ReadSessionResponses(output);

        Assert.Equal(4, responses.Length);
        Assert.Equal([1L, 2L, 3L, 4L], responses.Select(static response => response.Sequence));
        Assert.Equal("device-signal-subscribe-result", responses[0].MessageType);
        Assert.Equal("device-signal-subscription-event", responses[1].MessageType);
        Assert.Equal("device-signal-subscription-event", responses[2].MessageType);
        Assert.Equal("device-signal-subscription-completed", responses[3].MessageType);
        Assert.All(responses, response => Assert.Equal("session-01", response.SessionId));
        Assert.All(responses, response => Assert.Equal("subscription-request-01", response.RequestId));
        Assert.All(responses, response => Assert.Null(response.Error));
        Assert.Equal(
            "subscription-01",
            ReadSessionPayload<SubscriptionAcceptedPayload>(responses[0]).SubscriptionId);
        Assert.Equal(
            [41L, 42L],
            responses
                .Skip(1)
                .Take(2)
                .Select(response =>
                    ReadSessionPayload<PluginDeviceSignalSubscriptionEvent>(response).Sequence));
        Assert.Equal(
            "completed",
            ReadSessionPayload<SubscriptionCompletedPayload>(responses[3]).Reason);
    }

    [Fact]
    public async Task RunAsyncWritesSubscriptionErrorFrameWhenPluginStreamFails()
    {
        var plugin = new HostLoopDeviceSessionPlugin
        {
            SubscriptionBehavior = HostLoopSubscriptionBehavior.FailAfterFirstEvent
        };
        var request = SerializeSessionRequest(
            "device-signal-subscribe",
            "session-01",
            "subscription-request-02",
            new PluginDeviceSignalSubscriptionRequest(
                "session-01",
                "subscription-02",
                ["temperature"]));
        using var input = new StringReader(request);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var responses = ReadSessionResponses(output);

        Assert.Equal(3, responses.Length);
        Assert.Equal([1L, 2L, 3L], responses.Select(static response => response.Sequence));
        Assert.Equal("device-signal-subscription-error", responses[2].MessageType);
        Assert.Contains("stream failed", responses[2].Error, StringComparison.Ordinal);
        Assert.Equal(
            "subscription-02",
            ReadSessionPayload<SubscriptionFailedPayload>(responses[2]).SubscriptionId);
    }

    [Fact]
    public async Task RunAsyncClosesSessionAndCancelsItsActiveSubscriptions()
    {
        var plugin = new HostLoopDeviceSessionPlugin
        {
            SubscriptionBehavior = HostLoopSubscriptionBehavior.WaitForCancellation
        };
        var requests = new[]
        {
            SerializeSessionRequest(
                "device-signal-subscribe",
                "session-01",
                "subscription-request-03",
                new PluginDeviceSignalSubscriptionRequest(
                    "session-01",
                    "subscription-03",
                    ["temperature"])),
            SerializeSessionRequest(
                "device-session-close",
                "session-01",
                "session-request-close",
                new PluginDeviceSessionCloseRequest("session-01"))
        };
        using var input = new StringReader(string.Join(Environment.NewLine, requests));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var responses = ReadSessionResponses(output);

        Assert.Equal(3, responses.Length);
        Assert.Equal([1L, 2L, 3L], responses.Select(static response => response.Sequence));
        Assert.Equal("device-signal-subscribe-result", responses[0].MessageType);
        Assert.Equal("device-session-close-result", responses[1].MessageType);
        Assert.Equal("device-signal-subscription-completed", responses[2].MessageType);
        Assert.Equal(
            "canceled",
            ReadSessionPayload<SubscriptionCompletedPayload>(responses[2]).Reason);
        Assert.NotNull(plugin.CloseRequest);
    }

    [Fact]
    public async Task RunAsyncRejectsDuplicateActiveSubscriptionIdentity()
    {
        var plugin = new HostLoopDeviceSessionPlugin
        {
            SubscriptionBehavior = HostLoopSubscriptionBehavior.WaitForCancellation
        };
        var payload = new PluginDeviceSignalSubscriptionRequest(
            "session-01",
            "subscription-duplicate",
            ["temperature"]);
        var requests = new[]
        {
            SerializeSessionRequest(
                "device-signal-subscribe",
                "session-01",
                "subscription-request-first",
                payload),
            SerializeSessionRequest(
                "device-signal-subscribe",
                "session-01",
                "subscription-request-second",
                payload)
        };
        using var input = new StringReader(string.Join(Environment.NewLine, requests));
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var responses = ReadSessionResponses(output);

        Assert.Equal(3, responses.Length);
        Assert.Equal([1L, 2L, 3L], responses.Select(static response => response.Sequence));
        Assert.Equal("device-signal-subscribe-result", responses[0].MessageType);
        Assert.Equal("device-signal-subscribe-result", responses[1].MessageType);
        Assert.Contains("already active", responses[1].Error, StringComparison.Ordinal);
        Assert.Equal(
            "device-signal-subscription-completed",
            responses[2].MessageType);
        Assert.Equal(1, plugin.SubscriptionInvocationCount);
    }

    [Fact]
    public async Task RunAsyncReturnsCorrelatedSessionErrorForUnsupportedPlugin()
    {
        var unsupported = SerializeSessionRequest(
            "device-session-open",
            sessionId: null,
            "unsupported-request",
            new PluginDeviceSessionOpenRequest("scanner-01"));
        using var input = new StringReader(unsupported);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(
            new HostLoopLifecycleOnlyPlugin(),
            input,
            output);

        var responses = ReadSessionResponses(output);

        var response = Assert.Single(responses);
        Assert.Equal(1, response.Sequence);
        Assert.Contains(
            nameof(IOpenLineOpsDeviceSessionPlugin),
            response.Error,
            StringComparison.Ordinal);
        Assert.Equal("unsupported-request", response.RequestId);
    }

    [Fact]
    public async Task RunAsyncReturnsCorrelatedSessionErrorForInvalidPayload()
    {
        var invalid = SerializeSessionRequest<object?>(
            "device-signal-read",
            "session-01",
            "invalid-request",
            payload: null);
        using var input = new StringReader(invalid);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(
            new HostLoopDeviceSessionPlugin(),
            input,
            output);

        var response = Assert.Single(ReadSessionResponses(output));

        Assert.Equal(1, response.Sequence);
        Assert.Contains("payload is required", response.Error, StringComparison.Ordinal);
        Assert.Equal("session-01", response.SessionId);
        Assert.Equal("invalid-request", response.RequestId);
    }

    [Fact]
    public async Task RunAsyncRejectsMismatchedSessionCorrelationWithoutInvokingPlugin()
    {
        var plugin = new HostLoopDeviceSessionPlugin();
        var request = SerializeSessionRequest(
            "device-signal-read",
            "session-envelope",
            "mismatch-request",
            new PluginDeviceSignalReadRequest("session-payload", ["temperature"]));
        using var input = new StringReader(request);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = Assert.Single(ReadSessionResponses(output));

        Assert.Equal("session-envelope", response.SessionId);
        Assert.Equal("mismatch-request", response.RequestId);
        Assert.Equal(1, response.Sequence);
        Assert.Contains("does not match", response.Error, StringComparison.Ordinal);
        Assert.Null(plugin.ReadRequest);
    }

    [Fact]
    public async Task RunAsyncAnswersHeartbeatWithoutRequiringSessionPluginContract()
    {
        var request = SerializeSessionRequest<object?>(
            "device-session-heartbeat",
            "session-01",
            "heartbeat-request",
            payload: null);
        using var input = new StringReader(request);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(
            new HostLoopLifecycleOnlyPlugin(),
            input,
            output);

        var response = Assert.Single(ReadSessionResponses(output));

        Assert.Equal("device-session-heartbeat-result", response.MessageType);
        Assert.Equal("session-01", response.SessionId);
        Assert.Equal("heartbeat-request", response.RequestId);
        Assert.Equal(1, response.Sequence);
        Assert.Null(response.Error);
        Assert.Equal(
            TimeSpan.Zero,
            ReadSessionPayload<HeartbeatPayload>(response).ObservedAtUtc.Offset);
    }

    [Fact]
    public async Task RunAsyncConvertsPluginSessionExceptionToCorrelatedErrorFrame()
    {
        var plugin = new HostLoopDeviceSessionPlugin
        {
            ThrowOnInvoke = true
        };
        var request = SerializeSessionRequest(
            "device-session-invoke",
            "session-01",
            "invoke-failure-request",
            new PluginDeviceInvocationRequest(
                "session-01",
                CreateSessionCommand("invoke-failure"),
                "Fail"));
        using var input = new StringReader(request);
        using var output = new StringWriter();

        await ExternalPluginHostProtocolLoop.RunAsync(plugin, input, output);

        var response = Assert.Single(ReadSessionResponses(output));

        Assert.Equal("device-session-invoke-result", response.MessageType);
        Assert.Equal("session-01", response.SessionId);
        Assert.Equal("invoke-failure-request", response.RequestId);
        Assert.Equal(1, response.Sequence);
        Assert.Contains("invocation failed", response.Error, StringComparison.Ordinal);
        Assert.Null(response.Payload);
    }

    [Fact]
    public async Task RunAsyncPropagatesHostCancellationDuringSessionOperation()
    {
        var plugin = new HostLoopDeviceSessionPlugin
        {
            WaitForInvokeCancellation = true
        };
        var request = SerializeSessionRequest(
            "device-session-invoke",
            "session-01",
            "invoke-cancel-request",
            new PluginDeviceInvocationRequest(
                "session-01",
                CreateSessionCommand("invoke-cancel"),
                "Wait"));
        using var input = new StringReader(request);
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();

        var run = ExternalPluginHostProtocolLoop
            .RunAsync(plugin, input, output, cancellation.Token)
            .AsTask();
        await plugin.InvokeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(output.ToString());
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
            30000,
            ExecutionIdentity(HostLoopDeviceCommandPlugin.ManifestId));
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
            30000,
            ExecutionIdentity(HostLoopProcessNodePlugin.ManifestId));
    }

    private static PluginPackageExecutionIdentity ExecutionIdentity(string pluginId) => new(
        "project.test",
        "application.test",
        new PluginPackageRuntimeIdentity(
            pluginId,
            "1.0.0",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "1.0.0",
            "win-x64",
            "openlineops.plugin-abi/1"));

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

    private static void AssertLegacyResponseFrameShape(StringWriter output)
    {
        var line = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single();
        using var document = JsonDocument.Parse(line);

        Assert.Equal(
            ["messageType", "requestId", "payload", "error"],
            document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name));
        Assert.False(document.RootElement.TryGetProperty("sessionId", out _));
        Assert.False(document.RootElement.TryGetProperty("sequence", out _));
    }

    private static string SerializeSessionRequest<TPayload>(
        string messageType,
        string? sessionId,
        string requestId,
        TPayload payload)
    {
        return JsonSerializer.Serialize(
            new SessionProtocolRequest<TPayload>(
                messageType,
                sessionId,
                requestId,
                payload),
            JsonOptions);
    }

    private static SessionProtocolResponse[] ReadSessionResponses(StringWriter output)
    {
        return output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
                JsonSerializer.Deserialize<SessionProtocolResponse>(line, JsonOptions)
                ?? throw new InvalidOperationException("Session protocol response was empty."))
            .ToArray();
    }

    private static TPayload ReadSessionPayload<TPayload>(SessionProtocolResponse response)
        where TPayload : class
    {
        if (response.Payload is not { } payload
            || payload.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Session protocol response '{response.MessageType}' had no payload.");
        }

        return payload.Deserialize<TPayload>(JsonOptions)
               ?? throw new InvalidOperationException(
                   $"Session protocol response '{response.MessageType}' payload was empty.");
    }

    private static PluginDeviceCommandEnvelope CreateSessionCommand(string commandId)
    {
        return new PluginDeviceCommandEnvelope(
            commandId,
            42,
            SessionTimestamp.AddMinutes(1),
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Normal);
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

    private sealed record SessionProtocolRequest<TPayload>(
        string MessageType,
        string? SessionId,
        string RequestId,
        TPayload Payload);

    private sealed record SessionProtocolResponse(
        string MessageType,
        string? SessionId,
        string RequestId,
        long Sequence,
        JsonElement? Payload,
        string? Error);

    private sealed record AcknowledgementPayload(bool Accepted);

    private sealed record HeartbeatPayload(DateTimeOffset ObservedAtUtc);

    private sealed record DiagnosticsPayload(
        string SessionId,
        DateTimeOffset CapturedAtUtc,
        DiagnosticEntryPayload[] Entries);

    private sealed record DiagnosticEntryPayload(string Code);

    private sealed record SubscriptionAcceptedPayload(string SubscriptionId);

    private sealed record SubscriptionCompletedPayload(
        string SubscriptionId,
        string Reason);

    private sealed record SubscriptionFailedPayload(string SubscriptionId);

    private sealed class LegacySessionProtocolReader(StringWriter output) : TextReader
    {
        private string? _invokeRequest;
        private int _readIndex;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = _readIndex switch
            {
                0 => SerializeSessionRequest(
                    "device-session-open",
                    sessionId: null,
                    "legacy-open-request",
                    new PluginDeviceSessionOpenRequest("scanner-legacy")),
                1 => CreateInvokeRequest(),
                2 => _invokeRequest
                     ?? throw new InvalidOperationException("Legacy invocation request was not created."),
                3 => CreateCloseRequest(),
                _ => null
            };
            _readIndex++;

            return ValueTask.FromResult(line);
        }

        private string CreateInvokeRequest()
        {
            var sessionId = OpenedSessionId();
            _invokeRequest = SerializeSessionRequest(
                "device-session-invoke",
                sessionId,
                "legacy-invoke-request",
                new PluginDeviceInvocationRequest(
                    sessionId,
                    new PluginDeviceCommandEnvelope(
                        "legacy-command-01",
                        7,
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        PluginDeviceCommandIdempotencyClass.NonIdempotent,
                        PluginDeviceCommandSafetyClass.Normal),
                    "Scan",
                    """{"serial":"ABC"}"""));
            return _invokeRequest;
        }

        private string CreateCloseRequest()
        {
            var sessionId = OpenedSessionId();
            return SerializeSessionRequest(
                "device-session-close",
                sessionId,
                "legacy-close-request",
                new PluginDeviceSessionCloseRequest(sessionId));
        }

        private string OpenedSessionId()
        {
            var openResponse = ReadSessionResponses(output).First();
            return ReadSessionPayload<PluginDeviceSession>(openResponse).SessionId;
        }
    }

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
        ["device.scanner"],
        DeviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.scanner:scan",
                "device.scanner",
                "Scan")
        ]);

    public PluginDeviceCommandExecutionRequest? CommandRequest { get; private set; }

    public int CommandExecutionCount { get; private set; }

    public int DisposeCount { get; private set; }

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
        CommandExecutionCount++;

        return ValueTask.FromResult(PluginDeviceCommandExecutionResult.Completed("{\"barcode\":\"ABC-123\"}"));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
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

public enum HostLoopSubscriptionBehavior
{
    Complete,
    FailAfterFirstEvent,
    WaitForCancellation
}

public sealed class HostLoopDeviceSessionPlugin : IOpenLineOpsDeviceSessionPlugin
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

    public PluginManifest Manifest { get; } = new(
        "openlineops.host-loop-session-plugin",
        "Host Loop Device Session Plugin",
        "1.0.0",
        PluginKind.DeviceDriver,
        "OpenLineOps.Plugins.Tests.dll",
        typeof(HostLoopDeviceSessionPlugin).FullName!,
        ["device.scanner"]);

    public HostLoopSubscriptionBehavior SubscriptionBehavior { get; init; }

    public bool ThrowOnInvoke { get; init; }

    public bool WaitForInvokeCancellation { get; init; }

    public TaskCompletionSource<bool> InvokeEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PluginDeviceSessionOpenRequest? OpenRequest { get; private set; }

    public PluginDeviceSessionCloseRequest? CloseRequest { get; private set; }

    public PluginDeviceSignalReadRequest? ReadRequest { get; private set; }

    public PluginDeviceSignalWriteRequest? WriteRequest { get; private set; }

    public PluginDeviceInvocationRequest? InvocationRequest { get; private set; }

    public PluginDeviceSessionRequest? HealthRequest { get; private set; }

    public PluginDeviceSessionRequest? DiagnosticsRequest { get; private set; }

    public PluginDeviceSignalSubscriptionRequest? SubscriptionRequest { get; private set; }

    public int SubscriptionInvocationCount { get; private set; }

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask<PluginDeviceSession> OpenAsync(
        PluginDeviceSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenRequest = request;

        return ValueTask.FromResult(new PluginDeviceSession(
            "session-01",
            request.DeviceInstanceId,
            Timestamp,
            TimeSpan.FromSeconds(1)));
    }

    public ValueTask CloseAsync(
        PluginDeviceSessionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseRequest = request;

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadRequest = request;

        return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceSignalSample>>(
            [CreateSample(sequence: 10)]);
    }

    public ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteRequest = request;

        return ValueTask.FromResult(PluginDeviceOperationResult.Completed(
            Timestamp,
            """{"written":true}"""));
    }

    public async IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SubscriptionRequest = request;
        SubscriptionInvocationCount++;

        if (SubscriptionBehavior == HostLoopSubscriptionBehavior.WaitForCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new PluginDeviceSignalSubscriptionEvent(
            request.SubscriptionId,
            41,
            CreateSample(sequence: 101));

        if (SubscriptionBehavior == HostLoopSubscriptionBehavior.FailAfterFirstEvent)
        {
            throw new InvalidOperationException("Subscription stream failed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new PluginDeviceSignalSubscriptionEvent(
            request.SubscriptionId,
            42,
            CreateSample(sequence: 102));
    }

    public async ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        InvocationRequest = request;
        InvokeEntered.TrySetResult(true);

        if (WaitForInvokeCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnInvoke)
        {
            throw new InvalidOperationException("Device invocation failed.");
        }

        return PluginDeviceOperationResult.Completed(
            Timestamp,
            """{"invoked":true}""");
    }

    public ValueTask<PluginDeviceHealthSnapshot> GetHealthAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HealthRequest = request;

        return ValueTask.FromResult(new PluginDeviceHealthSnapshot(
            request.SessionId,
            PluginDeviceHealthStatus.Healthy,
            Timestamp,
            Timestamp.AddMilliseconds(-100),
            "Ready."));
    }

    public ValueTask<PluginDeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticsRequest = request;

        return ValueTask.FromResult(new PluginDeviceDiagnosticsSnapshot(
            request.SessionId,
            Timestamp,
            [
                new PluginDeviceDiagnosticEntry(
                    "Transport.Ready",
                    PluginDeviceDiagnosticSeverity.Information,
                    "Transport is ready.",
                    Timestamp)
            ]));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static PluginDeviceSignalSample CreateSample(long sequence)
    {
        return new PluginDeviceSignalSample(
            "temperature",
            PluginDeviceValue.FromDouble(22.5),
            "degC",
            PluginDeviceSignalQuality.Good,
            Timestamp,
            Timestamp.AddMilliseconds(1),
            sequence,
            "Simulator.Good");
    }
}
