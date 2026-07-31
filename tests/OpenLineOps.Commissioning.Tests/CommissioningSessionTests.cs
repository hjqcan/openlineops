using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Commissioning.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Commissioning.Tests;

public sealed class CommissioningSessionTests
{
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartRequiresExclusiveBoundedLeaseAndWritesAuditFact()
    {
        var session = CreateSession();

        Assert.Equal(CommissioningSessionStatus.Active, session.Status);
        Assert.Equal("lease.station-a", session.LeaseId);
        Assert.Equal(41, session.FencingToken);
        Assert.Equal(
            CommissioningAuditKind.SessionStarted,
            Assert.Single(session.AuditTrail).Kind);
    }

    [Fact]
    public void StartRejectsDurationLongerThanMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CommissioningSession.Start(
            new CommissioningSessionId("commissioning.too-long"),
            "station-a",
            "engineer-a",
            "CommissioningEngineer",
            "lease.station-a",
            41,
            StartedAtUtc,
            StartedAtUtc.Add(CommissioningSession.MaximumDuration).AddMilliseconds(1),
            [CommissioningCapability.DeviceDiagnostics]));
    }

    [Fact]
    public void PhysicalManualCommandRequiresWhitelistAndNeverAllowsSafetyAction()
    {
        var session = CreateSession();

        var notWhitelisted = session.AuthorizeManualCommand(
            "engineer-a",
            "command-1",
            StationMode.Maintenance,
            CommissioningActionSafetyClass.Normal,
            debugActionWhitelisted: false,
            StartedAtUtc.AddMinutes(1));
        var safetyCritical = session.AuthorizeManualCommand(
            "engineer-a",
            "command-2",
            StationMode.Simulation,
            CommissioningActionSafetyClass.SafetyCritical,
            debugActionWhitelisted: true,
            StartedAtUtc.AddMinutes(2));
        var accepted = session.AuthorizeManualCommand(
            "engineer-a",
            "command-3",
            StationMode.Maintenance,
            CommissioningActionSafetyClass.Motion,
            debugActionWhitelisted: true,
            StartedAtUtc.AddMinutes(3));

        Assert.False(notWhitelisted.Succeeded);
        Assert.False(safetyCritical.Succeeded);
        Assert.True(accepted.Succeeded);
        Assert.Equal(
            CommissioningAuditKind.ManualCommandAuthorized,
            session.AuditTrail[^1].Kind);
    }

    [Fact]
    public void AutomaticModeNeverAllowsManualCommand()
    {
        var session = CreateSession();

        var result = session.AuthorizeManualCommand(
            "engineer-a",
            "command-auto",
            StationMode.Automatic,
            CommissioningActionSafetyClass.Diagnostic,
            debugActionWhitelisted: true,
            StartedAtUtc.AddMinutes(1));

        Assert.False(result.Succeeded);
        Assert.Equal("Commissioning.AutomaticModeForbidden", result.Code);
    }

    [Fact]
    public void BreakpointRequiresSimulationOrWhitelistedDebugAction()
    {
        var session = CreateSession();

        var rejected = session.SetBreakpoint(
            "engineer-a",
            "node-1",
            StationMode.Setup,
            debugActionWhitelisted: false,
            StartedAtUtc.AddMinutes(1));
        var simulation = session.SetBreakpoint(
            "engineer-a",
            "node-2",
            StationMode.Simulation,
            debugActionWhitelisted: false,
            StartedAtUtc.AddMinutes(2));

        Assert.False(rejected.Succeeded);
        Assert.True(simulation.Succeeded);
    }

    [Fact]
    public void IdempotentInterruptedActionCanReplayWithoutLeavingActiveState()
    {
        var session = CreateSession();

        var result = session.RecoverInterruptedAction(
            "action-read",
            CommissioningActionIdempotencyClass.Idempotent,
            StartedAtUtc.AddMinutes(1));

        Assert.True(result.Succeeded);
        Assert.Equal(CommissioningSessionStatus.Active, session.Status);
        Assert.Equal(
            CommissioningAuditKind.AutomaticReplayAuthorized,
            session.AuditTrail[^1].Kind);
    }

    [Theory]
    [InlineData(CommissioningActionIdempotencyClass.Conditional)]
    [InlineData(CommissioningActionIdempotencyClass.NonIdempotent)]
    public void UnsafeInterruptedActionRequiresAuthorizedDisposition(
        CommissioningActionIdempotencyClass idempotencyClass)
    {
        var session = CreateSession();

        session.RecoverInterruptedAction(
            "action-clamp",
            idempotencyClass,
            StartedAtUtc.AddMinutes(1));
        var replay = session.ResolveRecovery(
            "engineer-a",
            CommissioningRecoveryDisposition.Replay,
            StartedAtUtc.AddMinutes(2));
        var skip = session.ResolveRecovery(
            "engineer-a",
            CommissioningRecoveryDisposition.Skip,
            StartedAtUtc.AddMinutes(3));

        Assert.Equal(CommissioningSessionStatus.Active, session.Status);
        Assert.False(replay.Succeeded);
        Assert.True(skip.Succeeded);
    }

    [Fact]
    public void DifferentActorCannotUseSession()
    {
        var session = CreateSession();

        var result = session.RecordDiagnosticAccess(
            "engineer-other",
            "device-a",
            StartedAtUtc.AddMinutes(1));

        Assert.False(result.Succeeded);
        Assert.Equal("Commissioning.ActorNotAuthorized", result.Code);
    }

    [Fact]
    public void LeaseRenewalRequiresIncreasingFenceAndMaximumDuration()
    {
        var session = CreateSession();

        var stale = session.RenewLease(
            "engineer-a",
            41,
            StartedAtUtc.AddHours(3),
            StartedAtUtc.AddMinutes(1));
        var accepted = session.RenewLease(
            "engineer-a",
            42,
            StartedAtUtc.AddHours(4),
            StartedAtUtc.AddMinutes(2));

        Assert.False(stale.Succeeded);
        Assert.True(accepted.Succeeded);
        Assert.Equal(42, session.FencingToken);
        Assert.Equal(42, session.AuditTrail[^1].FencingToken);
    }

    [Fact]
    public void ExpiredSessionFailsClosedAndRecordsExpiration()
    {
        var session = CreateSession();

        var result = session.RecordDiagnosticAccess(
            "engineer-a",
            "device-a",
            StartedAtUtc.AddHours(2).AddSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Equal(CommissioningSessionStatus.Expired, session.Status);
        Assert.Equal(CommissioningAuditKind.SessionExpired, session.AuditTrail[^1].Kind);
    }

    [Fact]
    public void ExpiryBoundaryFailsClosed()
    {
        var session = CreateSession();

        var result = session.RecordDiagnosticAccess(
            "engineer-a",
            "device-a",
            session.ExpiresAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal(CommissioningSessionStatus.Expired, session.Status);
    }

    [Fact]
    public void SnapshotRoundTripPreservesAppendOnlyAuditAndRecoveryState()
    {
        var session = CreateSession();
        session.RecoverInterruptedAction(
            "action-clamp",
            CommissioningActionIdempotencyClass.NonIdempotent,
            StartedAtUtc.AddMinutes(1));

        var restored = CommissioningSession.Restore(session.ToSnapshot());

        Assert.Equal(CommissioningSessionStatus.RecoveryRequired, restored.Status);
        Assert.Equal(session.RecoveryReason, restored.RecoveryReason);
        Assert.Equal(session.AuditTrail, restored.AuditTrail);
        Assert.Equal(session.Capabilities, restored.Capabilities);
    }

    [Fact]
    public void RecoveryCannotBypassExpiredExclusiveLease()
    {
        var session = CreateSession();

        var result = session.RecoverInterruptedAction(
            "action-read",
            CommissioningActionIdempotencyClass.Idempotent,
            session.ExpiresAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal(CommissioningSessionStatus.Expired, session.Status);
        Assert.Equal(CommissioningAuditKind.SessionExpired, session.AuditTrail[^1].Kind);
    }

    [Fact]
    public void DifferentActorCannotAbortSession()
    {
        var session = CreateSession();

        var result = session.Abort(
            "engineer-other",
            "operator request",
            StartedAtUtc.AddMinutes(1));

        Assert.False(result.Succeeded);
        Assert.Equal(CommissioningSessionStatus.Active, session.Status);
    }

    private static CommissioningSession CreateSession() =>
        CommissioningSession.Start(
            new CommissioningSessionId($"commissioning.{Guid.NewGuid():N}"),
            "station-a",
            "engineer-a",
            "CommissioningEngineer",
            "lease.station-a",
            41,
            StartedAtUtc,
            StartedAtUtc.AddHours(2),
            Enum.GetValues<CommissioningCapability>());
}
