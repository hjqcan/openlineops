using System.Reflection;
using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginDeviceSessionContractTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 31, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void SessionPluginExposesTheAdditiveAsyncContract()
    {
        var methodNames = typeof(IOpenLineOpsDeviceSessionPlugin)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "CloseAsync",
                "GetDiagnosticsAsync",
                "GetHealthAsync",
                "InvokeAsync",
                "OpenAsync",
                "ReadAsync",
                "SubscribeAsync",
                "WriteAsync"
            ],
            methodNames);
        Assert.True(typeof(IOpenLineOpsPlugin).IsAssignableFrom(
            typeof(IOpenLineOpsDeviceSessionPlugin)));
    }

    [Fact]
    public void ExistingCommandPluginAbiRemainsIndependentAndUnchanged()
    {
        var methods = typeof(IOpenLineOpsDeviceCommandPlugin)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var method = Assert.Single(methods);
        Assert.Equal("ExecuteDeviceCommandAsync", method.Name);
        Assert.Equal(
            typeof(ValueTask<PluginDeviceCommandExecutionResult>),
            method.ReturnType);
        Assert.Equal(
            [
                typeof(PluginDeviceCommandExecutionRequest),
                typeof(CancellationToken)
            ],
            method.GetParameters().Select(static parameter => parameter.ParameterType));
        Assert.False(typeof(IOpenLineOpsDeviceSessionPlugin).IsAssignableFrom(
            typeof(IOpenLineOpsDeviceCommandPlugin)));
    }

    [Fact]
    public void CommandEnvelopePreservesExecutionAuthority()
    {
        var deadline = Timestamp.AddMinutes(1);

        var envelope = new PluginDeviceCommandEnvelope(
            "command-0001",
            42,
            deadline,
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            PluginDeviceCommandSafetyClass.Motion);

        Assert.Equal("command-0001", envelope.CommandId);
        Assert.Equal(42, envelope.FencingToken);
        Assert.Equal(deadline, envelope.DeadlineUtc);
        Assert.Equal(PluginDeviceCommandIdempotencyClass.NonIdempotent, envelope.IdempotencyClass);
        Assert.Equal(PluginDeviceCommandSafetyClass.Motion, envelope.SafetyClass);
    }

    [Fact]
    public void CommandEnvelopeRejectsInvalidAuthorityValues()
    {
        Assert.Throws<ArgumentException>(() => new PluginDeviceCommandEnvelope(
            " command-0001 ",
            1,
            Timestamp,
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginDeviceCommandEnvelope(
            "command-0001",
            0,
            Timestamp,
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Normal));
        Assert.Throws<ArgumentException>(() => new PluginDeviceCommandEnvelope(
            "command-0001",
            1,
            Timestamp.ToOffset(TimeSpan.FromHours(8)),
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginDeviceCommandEnvelope(
            "command-0001",
            1,
            Timestamp,
            (PluginDeviceCommandIdempotencyClass)99,
            PluginDeviceCommandSafetyClass.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginDeviceCommandEnvelope(
            "command-0001",
            1,
            Timestamp,
            PluginDeviceCommandIdempotencyClass.Idempotent,
            (PluginDeviceCommandSafetyClass)99));
    }

    [Fact]
    public void DeviceValuesUsePortableCanonicalRepresentations()
    {
        Assert.Equal(
            new PluginDeviceValue(PluginDeviceValueType.Null, string.Empty),
            PluginDeviceValue.Null());
        Assert.Equal("true", PluginDeviceValue.FromBoolean(true).CanonicalValue);
        Assert.Equal("-42", PluginDeviceValue.FromInt64(-42).CanonicalValue);
        Assert.Equal("1.25", PluginDeviceValue.FromDouble(1.25).CanonicalValue);
        Assert.Equal("123.45", PluginDeviceValue.FromDecimal(123.4500m).CanonicalValue);
        Assert.Equal(" operator text ", PluginDeviceValue.FromString(" operator text ").CanonicalValue);
        Assert.Equal(
            Timestamp.ToString("O"),
            PluginDeviceValue.FromDateTimeOffset(Timestamp).CanonicalValue);
        Assert.Equal(
            Convert.ToBase64String([0x01, 0x02, 0x03]),
            PluginDeviceValue.FromBinary([0x01, 0x02, 0x03]).CanonicalValue);
    }

    [Theory]
    [InlineData(PluginDeviceValueType.Null, "null")]
    [InlineData(PluginDeviceValueType.Boolean, "True")]
    [InlineData(PluginDeviceValueType.SignedInteger, "01")]
    [InlineData(PluginDeviceValueType.FloatingPoint, "1.0")]
    [InlineData(PluginDeviceValueType.FixedPoint, "1.00")]
    [InlineData(PluginDeviceValueType.Timestamp, "2026-07-31T08:30:00Z")]
    [InlineData(PluginDeviceValueType.Binary, "***")]
    public void DeviceValuesRejectNonCanonicalWireRepresentations(
        PluginDeviceValueType type,
        string value)
    {
        Assert.Throws<ArgumentException>(() => new PluginDeviceValue(type, value));
    }

    [Fact]
    public void DeviceValuesRejectNonFiniteNumbersAndNonUtcTimestamps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginDeviceValue.FromDouble(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginDeviceValue.FromDouble(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() =>
            PluginDeviceValue.FromDateTimeOffset(Timestamp.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void SessionContractsRequireCanonicalIdentityUtcAndHeartbeatPrecision()
    {
        var session = new PluginDeviceSession(
            "session-1",
            "scanner-1",
            Timestamp,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("scanner-1", session.DeviceInstanceId);
        Assert.Equal(Timestamp, session.OpenedAtUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(500), session.HeartbeatInterval);

        Assert.Throws<ArgumentException>(() => new PluginDeviceSession(
            "session-1",
            " scanner-1 ",
            Timestamp,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new PluginDeviceSession(
            "session-1",
            "scanner-1",
            Timestamp.ToOffset(TimeSpan.FromHours(8)),
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginDeviceSession(
            "session-1",
            "scanner-1",
            Timestamp,
            TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void ReadAndWriteRequestsSnapshotInputsAndRejectAmbiguousTargets()
    {
        var signalIds = new List<string> { "temperature", "pressure" };
        var read = new PluginDeviceSignalReadRequest("session-1", signalIds);
        signalIds[0] = "mutated";

        Assert.Equal(["temperature", "pressure"], read.SignalIds);
        Assert.Throws<ArgumentException>(() =>
            new PluginDeviceSignalReadRequest("session-1", []));
        Assert.Throws<ArgumentException>(() =>
            new PluginDeviceSignalReadRequest("session-1", ["pressure", "pressure"]));

        var writes = new[]
        {
            new PluginDeviceSignalWrite(
                "setpoint",
                PluginDeviceValue.FromDouble(12.5),
                "V")
        };
        var write = new PluginDeviceSignalWriteRequest("session-1", Command(), writes);
        writes[0] = new PluginDeviceSignalWrite("other", PluginDeviceValue.FromBoolean(true));

        Assert.Equal("setpoint", Assert.Single(write.Writes).SignalId);
        Assert.Throws<ArgumentException>(() => new PluginDeviceSignalWriteRequest(
            "session-1",
            Command(),
            [
                new PluginDeviceSignalWrite("setpoint", PluginDeviceValue.FromInt64(1)),
                new PluginDeviceSignalWrite("setpoint", PluginDeviceValue.FromInt64(2))
            ]));
    }

    [Fact]
    public void SignalSampleCarriesTypedValueQualityTimestampsAndSequence()
    {
        var sample = Sample(sequence: 7);

        Assert.Equal("temperature", sample.SignalId);
        Assert.Equal(PluginDeviceValueType.FloatingPoint, sample.Value.Type);
        Assert.Equal("degC", sample.Unit);
        Assert.Equal(PluginDeviceSignalQuality.Good, sample.Quality);
        Assert.Equal(Timestamp, sample.SourceTimestampUtc);
        Assert.Equal(Timestamp.AddMilliseconds(2), sample.ObservedTimestampUtc);
        Assert.Equal(7, sample.Sequence);
        Assert.Equal("OpcUa.Good", sample.QualityCode);

        Assert.Throws<ArgumentOutOfRangeException>(() => Sample(sequence: -1));
        Assert.Throws<ArgumentException>(() => new PluginDeviceSignalSample(
            "temperature",
            PluginDeviceValue.FromDouble(21.5),
            " degC ",
            PluginDeviceSignalQuality.Good,
            Timestamp,
            Timestamp,
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginDeviceSignalSample(
            "temperature",
            PluginDeviceValue.FromDouble(21.5),
            "degC",
            (PluginDeviceSignalQuality)99,
            Timestamp,
            Timestamp,
            0));
    }

    [Fact]
    public void SubscriptionSupportsStableIdentityAndSequenceResume()
    {
        var signalIds = new List<string> { "temperature" };
        var request = new PluginDeviceSignalSubscriptionRequest(
            "session-1",
            "subscription-1",
            signalIds,
            resumeAfterSequence: 10,
            minimumSamplingInterval: TimeSpan.FromMilliseconds(100));
        signalIds[0] = "mutated";

        Assert.Equal("subscription-1", request.SubscriptionId);
        Assert.Equal(["temperature"], request.SignalIds);
        Assert.Equal(10, request.ResumeAfterSequence);
        Assert.Equal(TimeSpan.FromMilliseconds(100), request.MinimumSamplingInterval);

        var update = new PluginDeviceSignalSubscriptionEvent(
            request.SubscriptionId,
            11,
            Sample(sequence: 52));
        Assert.Equal(11, update.Sequence);
        Assert.Equal(52, update.Sample.Sequence);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PluginDeviceSignalSubscriptionRequest(
                "session-1",
                "subscription-1",
                ["temperature"],
                resumeAfterSequence: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PluginDeviceSignalSubscriptionEvent(
                "subscription-1",
                -1,
                Sample()));
    }

    [Fact]
    public void OperationResultRequiresFailureEvidenceForNonCompletedOutcome()
    {
        var completed = PluginDeviceOperationResult.Completed(Timestamp, """{"ok":true}""");
        var failed = PluginDeviceOperationResult.Failed(Timestamp, "Device disconnected.");

        Assert.True(completed.Succeeded);
        Assert.Null(completed.FailureReason);
        Assert.False(failed.Succeeded);
        Assert.Equal("Device disconnected.", failed.FailureReason);

        Assert.Throws<ArgumentException>(() => new PluginDeviceOperationResult(
            PluginDeviceOperationOutcome.Completed,
            Timestamp,
            null,
            "unexpected"));
        Assert.Throws<ArgumentException>(() => new PluginDeviceOperationResult(
            PluginDeviceOperationOutcome.Failed,
            Timestamp,
            null,
            null));
    }

    [Fact]
    public void UnknownCompletionIsAnAdditiveRecoveryRequiredContractState()
    {
        var result = PluginDeviceOperationResult.UnknownCompletion(
            Timestamp,
            "Physical command acknowledgement was not received.");
        var json = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<PluginDeviceOperationResult>(json);
        var legacyResult = JsonSerializer.Deserialize<PluginDeviceOperationResult>(
            $$"""
            {
              "Outcome": 2,
              "CompletedAtUtc": "{{Timestamp:O}}",
              "OutputPayload": null,
              "FailureReason": "Legacy failure."
            }
            """);

        Assert.Equal(PluginDeviceOperationOutcome.Failed, result.Outcome);
        Assert.Equal(PluginDeviceOperationCompletionState.Unknown, result.CompletionState);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(result, roundTrip);
        Assert.NotNull(legacyResult);
        Assert.Equal(
            PluginDeviceOperationCompletionState.Known,
            legacyResult.CompletionState);
        Assert.False(legacyResult.RecoveryRequired);
    }

    [Fact]
    public void HealthAndDiagnosticsAreUtcSnapshotsWithDefensiveAttributes()
    {
        var health = new PluginDeviceHealthSnapshot(
            "session-1",
            PluginDeviceHealthStatus.Healthy,
            Timestamp,
            Timestamp.AddSeconds(-1),
            "Heartbeat current.");

        Assert.Equal(PluginDeviceHealthStatus.Healthy, health.Status);
        Assert.Equal(Timestamp.AddSeconds(-1), health.LastHeartbeatAtUtc);
        Assert.Throws<ArgumentException>(() => new PluginDeviceHealthSnapshot(
            "session-1",
            PluginDeviceHealthStatus.Healthy,
            Timestamp,
            Timestamp.AddSeconds(1)));

        var attributes = new Dictionary<string, string?>
        {
            ["endpoint"] = "opc.tcp://controller"
        };
        var entry = new PluginDeviceDiagnosticEntry(
            "Transport.Reconnected",
            PluginDeviceDiagnosticSeverity.Information,
            "Transport reconnected.",
            Timestamp,
            attributes);
        attributes["endpoint"] = "mutated";

        var diagnostics = new PluginDeviceDiagnosticsSnapshot(
            "session-1",
            Timestamp,
            [entry]);

        Assert.Equal("opc.tcp://controller", entry.Attributes["endpoint"]);
        Assert.Same(entry, Assert.Single(diagnostics.Entries));
        Assert.Throws<ArgumentException>(() => new PluginDeviceDiagnosticEntry(
            " Transport.Reconnected ",
            PluginDeviceDiagnosticSeverity.Information,
            "Transport reconnected.",
            Timestamp));
    }

    private static PluginDeviceCommandEnvelope Command() =>
        new(
            "command-0001",
            42,
            Timestamp.AddMinutes(1),
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Normal);

    private static PluginDeviceSignalSample Sample(long sequence = 0) =>
        new(
            "temperature",
            PluginDeviceValue.FromDouble(21.5),
            "degC",
            PluginDeviceSignalQuality.Good,
            Timestamp,
            Timestamp.AddMilliseconds(2),
            sequence,
            "OpcUa.Good");
}
